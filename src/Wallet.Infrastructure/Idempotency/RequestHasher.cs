using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wallet.Infrastructure.Idempotency;

public static class RequestHasher
{
    public static byte[] Hash(string method, string path, object? body)
    {
        var canonicalBody = body is null ? "" : JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var input = $"{method.ToUpperInvariant()}|{path}|{canonicalBody}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(input));
    }
}