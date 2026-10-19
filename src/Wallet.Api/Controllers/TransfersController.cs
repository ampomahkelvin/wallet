using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Contracts;
using Wallet.Domain;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Transfers;

namespace Wallet.Api.Controllers;

[ApiController]
[Route("api/transfers")]
public class TransfersController(ITransferService transfers, IIdempotencyService idempotency) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<TransferResponse>> Create(CreateTransferRequest request, CancellationToken ct)
    {
        var clientId = GetClientId();
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
                    null,
                    ct);

                return (ToResponse(transfer), (short)StatusCodes.Status201Created);
            },
            ct);

        if (replayed) Response.Headers.Append("Idempotent-Replayed", "true");

        return StatusCode(status, response);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TransferResponse>> Get(Guid id, CancellationToken ct)
    {
        var transfer = await transfers.GetAsync(id, ct);
        return transfer is null ? NotFound() : ToResponse(transfer);
    }

    [HttpPost("{id:guid}/reverse")]
    public async Task<ActionResult<TransferResponse>> Reverse(Guid id, CancellationToken ct)
    {
        var clientId = GetClientId();
        var key = GetIdempotencyKey();
        var hash = RequestHasher.Hash("POST", $"/api/transfers/{id}/reverse", null);

        var (response, status, replayed) = await idempotency.HandleAsync(
            clientId, key, hash,
            async db =>
            {
                var transfer = await transfers.ReverseInTransactionAsync(db, id, ct);
                return (ToResponse(transfer), (short)StatusCodes.Status201Created);
            },
            ct);

        if (replayed) Response.Headers.Append("Idempotent-Replayed", "true");

        return StatusCode(status, response);
    }

    private string GetClientId() =>
        Request.Headers["X-Client-Id"].FirstOrDefault() ?? "default";

    private string GetIdempotencyKey() =>
        Request.Headers["Idempotency-Key"].FirstOrDefault()
        ?? throw new DomainException("Idempotency-Key header is required.");

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