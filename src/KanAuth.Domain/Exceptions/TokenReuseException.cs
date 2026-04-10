namespace KanAuth.Domain.Exceptions;

public class TokenReuseException : DomainException
{
    public TokenReuseException()
        : base("Token reuse detected. All sessions have been revoked.") { }
}
