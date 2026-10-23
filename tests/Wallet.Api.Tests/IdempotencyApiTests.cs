using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Wallet.Infrastructure.Persistence;
using Xunit;

namespace Wallet.Api.Tests;

public class IdempotencyApiTests : IClassFixture<WalletApiFactory>
{
    private readonly WalletApiFactory _factory;
    private readonly ApiTestClient _api;

    public IdempotencyApiTests(WalletApiFactory factory)
    {
        _factory = factory;
        _api = new ApiTestClient(factory);
    }

    [Fact]
    public async Task PostWallet_SameKeyTwice_ReplaysAndCreatesOneWallet()
    {
        var clientId = ApiTestClient.NewClientId("user");
        var apiKey = _api.CreateKey(clientId);
        var key = Guid.NewGuid().ToString();
        var payload = new { currency = "GHS" };

        var first = await _api.PostJsonAsync("/api/wallets", payload, apiKey, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Contains("Idempotent-Replayed").Should().BeFalse();
        var firstBody = await first.Content.ReadFromJsonAsync<ApiTestClient.WalletDto>(ApiTestClient.JsonOptions);

        var second = await _api.PostJsonAsync("/api/wallets", payload, apiKey, key);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.Should().Contain(h => h.Key.Equals("Idempotent-Replayed", StringComparison.OrdinalIgnoreCase));
        var secondBody = await second.Content.ReadFromJsonAsync<ApiTestClient.WalletDto>(ApiTestClient.JsonOptions);

        secondBody!.Id.Should().Be(firstBody!.Id);
        secondBody.OwnerId.Should().Be(clientId);

        using var db = _factory.CreateDbContext();
        (await db.Wallets.CountAsync(w => w.OwnerId == clientId && w.Currency == "GHS")).Should().Be(1);
    }

    [Fact]
    public async Task PostTransfer_50ParallelSameKey_CreatesOneTransferAndOneEvent()
    {
        var (alice, aliceClientId, aliceKey) = await CreateFundedWalletAsync("alice");
        var (bob, _, _) = await CreateFundedWalletAsync("bob");

        var key = Guid.NewGuid().ToString();
        var payload = new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 100
        };

        var responses = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => _api.PostJsonAsync("/api/transfers", payload, aliceKey, key)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);

        var ids = await Task.WhenAll(responses
            .Select(r => r.Content.ReadFromJsonAsync<ApiTestClient.TransferDto>(ApiTestClient.JsonOptions)));
        ids.Select(t => t!.Id).Distinct().Should().HaveCount(1);
        var transferId = ids[0]!.Id;

        using var db = _factory.CreateDbContext();
        (await db.IdempotencyKeys.CountAsync(k => k.ClientId == aliceClientId && k.Key == key)).Should().Be(1);
        (await db.Transfers.CountAsync(t => t.Id == transferId)).Should().Be(1);
        (await db.OutboxEvents.CountAsync(e => e.TransferId == transferId)).Should().Be(1);
    }

    [Fact]
    public async Task PostTransfer_SameKeyDifferentPayload_Returns422()
    {
        var (alice, _, aliceKey) = await CreateFundedWalletAsync("alice");
        var (bob, _, _) = await CreateFundedWalletAsync("bob");

        var key = Guid.NewGuid().ToString();

        var first = await _api.PostJsonAsync("/api/transfers", new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 100
        }, aliceKey, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await _api.PostJsonAsync("/api/transfers", new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 200
        }, aliceKey, key);
        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostWallet_MissingIdempotencyKey_Returns400()
    {
        var apiKey = _api.CreateKey(ApiTestClient.NewClientId("missing-key"));
        var response = await _api.PostJsonAsync(
            "/api/wallets",
            new { currency = "GHS" },
            apiKey,
            idempotencyKey: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetWallet_Unknown_Returns404()
    {
        var apiKey = _api.CreateKey(ApiTestClient.NewClientId("unknown"));
        var response = await _api.GetAsync($"/api/wallets/{Guid.NewGuid()}", apiKey);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostTransfer_InsufficientFunds_Returns409()
    {
        var (alice, _, aliceKey) = await CreateWalletAsync("alice");
        var (bob, _, _) = await CreateWalletAsync("bob");

        var response = await _api.PostJsonAsync("/api/transfers", new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 100
        }, aliceKey, Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task GetEntries_LimitTooLarge_Returns422()
    {
        var (wallet, _, apiKey) = await CreateWalletAsync("entries-user");
        var response = await _api.GetAsync($"/api/wallets/{wallet}/entries?limit=500", apiKey);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    private Task<(Guid WalletId, string ClientId, string ApiKey)> CreateWalletAsync(string prefix)
        => _api.CreateWalletAsync(prefix);

    private async Task<(Guid WalletId, string ClientId, string ApiKey)> CreateFundedWalletAsync(string prefix)
    {
        var wallet = await CreateWalletAsync(prefix);
        await _api.DepositAsync(wallet.WalletId, 10000, wallet.ApiKey);
        return wallet;
    }
}
