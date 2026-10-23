using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Authentication;
using Wallet.Api.Contracts;
using Wallet.Domain;
using Wallet.Infrastructure.Accounts;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Observability;
using Wallet.Infrastructure.Transfers;

namespace Wallet.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/transfers")]
public class TransfersController(ITransferService transfers, IAccountService wallets, IIdempotencyService idempotency) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<TransferResponse>> Create(CreateTransferRequest request, CancellationToken ct)
    {
        var clientId = User.GetClientId();
        var key = GetIdempotencyKey();
        var hash = RequestHasher.Hash("POST", "/api/transfers", request);

        var (response, status, replayed) = await idempotency.HandleAsync(
            clientId, key, hash,
            async db =>
            {
                var transfer = await transfers.ExecuteInTransactionAsync(
                    db,
                    TransferType.Transfer,
                    request.SourceWalletId,
                    request.DestinationWalletId,
                    request.Currency,
                    request.Amount,
                    clientId,
                    null,
                    ct);

                return (ToResponse(transfer), (short)StatusCodes.Status201Created);
            },
            ct);

        if (replayed)
            Response.Headers.Append("Idempotent-Replayed", "true");
        else
            LedgerMetrics.TransfersTotal.Inc();

        return StatusCode(status, response);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TransferResponse>> Get(Guid id, CancellationToken ct)
    {
        var clientId = User.GetClientId();
        var transfer = await transfers.GetAsync(id, ct);
        if (transfer is null) return NotFound();

        var sourceWallet = await wallets.GetAsync(transfer.SourceWalletId, clientId, ct);
        var destinationWallet = await wallets.GetAsync(transfer.DestinationWalletId, clientId, ct);
        if (sourceWallet is null && destinationWallet is null) return NotFound();

        return ToResponse(transfer);
    }

    [HttpPost("{id:guid}/reverse")]
    public async Task<ActionResult<TransferResponse>> Reverse(Guid id, CancellationToken ct)
    {
        var clientId = User.GetClientId();
        var key = GetIdempotencyKey();
        var hash = RequestHasher.Hash("POST", $"/api/transfers/{id}/reverse", null);

        var (response, status, replayed) = await idempotency.HandleAsync(
            clientId, key, hash,
            async db =>
            {
                var transfer = await transfers.ReverseInTransactionAsync(db, id, clientId, ct);
                return (ToResponse(transfer), (short)StatusCodes.Status201Created);
            },
            ct);

        if (replayed)
            Response.Headers.Append("Idempotent-Replayed", "true");
        else
            LedgerMetrics.TransfersTotal.Inc();

        return StatusCode(status, response);
    }

    private string GetIdempotencyKey() =>
        Request.Headers["Idempotency-Key"].FirstOrDefault()
        ?? throw new BadRequestException("Idempotency-Key header is required.");

    private static TransferResponse ToResponse(Transfer t) =>
        new(t.Id, t.Type switch
        {
            TransferType.Transfer => "transfer",
            TransferType.Deposit => "deposit",
            TransferType.Withdrawal => "withdrawal",
            TransferType.Reversal => "reversal",
            _ => t.Type.ToString()
        }, t.SourceWalletId, t.DestinationWalletId, t.Currency, t.Amount, t.CreatedAt);
}