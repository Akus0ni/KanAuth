using FluentAssertions;
using KanAuth.Application.DTOs.Requests;
using KanAuth.Application.DTOs.Responses;
using KanAuth.Application.Interfaces;
using KanAuth.Application.Services;
using KanAuth.Domain.Entities;
using KanAuth.Domain.Exceptions;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace KanAuth.Tests.Services;

public class AuthServiceTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────

    private const string TestPassword = "Password1!";

    // workFactor 4 keeps tests fast while still exercising the real BCrypt path
    private static readonly string TestPasswordHash =
        BCrypt.Net.BCrypt.HashPassword(TestPassword, workFactor: 4);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRefreshTokenRepository _refreshTokens = Substitute.For<IRefreshTokenRepository>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly AuthService _sut;

    public AuthServiceTests()
    {
        _sut = new AuthService(_users, _refreshTokens, _tokenService, _uow);

        // default token stubs used by most tests
        _tokenService.GenerateAccessToken(Arg.Any<User>())
            .Returns(new AccessTokenResult("access.token.stub", DateTime.UtcNow.AddMinutes(15)));

        _tokenService.GenerateRefreshToken(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(RefreshToken.Create(Guid.NewGuid(), "refresh-token-stub",
                DateTime.UtcNow.AddDays(30), "127.0.0.1"));
    }

    private static User MakeUser(string email = "alice@example.com", bool isActive = true)
    {
        var user = User.Create(email, TestPasswordHash, "Alice", "Smith");
        if (!isActive) user.Deactivate();
        return user;
    }

    // ── RegisterAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterAsync_NewEmail_ReturnsAuthResponse()
    {
        _users.EmailExistsAsync("alice@example.com", Arg.Any<CancellationToken>()).Returns(false);

        var req = new RegisterRequest("Alice", "Smith", "alice@example.com", TestPassword);
        var result = await _sut.RegisterAsync(req, "127.0.0.1");

        result.AccessToken.Should().Be("access.token.stub");
        result.User.Email.Should().Be("alice@example.com");
    }

    [Fact]
    public async Task RegisterAsync_NewEmail_SavesOnce()
    {
        _users.EmailExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        await _sut.RegisterAsync(new RegisterRequest("Alice", "Smith", "alice@example.com", TestPassword), "127.0.0.1");

        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_EmailNormalizedToLowercase()
    {
        _users.EmailExistsAsync("alice@example.com", Arg.Any<CancellationToken>()).Returns(false);

        var req = new RegisterRequest("Alice", "Smith", "ALICE@EXAMPLE.COM", TestPassword);
        var result = await _sut.RegisterAsync(req, "127.0.0.1");

        result.User.Email.Should().Be("alice@example.com");
    }

    [Fact]
    public async Task RegisterAsync_EmailAlreadyExists_ThrowsInvalidOperation()
    {
        _users.EmailExistsAsync("alice@example.com", Arg.Any<CancellationToken>()).Returns(true);

        var req = new RegisterRequest("Alice", "Smith", "alice@example.com", TestPassword);
        var act = () => _sut.RegisterAsync(req, "127.0.0.1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*alice@example.com*");
    }

    // ── LoginAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsAuthResponse()
    {
        var user = MakeUser();
        _users.GetByEmailAsync("alice@example.com", Arg.Any<CancellationToken>()).Returns(user);

        var req = new LoginRequest("alice@example.com", TestPassword);
        var result = await _sut.LoginAsync(req, "127.0.0.1");

        result.AccessToken.Should().Be("access.token.stub");
        result.User.Email.Should().Be("alice@example.com");
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_SavesOnce()
    {
        var user = MakeUser();
        _users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);

        await _sut.LoginAsync(new LoginRequest("alice@example.com", TestPassword), "127.0.0.1");

        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_UserNotFound_ThrowsInvalidCredentials()
    {
        _users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var act = () => _sut.LoginAsync(new LoginRequest("ghost@example.com", TestPassword), "127.0.0.1");

        await act.Should().ThrowAsync<InvalidCredentialsException>();
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_ThrowsInvalidCredentials()
    {
        var user = MakeUser();
        _users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);

        var act = () => _sut.LoginAsync(new LoginRequest("alice@example.com", "WrongPass1!"), "127.0.0.1");

        await act.Should().ThrowAsync<InvalidCredentialsException>();
    }

    [Fact]
    public async Task LoginAsync_InactiveUser_ThrowsInvalidCredentials()
    {
        var user = MakeUser(isActive: false);
        _users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(user);

        var act = () => _sut.LoginAsync(new LoginRequest("alice@example.com", TestPassword), "127.0.0.1");

        await act.Should().ThrowAsync<InvalidCredentialsException>();
    }

    // ── LogoutAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LogoutAsync_ActiveToken_RevokesAndSaves()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), "my-token", DateTime.UtcNow.AddDays(1), "127.0.0.1");
        _refreshTokens.GetByTokenAsync("my-token", Arg.Any<CancellationToken>()).Returns(token);

        await _sut.LogoutAsync("my-token", "127.0.0.1");

        token.IsRevoked.Should().BeTrue();
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogoutAsync_TokenNotFound_DoesNotSave()
    {
        _refreshTokens.GetByTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await _sut.LogoutAsync("unknown-token", "127.0.0.1");

        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogoutAsync_AlreadyRevokedToken_DoesNotSave()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), "my-token", DateTime.UtcNow.AddDays(1), "127.0.0.1");
        token.RevokedAtUtc = DateTime.UtcNow.AddHours(-1); // already revoked
        _refreshTokens.GetByTokenAsync("my-token", Arg.Any<CancellationToken>()).Returns(token);

        await _sut.LogoutAsync("my-token", "127.0.0.1");

        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── RefreshTokenAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_ValidToken_ReturnsNewAuthResponse()
    {
        var user = MakeUser();
        var token = RefreshToken.Create(user.Id, "old-token", DateTime.UtcNow.AddDays(1), "127.0.0.1");
        SetUserOnToken(token, user);

        _refreshTokens.GetByTokenAsync("old-token", Arg.Any<CancellationToken>()).Returns(token);

        var result = await _sut.RefreshTokenAsync("old-token", "127.0.0.1");

        result.AccessToken.Should().Be("access.token.stub");
        token.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshTokenAsync_ValidToken_SavesOnce()
    {
        var user = MakeUser();
        var token = RefreshToken.Create(user.Id, "old-token", DateTime.UtcNow.AddDays(1), "127.0.0.1");
        SetUserOnToken(token, user);

        _refreshTokens.GetByTokenAsync("old-token", Arg.Any<CancellationToken>()).Returns(token);

        await _sut.RefreshTokenAsync("old-token", "127.0.0.1");

        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshTokenAsync_TokenNotFound_ThrowsTokenExpired()
    {
        _refreshTokens.GetByTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var act = () => _sut.RefreshTokenAsync("ghost-token", "127.0.0.1");

        await act.Should().ThrowAsync<TokenExpiredException>();
    }

    [Fact]
    public async Task RefreshTokenAsync_ExpiredToken_ThrowsTokenExpired()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), "old-token",
            DateTime.UtcNow.AddDays(-1), // expired yesterday
            "127.0.0.1");
        _refreshTokens.GetByTokenAsync("old-token", Arg.Any<CancellationToken>()).Returns(token);

        var act = () => _sut.RefreshTokenAsync("old-token", "127.0.0.1");

        await act.Should().ThrowAsync<TokenExpiredException>();
    }

    [Fact]
    public async Task RefreshTokenAsync_RevokedToken_ThrowsTokenReuse()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), "old-token", DateTime.UtcNow.AddDays(1), "127.0.0.1");
        token.RevokedAtUtc = DateTime.UtcNow.AddHours(-1); // already revoked
        _refreshTokens.GetByTokenAsync("old-token", Arg.Any<CancellationToken>()).Returns(token);

        var act = () => _sut.RefreshTokenAsync("old-token", "127.0.0.1");

        await act.Should().ThrowAsync<TokenReuseException>();
    }

    [Fact]
    public async Task RefreshTokenAsync_RevokedToken_RevokesAllTokensForUser()
    {
        var userId = Guid.NewGuid();
        var token = RefreshToken.Create(userId, "old-token", DateTime.UtcNow.AddDays(1), "127.0.0.1");
        token.RevokedAtUtc = DateTime.UtcNow.AddHours(-1);
        _refreshTokens.GetByTokenAsync("old-token", Arg.Any<CancellationToken>()).Returns(token);

        await Assert.ThrowsAsync<TokenReuseException>(() =>
            _sut.RefreshTokenAsync("old-token", "attacker-ip"));

        await _refreshTokens.Received(1)
            .RevokeAllForUserAsync(userId, "attacker-ip", Arg.Any<CancellationToken>());
    }

    // ── GetCurrentUserAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentUserAsync_ExistingUser_ReturnsUserDto()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.GetCurrentUserAsync(user.Id);

        result.Email.Should().Be("alice@example.com");
        result.FirstName.Should().Be("Alice");
    }

    [Fact]
    public async Task GetCurrentUserAsync_UserNotFound_ThrowsUserNotFound()
    {
        var unknownId = Guid.NewGuid();
        _users.GetByIdAsync(unknownId, Arg.Any<CancellationToken>()).ReturnsNull();

        var act = () => _sut.GetCurrentUserAsync(unknownId);

        await act.Should().ThrowAsync<UserNotFoundException>();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // RefreshToken.User is a navigation property with a public setter
    private static void SetUserOnToken(RefreshToken token, User user) =>
        token.User = user;
}
