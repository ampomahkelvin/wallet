using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Contracts;
using Wallet.Domain;
using Wallet.Infrastructure.Accounts;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Transfers;

namespace Wallet.Api.Controllers;

[ApiController]
[Route("api/wallets")]
public class WalletsController(
    IAccountService wallets,
    ITransferService transfers,
    IIdempotencyService idempotency) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<WalletResponse>> Open(OpenWalletRequest request, CancellationToken ct)
    {
        var clientId = GetClientId();
        var key = GetIdempotencyKey();
        var hash = RequestHasher.Hash("POST", "/api/wallets", request);

        var (response, status, replayed) = await idempotency.HandleAsync(
            clientId, key, hash,
            async db =>
            {
                var wallet = await wallets.OpenInTransactionAsync(db, request.OwnerId, request.Currency, ct);
                return (ToWalletResponse(wallet), (short)StatusCodes.Status201Created);
            },
            ct);

        if (replayed) Response.Headers.Append("Idempotent-Replayed", "true");

        return StatusCode(status, response);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WalletResponse>> Get(Guid id, CancellationToken ct)
    {
        var w = await wallets.GetAsync(id, ct);
        return w is null ? NotFound() : ToWalletResponse(w);
    }

    [HttpPost("{id:guid}/deposits")]
    public async Task<ActionResult<TransferResponse>> Deposit(Guid id, DepositRequest request, CancellationToken ct)
    {
        var clientId = GetClientId();
        var key = GetIdempotencyKey();
        var hash = RequestHasher.Hash("POST", $"/api/wallets/{id}/deposits", request);

        var (response, status, replayed) = await idempotency.HandleAsync(
            clientId, key, hash,
            async db =>
            {
                var wallet = await wallets.GetAsync(db, id, ct)
                    ?? throw new NotFoundException("Wallet not found.");

                var systemWallet = await wallets.GetSystemWalletAsync(db, wallet.Currency, ct)
                    ?? throw new NotFoundException("System wallet not found.");

                var transfer = await transfers.ExecuteInTransactionAsync(
                    db,
                    TransferType.Deposit,
                    systemWallet.Id,
                    wallet.Id,
                    wallet.Currency,
                    request.Amount,
                    null,
                    ct);

                return (ToTransferResponse(transfer), (short)StatusCodes.Status201Created);
            },
            ct);

        if (replayed) Response.Headers.Append("Idempotent-Replayed", "true");

        return StatusCode(status, response);
    }

    [HttpPost("{id:guid}/withdrawals")]
    public async Task<ActionResult<TransferResponse>> Withdraw(Guid id, WithdrawalRequest request, CancellationToken ct)
    {
        var clientId = GetClientId();
        var key = GetIdempotencyKey();
        var hash = RequestHasher.Hash("POST", $"/api/wallets/{id}/withdrawals", request);

        var (response, status, replayed) = await idempotency.HandleAsync(
            clientId, key, hash,
            async db =>
            {
                var wallet = await wallets.GetAsync(db, id, ct)
                    ?? throw new NotFoundException("Wallet not found.");

                var systemWallet = await wallets.GetSystemWalletAsync(db, wallet.Currency, ct)
                    ?? throw new NotFoundException("System wallet not found.");

                var transfer = await transfers.ExecuteInTransactionAsync(
                    db,
                    TransferType.Withdrawal,
                    wallet.Id,
                    systemWallet.Id,
                    wallet.Currency,
                    request.Amount,
                    null,
                    ct);

                return (ToTransferResponse(transfer), (short)StatusCodes.Status201Created);
            },
            ct);

        if (replayed) Response.Headers.Append("Idempotent-Replayed", "true");

        return StatusCode(status, response);
    }

    [HttpGet("{id:guid}/entries")]
    public async Task<ActionResult<EntriesResponse>> GetEntries(
        Guid id,
        [FromQuery] long? before,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var entries = await transfers.GetWalletEntriesAsync(id, before, limit, ct);

        var hasMore = entries.Count > limit;
        var page = hasMore ? entries.Take(limit).ToList() : entries;

        var nextCursor = hasMore ? page.Last().Id : (long?)null;

        var response = new EntriesResponse(
            page.Select(e => new EntryResponse(e.Id, e.TransferId, e.Amount, e.BalanceAfter)).ToList(),
            nextCursor);

        return response;
    }

    private string GetClientId() =>
        Request.Headers["X-Client-Id"].FirstOrDefault() ?? "default";

    private string GetIdempotencyKey() =>
        Request.Headers["Idempotency-Key"].FirstOrDefault()
        ?? throw new DomainException("Idempotency-Key header is required.");

    private static WalletResponse ToWalletResponse(Wallet.Domain.Wallet w) =>
        new(w.Id, w.OwnerId ?? "", w.Currency, w.Balance);

    private static TransferResponse ToTransferResponse(Transfer t) =>
        new(t.Id, t.Type switch
        {
            TransferType.Transfer => "transfer",
            TransferType.Deposit => "deposit",
            TransferType.Withdrawal => "withdrawal",
            TransferType.Reversal => "reversal",
            _ => t.Type.ToString()
        }, t.SourceWalletId, t.DestinationWalletId, t.Currency, t.Amount, t.CreatedAt);
}