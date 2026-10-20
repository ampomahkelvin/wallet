using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wallet.Infrastructure.Persistence;
using Xunit;

namespace Wallet.Api.Tests;

public class ObservabilityApiTests : IClassFixture<WalletApiFactory>
{
    private readonly WalletApiFactory _factory;
    private readonly HttpClient _client;

    public ObservabilityApiTests(WalletApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetMetrics_ReturnsPrometheusMetrics()
    {
        await SeedTransferAsync();

        var response = await _client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("wallet_transfers_total");
    }

    [Fact]
    public async Task GetHealthDeep_ReturnsComponentStatus()
    {
        var response = await _client.GetAsync("/health/deep");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("database");
        body.Should().Contain("broker");
        body.Should().Contain("redis");
    }

    [Fact]
    public async Task GetAdminReconcile_WithNoViolations_ReturnsOk()
    {
        await SeedTransferAsync();

        var response = await _client.GetAsync("/admin/reconcile");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReconcileResponse>();
        body!.Violations.Should().BeEmpty();
        body.Status.Should().Be("ok");
    }

    [Fact]
    public async Task GetAdminReconcile_DetectsCorruptedBalance()
    {
        var walletId = await CreateWalletAsync();
        await DepositAsync(walletId, 1000);

        using (var db = _factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE wallets SET ""Balance"" = ""Balance"" - 1 WHERE ""Id"" = {walletId}");
        }

        var response = await _client.GetAsync("/admin/reconcile");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReconcileResponse>();
        body!.Violations.Should().NotBeEmpty();
        body.Status.Should().Be("violations_detected");

        using (var db = _factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE wallets SET ""Balance"" = ""Balance"" + 1 WHERE ""Id"" = {walletId}");
        }
    }

    private async Task SeedTransferAsync()
    {
        var alice = await CreateWalletAsync();
        var bob = await CreateWalletAsync();
        await DepositAsync(alice, 1000);
        await DepositAsync(bob, 1000);
        await TransferAsync(alice, bob, 100);
    }

    private async Task<Guid> CreateWalletAsync()
    {
        var response = await PostJsonAsync("/api/wallets", new
        {
            ownerId = $"obs-{Guid.NewGuid():N}",
            currency = "GHS"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<WalletDto>();
        return body!.Id;
    }

    private async Task DepositAsync(Guid walletId, long amount)
    {
        var response = await PostJsonAsync($"/api/wallets/{walletId}/deposits", new { amount });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task TransferAsync(Guid source, Guid destination, long amount)
    {
        var response = await PostJsonAsync("/api/transfers", new
        {
            sourceWalletId = source,
            destinationWalletId = destination,
            currency = "GHS",
            amount
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-Client-Id", "obs-test");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await _client.SendAsync(request);
    }

    private sealed record WalletDto(Guid Id, string OwnerId, string Currency, long Balance);

    private sealed record ReconcileResponse(List<Guid> Violations, string Status);
}