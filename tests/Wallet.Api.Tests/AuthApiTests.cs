using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Wallet.Api.Tests;

public class AuthApiTests : IClassFixture<WalletApiFactory>
{
    private readonly WalletApiFactory _factory;
    private readonly ApiTestClient _api;

    public AuthApiTests(WalletApiFactory factory)
    {
        _factory = factory;
        _api = new ApiTestClient(factory);
    }

    [Fact]
    public async Task PostWallet_WithoutApiKey_Returns401()
    {
        var response = await _api.PostJsonAsync(
            "/api/wallets",
            new { currency = "GHS" },
            apiKey: null,
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostWallet_WithInvalidApiKey_Returns401()
    {
        var response = await _api.PostJsonAsync(
            "/api/wallets",
            new { currency = "GHS" },
            "not-a-real-key",
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetWallet_OtherClient_Returns404()
    {
        var (wallet, _, _) = await _api.CreateWalletAsync("owner");
        var otherKey = _api.CreateKey(ApiTestClient.NewClientId("other"));

        var response = await _api.GetAsync($"/api/wallets/{wallet}", otherKey);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetEntries_OtherClient_Returns404()
    {
        var (wallet, _, _) = await _api.CreateWalletAsync("owner");
        var otherKey = _api.CreateKey(ApiTestClient.NewClientId("other"));

        var response = await _api.GetAsync($"/api/wallets/{wallet}/entries", otherKey);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostTransfer_FromOtherClientWallet_Returns403()
    {
        var (alice, _, _) = await _api.CreateWalletAsync("alice");
        var (bob, _, bobKey) = await _api.CreateWalletAsync("bob");

        var response = await _api.PostJsonAsync("/api/transfers", new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 100
        }, bobKey, Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostTransfer_FromSystemWallet_Returns403()
    {
        var (wallet, apiKey) = await CreateFundedWalletAsync("system-source");
        var depositResponse = await _api.DepositAsync(wallet, 100, apiKey);
        var deposit = await depositResponse.Content.ReadFromJsonAsync<ApiTestClient.TransferDto>(
            ApiTestClient.JsonOptions);

        var response = await _api.PostJsonAsync("/api/transfers", new
        {
            sourceWalletId = deposit!.SourceWalletId,
            destinationWalletId = wallet,
            currency = "GHS",
            amount = 1000
        }, apiKey, Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostReverse_ByOriginalSource_Returns403()
    {
        var (alice, aliceKey) = await CreateFundedWalletAsync("alice");
        var (bob, bobKey) = await CreateFundedWalletAsync("bob");

        var transferResponse = await _api.PostJsonAsync("/api/transfers", new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 100
        }, aliceKey, Guid.NewGuid().ToString());
        transferResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var transfer = await transferResponse.Content.ReadFromJsonAsync<ApiTestClient.TransferDto>(
            ApiTestClient.JsonOptions);

        var response = await _api.PostJsonAsync(
            $"/api/transfers/{transfer!.Id}/reverse",
            new { },
            aliceKey,
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetTransfer_Deposit_Returns200ForOwner()
    {
        var (wallet, apiKey) = await CreateFundedWalletAsync("deposit-owner");

        var deposit = await _api.DepositAsync(wallet, 100, apiKey);
        var transfer = await deposit.Content.ReadFromJsonAsync<ApiTestClient.TransferDto>(
            ApiTestClient.JsonOptions);

        var response = await _api.GetAsync($"/api/transfers/{transfer!.Id}", apiKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetTransfer_IsVisibleToSourceAndDestinationOnly()
    {
        var (alice, aliceKey) = await CreateFundedWalletAsync("visible-alice");
        var (bob, bobKey) = await CreateFundedWalletAsync("visible-bob");

        var transferResponse = await _api.PostJsonAsync("/api/transfers", new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 100
        }, aliceKey, Guid.NewGuid().ToString());
        transferResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var transfer = await transferResponse.Content.ReadFromJsonAsync<ApiTestClient.TransferDto>(
            ApiTestClient.JsonOptions);
        var otherKey = _api.CreateKey(ApiTestClient.NewClientId("visible-other"));

        (await _api.GetAsync($"/api/transfers/{transfer!.Id}", aliceKey)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await _api.GetAsync($"/api/transfers/{transfer.Id}", bobKey)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await _api.GetAsync($"/api/transfers/{transfer.Id}", otherKey)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostReverse_ByDestination_Returns201()
    {
        var (alice, aliceKey) = await CreateFundedWalletAsync("reverse-alice");
        var (bob, bobKey) = await CreateFundedWalletAsync("reverse-bob");

        var transferResponse = await _api.PostJsonAsync("/api/transfers", new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 100
        }, aliceKey, Guid.NewGuid().ToString());
        transferResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var transfer = await transferResponse.Content.ReadFromJsonAsync<ApiTestClient.TransferDto>(
            ApiTestClient.JsonOptions);

        var response = await _api.PostJsonAsync(
            $"/api/transfers/{transfer!.Id}/reverse",
            new { },
            bobKey,
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task PostDeposit_RateLimiterUsesAuthenticatedClientId()
    {
        var (wallet, clientId, apiKey) = await _api.CreateWalletAsync("rate-limit");

        _factory.RateLimiter.Clear();
        await _api.DepositAsync(wallet, 100, apiKey);

        _factory.RateLimiter.Calls.Should().ContainSingle(call =>
            call.ClientId == clientId && call.Route == "/api/wallets/{id}/deposits");
    }

    private async Task<(Guid WalletId, string ApiKey)> CreateFundedWalletAsync(string prefix)
    {
        var (walletId, _, apiKey) = await _api.CreateWalletAsync(prefix);
        await _api.DepositAsync(walletId, 10000, apiKey);
        return (walletId, apiKey);
    }
}
