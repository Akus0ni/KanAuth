using System.Security.Claims;
using KanAuth.Domain.Entities;

namespace KanAuth.Application.Interfaces;

public interface ITokenService
{
    string GenerateAccessToken(User user);
    RefreshToken GenerateRefreshToken(Guid userId, string ipAddress);
    ClaimsPrincipal? ValidateAccessToken(string token);
}
