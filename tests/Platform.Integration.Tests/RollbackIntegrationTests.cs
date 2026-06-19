using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using Platform.Api.Features.Promotions.Models;
using Platform.Api.Infrastructure.Persistence;

namespace Platform.Integration.Tests;

/// <summary>
/// End-to-end tests for the rollback feature: feature-flag + per-product gating, the two selection
/// modes (manual + align-all-except), the "version must have run here" safety rule, auto-approve +
/// completion via deploy event, and the stale-promotion invariant (drift block + reactivation).
/// </summary>
public class RollbackIntegrationTests : IClassFixture<RollbackIntegrationTests.RollbackFactory>, IDisposable
{
    private readonly RollbackFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _apiKey;

    private const string Product = "acme";

    public RollbackIntegrationTests(RollbackFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAdminClient();
        _apiKey = factory.CreateClient();
        _apiKey.DefaultRequestHeaders.Add("X-Api-Key", RollbackFactory.TestApiKey);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _apiKey.Dispose();
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WhenProductNotEnrolled_Returns400()
    {
        await EnableRollbacksAsync();
        // Deliberately do NOT enroll a fresh product.
        var product = $"unenrolled-{Guid.NewGuid():N}";
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));

        var resp = await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product,
            targetEnv = "staging",
            mode = "manual",
            items = new[] { new { service = "api", toVersion = "1.0" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ManualRollback_AutoApproves_AndCompletesOnDeployEvent()
    {
        var product = await FreshEnrolledProductAsync();
        // History: staging ran 1.0 then 1.1 (current). No promotion policy → rollback auto-approves.
        await IngestAsync(product, "api", "staging", "1.0", Hours(-2));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-1));

        var create = await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product,
            targetEnv = "staging",
            mode = "manual",
            reason = "rolling api back to 1.0",
            items = new[] { new { service = "api", toVersion = "1.0" } },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await Body(create);
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("Approved", created.GetProperty("status").GetString()); // no approver group → auto
        var item = created.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("api", item.GetProperty("service").GetString());
        Assert.Equal("1.1", item.GetProperty("fromVersion").GetString());
        Assert.Equal("1.0", item.GetProperty("toVersion").GetString());

        // The operator performs the rollback → a deploy event for 1.0 lands in staging.
        await IngestAsync(product, "api", "staging", "1.0", Hours(1), isRollback: true);

        var detail = await Body(await _admin.GetAsync($"/api/rollbacks/{id}"));
        Assert.Equal("RolledBack", detail.GetProperty("status").GetString());
        Assert.Equal("RolledBack", detail.GetProperty("items").EnumerateArray().Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task SafetyRule_RejectsVersionThatNeverRanInEnv()
    {
        var product = await FreshEnrolledProductAsync();
        await IngestAsync(product, "api", "staging", "2.0", Hours(-1)); // only 2.0 ever ran

        var resp = await _admin.PostAsJsonAsync("/api/rollbacks", new
        {
            product,
            targetEnv = "staging",
            mode = "manual",
            items = new[] { new { service = "api", toVersion = "1.0" } }, // 1.0 never ran here
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task AlignAllExcept_PreviewAndCreate()
    {
        var product = await FreshEnrolledProductAsync();
        // staging history: svc-a 1→2, svc-b 1→2, svc-c 2. prod (reference): a=1, b=1, c=2.
        await IngestAsync(product, "svc-a", "staging", "1.0", Hours(-3));
        await IngestAsync(product, "svc-a", "staging", "2.0", Hours(-2));
        await IngestAsync(product, "svc-b", "staging", "1.0", Hours(-3));
        await IngestAsync(product, "svc-b", "staging", "2.0", Hours(-2));
        await IngestAsync(product, "svc-c", "staging", "2.0", Hours(-2));
        await IngestAsync(product, "svc-a", "prod", "1.0", Hours(-1));
        await IngestAsync(product, "svc-b", "prod", "1.0", Hours(-1));
        await IngestAsync(product, "svc-c", "prod", "2.0", Hours(-1));

        var body = new
        {
            product,
            targetEnv = "staging",
            mode = "align",
            referenceEnv = "prod",
            exclude = new[] { "svc-b" },
        };

        var preview = await Body(await _admin.PostAsJsonAsync("/api/rollbacks/preview", body));
        var items = preview.GetProperty("items").EnumerateArray().ToList();
        var a = items.Single(i => i.GetProperty("service").GetString() == "svc-a");
        var b = items.Single(i => i.GetProperty("service").GetString() == "svc-b");
        var c = items.Single(i => i.GetProperty("service").GetString() == "svc-c");
        Assert.True(a.GetProperty("eligible").GetBoolean());      // 2.0 → 1.0, 1.0 ran in staging
        Assert.Equal("1.0", a.GetProperty("toVersion").GetString());
        Assert.False(b.GetProperty("eligible").GetBoolean());     // excluded
        Assert.Equal("excluded", b.GetProperty("skipReason").GetString());
        Assert.False(c.GetProperty("eligible").GetBoolean());     // already matching (2.0 == 2.0)

        var create = await _admin.PostAsJsonAsync("/api/rollbacks", body);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await Body(create);
        var created_items = created.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(created_items); // only svc-a
        Assert.Equal("svc-a", created_items[0].GetProperty("service").GetString());
    }

    [Fact]
    public async Task StalePromotion_BlockedAfterRollback_ThenReactivatedOnRedeploy()
    {
        var product = $"promo-{Guid.NewGuid():N}";
        await SeedPromotionTopologyAndPolicyAsync(product);
        await EnableRollbacksAsync();

        // 1.1 lands in staging → a Pending promotion candidate (staging → prod) is created.
        await IngestAsync(product, "api", "staging", "1.0", Hours(-3));
        await IngestAsync(product, "api", "staging", "1.1", Hours(-2));
        var candidateId = await GetPendingCandidateIdAsync(product, "prod");
        Assert.NotNull(candidateId);

        // Roll staging back to 1.0 (flagged rollback so it doesn't itself spawn a candidate).
        await IngestAsync(product, "api", "staging", "1.0", Hours(-1), isRollback: true);

        // Approving now must NOT promote: the source env no longer runs 1.1 (drifted).
        var approve = await _admin.PostAsJsonAsync($"/api/promotions/{candidateId}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var afterApprove = await Body(await _admin.GetAsync($"/api/promotions/{candidateId}"));
        Assert.Equal("Pending", afterApprove.GetProperty("candidate").GetProperty("status").GetString()); // blocked by drift

        // Redeploy 1.1 to staging → reactivates the same candidate; gate now satisfied → Approved.
        await IngestAsync(product, "api", "staging", "1.1", Hours(1));
        var afterRedeploy = await Body(await _admin.GetAsync($"/api/promotions/{candidateId}"));
        Assert.Equal("Approved", afterRedeploy.GetProperty("candidate").GetProperty("status").GetString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task EnableRollbacksAsync()
    {
        (await _admin.PutAsJsonAsync("/api/features/features.rollbacks", new { enabled = true })).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync("/api/features/features.promotions", new { enabled = true })).EnsureSuccessStatusCode();
    }

    private async Task<string> FreshEnrolledProductAsync()
    {
        await EnableRollbacksAsync();
        var product = $"acme-{Guid.NewGuid():N}";
        var enabled = await GetEnabledProductsAsync();
        enabled.Add(product);
        (await _admin.PutAsJsonAsync("/api/rollbacks/admin/enabled-products", new { products = enabled }))
            .EnsureSuccessStatusCode();
        return product;
    }

    private async Task<List<string>> GetEnabledProductsAsync()
    {
        var body = await Body(await _admin.GetAsync("/api/rollbacks/admin/enabled-products"));
        return body.GetProperty("products").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    private async Task IngestAsync(string product, string service, string env, string version,
        DateTimeOffset deployedAt, bool isRollback = false)
    {
        var resp = await _apiKey.PostAsJsonAsync("/api/deployments/events", new
        {
            product, service, environment = env, version,
            source = "rollback-test", deployedAt, status = "succeeded", isRollback,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task<string?> GetPendingCandidateIdAsync(string product, string targetEnv)
    {
        var body = await Body(await _admin.GetAsync(
            $"/api/promotions/?product={product}&targetEnv={targetEnv}&status=Pending"));
        var arr = body.GetProperty("candidates").EnumerateArray().ToList();
        return arr.Count == 0 ? null : arr[0].GetProperty("id").GetString();
    }

    // Seeds a staging→prod topology and a product-default promotion policy (with an approver group
    // so candidates are born Pending) directly via the DbContext.
    private async Task SeedPromotionTopologyAndPolicyAsync(string product)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        if (!await db.PlatformSettings.AnyAsync(s => s.Key == "promotions.topology"))
        {
            db.PlatformSettings.Add(new Platform.Api.Infrastructure.Features.PlatformSetting
            {
                Key = "promotions.topology",
                Value = "{\"environments\":[\"staging\",\"prod\"],\"edges\":[{\"from\":\"staging\",\"to\":\"prod\"}]}",
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "test",
            });
        }
        db.PromotionPolicies.Add(new PromotionPolicy
        {
            Id = Guid.NewGuid(),
            Product = product,
            Service = null,
            TargetEnv = "prod",
            ApproverGroup = "release-managers",
            Strategy = PromotionStrategy.Any,
            Gate = PromotionGate.PromotionOnly,
        });
        await db.SaveChangesAsync();
    }

    private static DateTimeOffset Hours(double h) => DateTimeOffset.UtcNow.AddHours(h);

    private static async Task<JsonElement> Body(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonDocument.ParseAsync(stream)).RootElement;
    }

    // ── Factory ───────────────────────────────────────────────────────────

    public class RollbackFactory : TestFactory
    {
        public const string TestApiKey = "rollback-test-api-key-13579";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Deployments:ApiKeys:0:Name", "rollback-integration-test");
            builder.UseSetting("Deployments:ApiKeys:0:Key", TestApiKey);
            builder.UseSetting("Deployments:ApiKeys:0:Roles:0", "InfraPortal.Admin");
        }
    }
}
