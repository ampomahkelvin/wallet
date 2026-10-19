using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wallet.Infrastructure.Persistence;
using Xunit;

namespace Wallet.Api.Tests;

public class IdempotencyApiTests : IClassFixture<WalletApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly WalletApiFactory _factory;
    private readonly HttpClient _client;

    public IdempotencyApiTests(WalletApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostWallet_SameKeyTwice_ReplaysAndCreatesOneWallet()
    {
        var key = Guid.NewGuid().ToString();
        var payload = new { ownerId = "user-1", currency = "GHS" };

        var first = await PostJson("/api/wallets", payload, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Contains("Idempotent-Replayed").Should().BeFalse();
        var firstBody = await first.Content.ReadFromJsonAsync<WalletDto>(JsonOptions);

        var second = await PostJson("/api/wallets", payload, key);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.Should().Contain(h => h.Key.Equals("Idempotent-Replayed", StringComparison.OrdinalIgnoreCase));
        var secondBody = await second.Content.ReadFromJsonAsync<WalletDto>(JsonOptions);

        secondBody!.Id.Should().Be(firstBody!.Id);

        using var db = _factory.CreateDbContext();
        (await db.Wallets.CountAsync(w => w.OwnerId == "user-1" && w.Currency == "GHS")).Should().Be(1);
    }

    [Fact]
    public async Task PostTransfer_50ParallelSameKey_CreatesOneTransferAndOneEvent()
    {
        var alice = await CreateWallet("alice");
        var bob = await CreateWallet("bob");
        await DepositAsync(alice, 10000);
        await DepositAsync(bob, 10000);

        var key = Guid.NewGuid().ToString();
        var payload = new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 100
        };

        var responses = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => PostJson("/api/transfers", payload, key)));

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);

        var ids = await Task.WhenAll(responses
            .Select(r => r.Content.ReadFromJsonAsync<TransferDto>(JsonOptions)));
        ids.Select(t => t!.Id).Distinct().Should().HaveCount(1);

        using var db = _factory.CreateDbContext();
        (await db.Transfers.CountAsync()).Should().Be(3);
        (await db.OutboxEvents.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task PostTransfer_SameKeyDifferentPayload_Returns422()
    {
        var alice = await CreateWallet("alice");
        var bob = await CreateWallet("bob");
        await DepositAsync(alice, 10000);
        await DepositAsync(bob, 10000);

        var key = Guid.NewGuid().ToString();

        var first = await PostJson("/api/transfers", new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 100
        }, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await PostJson("/api/transfers", new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 200
        }, key);
        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PostWallet_MissingIdempotencyKey_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/wallets", new { ownerId = "u", currency = "GHS" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetWallet_Unknown_Returns404()
    {
        var response = await _client.GetAsync($"/api/wallets/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostTransfer_InsufficientFunds_Returns409()
    {
        var alice = await CreateWallet("alice");
        var bob = await CreateWallet("bob");

        var response = await PostJson("/api/transfers", new
        {
            sourceWalletId = alice,
            destinationWalletId = bob,
            currency = "GHS",
            amount = 100
        }, Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task GetEntries_LimitTooLarge_Returns422()
    {
        var wallet = await CreateWallet("entries-user");
        var response = await _client.GetAsync($"/api/wallets/{wallet}/entries?limit=500");
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    private async Task<Guid> CreateWallet(string ownerId)
    {
        var uniqueOwner = $"{ownerId}-{Guid.NewGuid():N}";
        var response = await PostJson("/api/wallets", new { ownerId = uniqueOwner, currency = "GHS" }, Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<WalletDto>(JsonOptions);
        return body!.Id;
    }

    private async Task DepositAsync(Guid walletId, long amount)
    {
        var response = await PostJson($"/api/wallets/{walletId}/deposits", new { amount }, Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private Task<HttpResponseMessage> PostJson(string url, object payload, string idempotencyKey)
    {
        var content = JsonContent.Create(payload, options: JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("X-Client-Id", "api-test");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _client.SendAsync(request);
    }

    private sealed record WalletDto(Guid Id, string OwnerId, string Currency, long Balance);

    private sealed record TransferDto(
        Guid Id,
        string Type,
        Guid SourceWalletId,
        Guid DestinationWalletId,
        string Currency,
        long Amount,
        DateTimeOffset CreatedAt);
}