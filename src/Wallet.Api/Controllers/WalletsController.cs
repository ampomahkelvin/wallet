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
        try
        {
            var clientId = GetClientId();
            var key = GetIdempotencyKey();
            var hash = RequestHasher.Hash("POST", "/api/wallets", request);

            var (wallet, replayed) = await idempotency.HandleAsync(
                clientId, key, hash,
                async () => await wallets.OpenAsync(request.OwnerId, request.Currency, ct),
                ct);

            if (replayed) Response.Headers.Append("Idempotent-Replayed", "true");

            return CreatedAtAction(nameof(Get), new { id = wallet.Id }, ToWalletResponse(wallet));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
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
        try
        {
            var clientId = GetClientId();
            var key = GetIdempotencyKey();
            var hash = RequestHasher.Hash("POST", $"/api/wallets/{id}/deposits", request);

            var (transfer, replayed) = await idempotency.HandleAsync(
                clientId, key, hash,
                async () =>
                {
                    var wallet = await wallets.GetAsync(id, ct)
                        ?? throw new DomainException("Wallet not found.");

                    var systemWallet = await wallets.GetSystemWalletAsync(wallet.Currency, ct)
                        ?? throw new DomainException("System wallet not found.");

                    return await transfers.ExecuteAsync(
                        TransferType.Deposit,
                        systemWallet.Id,
                        wallet.Id,
                        wallet.Currency,
                        request.Amount,
                        null,
                        ct);
                },
                ct);

            if (replayed) Response.Headers.Append("Idempotent-Replayed", "true");

            return CreatedAtAction("Get", "Transfers", new { id = transfer.Id }, ToTransferResponse(transfer));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpPost("{id:guid}/withdrawals")]
    public async Task<ActionResult<TransferResponse>> Withdraw(Guid id, WithdrawalRequest request, CancellationToken ct)
    {
        try
        {
            var clientId = GetClientId();
            var key = GetIdempotencyKey();
            var hash = RequestHasher.Hash("POST", $"/api/wallets/{id}/withdrawals", request);

            var (transfer, replayed) = await idempotency.HandleAsync(
                clientId, key, hash,
                async () =>
                {
                    var wallet = await wallets.GetAsync(id, ct)
                        ?? throw new DomainException("Wallet not found.");

                    var systemWallet = await wallets.GetSystemWalletAsync(wallet.Currency, ct)
                        ?? throw new DomainException("System wallet not found.");

                    return await transfers.ExecuteAsync(
                        TransferType.Withdrawal,
                        wallet.Id,
                        systemWallet.Id,
                        wallet.Currency,
                        request.Amount,
                        null,
                        ct);
                },
                ct);

            if (replayed) Response.Headers.Append("Idempotent-Replayed", "true");

            return CreatedAtAction("Get", "Transfers", new { id = transfer.Id }, ToTransferResponse(transfer));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
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