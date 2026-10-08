using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Contracts;
using Wallet.Domain;
using Wallet.Infrastructure.Transfers;

namespace Wallet.Api.Controllers;

[ApiController]
[Route("api/transfers")]
public class TransfersController(ITransferService transfers) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<TransferResponse>> Create(CreateTransferRequest request, CancellationToken ct)
    {
        try
        {
            var transfer = await transfers.ExecuteAsync(
                TransferType.Transfer,
                request.SourceWalletId,
                request.DestinationWalletId,
                request.Currency,
                request.Amount,
                ct);

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

    private static TransferResponse ToResponse(Transfer t) =>
        new(t.Id, t.Type.ToString(), t.SourceWalletId, t.DestinationWalletId, t.Currency, t.Amount, t.CreatedAt);
}
