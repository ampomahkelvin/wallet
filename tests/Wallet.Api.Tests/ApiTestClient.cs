using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;

namespace Wallet.Api.Tests;

public sealed class ApiTestClient(WalletApiFactory factory)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public HttpClient Http { get; } = factory.CreateClient();
    public TestApiKeyProvider Keys { get; } = factory.ApiKeys;

    public static string NewClientId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    public string CreateKey(string clientId) => Keys.CreateKey(clientId);

    public string CreateAdminKey(string clientId) => Keys.CreateAdminKey(clientId);

    public async Task<(Guid WalletId, string ClientId, string ApiKey)> CreateWalletAsync(
        string prefix,
        string? clientId = null)
    {
        clientId ??= NewClientId(prefix);
        var key = CreateKey(clientId);

        var response = await PostJsonAsync(
            "/api/wallets",
            new { currency = "GHS" },
            key,
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<WalletDto>(JsonOptions);
        body!.OwnerId.Should().Be(clientId);
        return (body.Id, clientId, key);
    }

    public async Task<HttpResponseMessage> DepositAsync(Guid walletId, long amount, string apiKey)
    {
        var response = await PostJsonAsync(
            $"/api/wallets/{walletId}/deposits",
            new { amount },
            apiKey,
            Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return response;
    }

    public Task<HttpResponseMessage> PostJsonAsync(
        string url,
        object payload,
        string? apiKey,
        string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };

        if (apiKey is not null)
            request.Headers.Add("X-Api-Key", apiKey);

        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);

        return Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string url, string? apiKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (apiKey is not null)
            request.Headers.Add("X-Api-Key", apiKey);

        return Http.SendAsync(request);
    }

    public sealed record WalletDto(Guid Id, string OwnerId, string Currency, long Balance);

    public sealed record TransferDto(
        Guid Id,
        string Type,
        Guid SourceWalletId,
        Guid DestinationWalletId,
        string Currency,
        long Amount,
        DateTimeOffset CreatedAt);
}
