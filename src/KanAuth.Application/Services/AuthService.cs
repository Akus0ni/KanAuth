using BCrypt.Net;
using KanAuth.Application.DTOs.Requests;
using KanAuth.Application.DTOs.Responses;
using KanAuth.Application.Interfaces;
using KanAuth.Domain.Entities;
using KanAuth.Domain.Exceptions;

namespace KanAuth.Application.Services;

public class AuthService : IAuthService
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ITokenService _tokenService;
    private readonly IUnitOfWork _uow;

    public AuthService(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        ITokenService tokenService,
        IUnitOfWork uow)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _tokenService = tokenService;
        _uow = uow;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest req, string ip, CancellationToken ct = default)
    {
        var email = req.Email.ToLowerInvariant();

        if (await _users.EmailExistsAsync(email, ct))
            throw new InvalidOperationException($"Email '{email}' is already registered.");

        var hash = BCrypt.Net.BCrypt.HashPassword(req.Password, workFactor: 12);
        var user = User.Create(email, hash, req.FirstName, req.LastName);

        await _users.AddAsync(user, ct);

        return await IssueTokenPair(user, ip, ct);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest req, string ip, CancellationToken ct = default)
    {
        var email = req.Email.ToLowerInvariant();
        var user = await _users.GetByEmailAsync(email, ct)
            ?? throw new InvalidCredentialsException();

        if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            throw new InvalidCredentialsException();

        if (!user.IsActive)
            throw new InvalidCredentialsException();

        user.RecordLogin();
        await _users.UpdateAsync(user, ct);

        return await IssueTokenPair(user, ip, ct);
    }

    public async Task LogoutAsync(string refreshToken, string ip, CancellationToken ct = default)
    {
        var token = await _refreshTokens.GetByTokenAsync(refreshToken, ct);
        if (token is null || !token.IsActive)
            return;

        token.RevokedAtUtc = DateTime.UtcNow;
        token.RevokedByIp = ip;
        await _refreshTokens.UpdateAsync(token, ct);
        await _uow.SaveChangesAsync(ct);
    }

    public async Task<AuthResponse> RefreshTokenAsync(string refreshToken, string ip, CancellationToken ct = default)
    {
        var token = await _refreshTokens.GetByTokenAsync(refreshToken, ct)
            ?? throw new TokenExpiredException();

        if (token.IsRevoked)
        {
            await _refreshTokens.RevokeAllForUserAsync(token.UserId, ip, ct);
            throw new TokenReuseException();
        }

        if (token.IsExpired)
            throw new TokenExpiredException();

        var user = token.User;

        token.RevokedAtUtc = DateTime.UtcNow;
        token.RevokedByIp = ip;

        var newRefreshToken = _tokenService.GenerateRefreshToken(user.Id, ip);
        token.ReplacedByToken = newRefreshToken.Token;

        await _refreshTokens.UpdateAsync(token, ct);
        await _refreshTokens.AddAsync(newRefreshToken, ct);
        await _uow.SaveChangesAsync(ct);

        var accessToken = _tokenService.GenerateAccessToken(user);

        return new AuthResponse(
            accessToken.Token,
            newRefreshToken.Token,
            accessToken.ExpiresAt,
            newRefreshToken.ExpiresAtUtc,
            ToUserDto(user));
    }

    public async Task<UserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new UserNotFoundException(userId);

        return ToUserDto(user);
    }

    private async Task<AuthResponse> IssueTokenPair(User user, string ip, CancellationToken ct)
    {
        var accessToken = _tokenService.GenerateAccessToken(user);
        var refreshToken = _tokenService.GenerateRefreshToken(user.Id, ip);

        await _refreshTokens.AddAsync(refreshToken, ct);
        await _uow.SaveChangesAsync(ct);

        return new AuthResponse(
            accessToken.Token,
            refreshToken.Token,
            accessToken.ExpiresAt,
            refreshToken.ExpiresAtUtc,
            ToUserDto(user));
    }

    private static UserDto ToUserDto(User user) =>
        new(user.Id, user.Email, user.FirstName, user.LastName, user.CreatedAtUtc, user.LastLoginAtUtc);
}
