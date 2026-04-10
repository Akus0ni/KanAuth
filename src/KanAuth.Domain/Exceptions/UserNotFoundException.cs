namespace KanAuth.Domain.Exceptions;

public class UserNotFoundException : DomainException
{
    public UserNotFoundException(Guid userId)
        : base($"User '{userId}' was not found.") { }
}
