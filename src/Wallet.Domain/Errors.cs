namespace Wallet.Domain;

public class NotFoundException(string message) : DomainException(message);

public class ConflictException(string message) : DomainException(message);

public class InsufficientFundsException(string message) : DomainException(message);

public class IdempotencyKeyReusedException(string message) : DomainException(message);

public class ValidationException(string message) : DomainException(message);

public class BadRequestException(string message) : DomainException(message);

public class ForbiddenException(string message) : DomainException(message);

public class UnauthorizedException(string message) : DomainException(message);