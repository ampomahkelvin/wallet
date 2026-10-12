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
        try
        {
            var clientId = GetClientId();
            var key = GetIdempotencyKey();
            var hash = RequestHasher.Hash("POST", "/api/transfers", request);

            var (transfer, replayed) = await idempotency.HandleAsync(
                clientId, key, hash,
                async () => await transfers.ExecuteAsync(
                    TransferType.Transfer,
                    request.SourceWalletId,
                    request.DestinationWalletId,
                    request.Currency,
                    request.Amount,
                    null,
                    ct),
                ct);

            if (replayed) Response.Headers.Append("Idempotent-Replayed", "true");

            return CreatedAtAction(nameof(Get), new { id = transfer.Id }, ToResponse(transfer));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
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
        try
        {
            var clientId = GetClientId();
            var key = GetIdempotencyKey();
            var hash = RequestHasher.Hash("POST", $"/api/transfers/{id}/reverse", null);

            var (transfer, replayed) = await idempotency.HandleAsync(
                clientId, key, hash,
                async () => await transfers.ReverseAsync(id, ct),
                ct);

            if (replayed) Response.Headers.Append("Idempotent-Replayed", "true");

            return CreatedAtAction(nameof(Get), new { id = transfer.Id }, ToResponse(transfer));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
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