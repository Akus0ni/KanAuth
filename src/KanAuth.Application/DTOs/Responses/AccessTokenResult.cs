namespace KanAuth.Application.DTOs.Responses;

public record AccessTokenResult(string Token, DateTime ExpiresAt);
