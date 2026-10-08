namespace Wallet.Api.Contracts;

public record CreateTransferRequest(Guid SourceWalletId, Guid DestinationWalletId, string Currency, long Amount);

public record DepositRequest(long Amount);

public record WithdrawalRequest(long Amount);

public record TransferResponse(
    Guid Id,
    string Type,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    string Currency,
    long Amount,
    DateTimeOffset CreatedAt);

public record EntryResponse(long Id, Guid TransferId, long Amount, long BalanceAfter);

public record EntriesResponse(IReadOnlyList<EntryResponse> Entries, long? NextCursor);
