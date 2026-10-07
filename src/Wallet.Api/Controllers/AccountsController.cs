using Microsoft.AspNetCore.Mvc;
using Wallet.Api.Contracts;
using Wallet.Domain;
using Wallet.Infrastructure.Accounts;

namespace Wallet.Api.Controllers;

[ApiController]
[Route("api/accounts")]
public class AccountsController(IAccountService accounts) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<AccountResponse>> Open(OpenAccountRequest request, CancellationToken ct)
    {
        try
        {
            var a = await accounts.OpenAsync(request.OwnerId, request.Currency, ct);
            return CreatedAtAction(nameof(Get), new { id = a.Id }, ToResponse(a));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AccountResponse>> Get(Guid id, CancellationToken ct)
    {
        var a = await accounts.GetAsync(id, ct);
        return a is null ? NotFound() : ToResponse(a);
    }

    private static AccountResponse ToResponse(Account a) => new(a.Id, a.OwnerId, a.Currency, a.Balance);
}