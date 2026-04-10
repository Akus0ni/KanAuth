using KanAuth.Application.DTOs.Requests;
using KanAuth.Application.DTOs.Responses;

namespace KanAuth.Application.Interfaces;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest req, string ip, CancellationToken ct = default);
    Task<AuthResponse> LoginAsync(LoginRequest req, string ip, CancellationToken ct = default);
    Task LogoutAsync(string refreshToken, string ip, CancellationToken ct = default);
    Task<AuthResponse> RefreshTokenAsync(string refreshToken, string ip, CancellationToken ct = default);
    Task<UserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct = default);
}
