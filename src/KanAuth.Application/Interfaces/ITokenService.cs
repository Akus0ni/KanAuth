using System.Security.Claims;
using KanAuth.Application.DTOs.Responses;
using KanAuth.Domain.Entities;

namespace KanAuth.Application.Interfaces;

public interface ITokenService
{
    AccessTokenResult GenerateAccessToken(User user);
    RefreshToken GenerateRefreshToken(Guid userId, string ipAddress);
    ClaimsPrincipal? ValidateAccessToken(string token);
}
