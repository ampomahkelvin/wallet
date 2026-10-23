using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wallet.Domain;

namespace Wallet.Api.Middleware;

public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code, message) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "NOT_FOUND", exception.Message),
            ConflictException => (StatusCodes.Status409Conflict, "CONFLICT", exception.Message),
            InsufficientFundsException => (StatusCodes.Status409Conflict, "INSUFFICIENT_FUNDS", exception.Message),
            IdempotencyKeyReusedException => (StatusCodes.Status422UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED", exception.Message),
            ValidationException => (StatusCodes.Status422UnprocessableEntity, "VALIDATION_ERROR", exception.Message),
            BadRequestException => (StatusCodes.Status400BadRequest, "BAD_REQUEST", exception.Message),
            UnauthorizedException => (StatusCodes.Status401Unauthorized, "UNAUTHORIZED", exception.Message),
            ForbiddenException => (StatusCodes.Status403Forbidden, "FORBIDDEN", exception.Message),
            DomainException => (StatusCodes.Status400BadRequest, "BAD_REQUEST", exception.Message),
            DbUpdateException => MapDbException(exception),
            PostgresException pg => MapPostgres(pg),
            _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred.")
        };

        if (status >= 500)
            logger.LogError(exception, "Request failed with {Code}", code);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { code, message }, ct);
        return true;
    }

    private static (int, string, string) MapDbException(Exception exception)
    {
        var pg = exception.InnerException as PostgresException;
        return pg is not null ? MapPostgres(pg) : (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred.");
    }

    private static (int, string, string) MapPostgres(PostgresException exception) => exception.SqlState switch
    {
        "23505" => (StatusCodes.Status409Conflict, "CONFLICT", "A conflicting record already exists."),
        "23514" => (StatusCodes.Status422UnprocessableEntity, "VALIDATION_ERROR", "A database constraint was violated."),
        "23503" => (StatusCodes.Status409Conflict, "CONFLICT", "The operation conflicts with related data."),
        _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An unexpected error occurred.")
    };
}