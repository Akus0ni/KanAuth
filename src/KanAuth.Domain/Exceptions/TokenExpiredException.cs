namespace KanAuth.Domain.Exceptions;

public class TokenExpiredException : DomainException
{
    public TokenExpiredException()
        : base("The token has expired or is invalid.") { }
}
