namespace Wallet.Api.Contracts;

public record OpenWalletRequest(string Currency);

public record WalletResponse(Guid Id, string OwnerId, string Currency, long Balance);