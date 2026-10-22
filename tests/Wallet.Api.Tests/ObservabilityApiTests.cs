using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wallet.Infrastructure.Persistence;
using Xunit;

namespace Wallet.Api.Tests;

public class ObservabilityApiTests : IClassFixture<WalletApiFactory>
{
    private readonly WalletApiFactory _factory;
    private readonly ApiTestClient _api;
    private readonly string _apiKey;

    public ObservabilityApiTests(WalletApiFactory factory)
    {
        _factory = factory;
        _api = new ApiTestClient(factory);
        _apiKey = _api.CreateKey(ApiTestClient.NewClientId("obs"));
    }

    [Fact]
    public async Task GetMetrics_ReturnsPrometheusMetrics()
    {
        await SeedTransferAsync();

        var response = await _api.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("wallet_transfers_total");
    }

    [Fact]
    public async Task GetHealthDeep_ReturnsComponentStatus()
    {
        var response = await _api.GetAsync("/health/deep");

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

        var response = await _api.GetAsync("/admin/reconcile", _apiKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReconcileResponse>();
        body!.Violations.Should().BeEmpty();
        body.Status.Should().Be("ok");
    }

    [Fact]
    public async Task GetAdminReconcile_DetectsCorruptedBalance()
    {
        var (walletId, walletKey) = await CreateWalletAsync();
        await DepositAsync(walletId, 1000, walletKey);

        using (var db = _factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE wallets SET ""Balance"" = ""Balance"" - 1 WHERE ""Id"" = {walletId}");
        }

        var response = await _api.GetAsync("/admin/reconcile", _apiKey);
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

    [Fact]
    public async Task GetAdminReconcile_WithoutApiKey_Returns401()
    {
        var response = await _api.GetAsync("/admin/reconcile");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task SeedTransferAsync()
    {
        var (alice, aliceKey) = await CreateWalletAsync();
        var (bob, bobKey) = await CreateWalletAsync();
        await DepositAsync(alice, 1000, aliceKey);
        await DepositAsync(bob, 1000, bobKey);
        await TransferAsync(alice, bob, aliceKey);
    }

    private async Task<(Guid WalletId, string ApiKey)> CreateWalletAsync()
    {
        var (walletId, _, apiKey) = await _api.CreateWalletAsync("obs");
        return (walletId, apiKey);
    }

    private Task DepositAsync(Guid walletId, long amount, string apiKey)
        => _api.DepositAsync(walletId, amount, apiKey);

    private Task<HttpResponseMessage> TransferAsync(Guid source, Guid destination, string sourceApiKey)
        => _api.PostJsonAsync("/api/transfers", new
        {
            sourceWalletId = source,
            destinationWalletId = destination,
            currency = "GHS",
            amount = 100
        }, sourceApiKey, Guid.NewGuid().ToString());

    private sealed record ReconcileResponse(List<Guid> Violations, string Status);
}
