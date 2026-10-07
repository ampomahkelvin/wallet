namespace Wallet.Api.Contracts;

public record OpenAccountRequest(string OwnerId, string Currency);

public record AccountResponse(Guid Id, string OwnerId, string Currency, long Balance);