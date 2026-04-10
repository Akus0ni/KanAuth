namespace KanAuth.Application.DTOs.Responses;

public record UserDto(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    DateTime CreatedAtUtc,
    DateTime? LastLoginAtUtc);
