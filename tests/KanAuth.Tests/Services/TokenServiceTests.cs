using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using KanAuth.Application.Services;
using KanAuth.Application.Settings;
using KanAuth.Domain.Entities;
using Microsoft.Extensions.Options;

namespace KanAuth.Tests.Services;

public class TokenServiceTests
{
    private static readonly JwtSettings Settings = new()
    {
        Secret = "test-secret-key-long-enough-for-hmac256-algorithm",
        Issuer = "KanAuth-Test",
        Audience = "KanAuth-Test-Clients",
        AccessTokenExpiryMinutes = 15,
        RefreshTokenExpiryDays = 30
    };

    private static TokenService CreateSut() => new(Options.Create(Settings));

    private static User MakeUser() =>
        User.Create("alice@example.com", "any-hash", "Alice", "Smith");

    // ── GenerateAccessToken ───────────────────────────────────────────────────

    [Fact]
    public void GenerateAccessToken_ReturnsNonEmptyToken()
    {
        var result = CreateSut().GenerateAccessToken(MakeUser());

        result.Token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateAccessToken_TokenContainsUserIdAsSub()
    {
        var user = MakeUser();
        var result = CreateSut().GenerateAccessToken(user);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);

        jwt.Subject.Should().Be(user.Id.ToString());
    }

    [Fact]
    public void GenerateAccessToken_TokenContainsEmail()
    {
        var user = MakeUser();
        var result = CreateSut().GenerateAccessToken(user);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);
        var emailClaim = jwt.Claims.FirstOrDefault(c => c.Type == "email");

        emailClaim.Should().NotBeNull();
        emailClaim!.Value.Should().Be("alice@example.com");
    }

    [Fact]
    public void GenerateAccessToken_ExpiresAtMatchesConfiguredMinutes()
    {
        var before = DateTime.UtcNow;
        var result = CreateSut().GenerateAccessToken(MakeUser());
        var after = DateTime.UtcNow;

        result.ExpiresAt.Should()
            .BeOnOrAfter(before.AddMinutes(Settings.AccessTokenExpiryMinutes))
            .And
            .BeOnOrBefore(after.AddMinutes(Settings.AccessTokenExpiryMinutes));
    }

    [Fact]
    public void GenerateAccessToken_TwoCallsProduce_DifferentJtis()
    {
        var user = MakeUser();
        var sut = CreateSut();

        var first  = new JwtSecurityTokenHandler().ReadJwtToken(sut.GenerateAccessToken(user).Token);
        var second = new JwtSecurityTokenHandler().ReadJwtToken(sut.GenerateAccessToken(user).Token);

        first.Id.Should().NotBe(second.Id);
    }

    // ── GenerateRefreshToken ──────────────────────────────────────────────────

    [Fact]
    public void GenerateRefreshToken_TokenIsNonEmpty()
    {
        var token = CreateSut().GenerateRefreshToken(Guid.NewGuid(), "127.0.0.1");

        token.Token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateRefreshToken_SetsCorrectUserId()
    {
        var userId = Guid.NewGuid();
        var token = CreateSut().GenerateRefreshToken(userId, "127.0.0.1");

        token.UserId.Should().Be(userId);
    }

    [Fact]
    public void GenerateRefreshToken_SetsCorrectIp()
    {
        var token = CreateSut().GenerateRefreshToken(Guid.NewGuid(), "192.168.1.1");

        token.CreatedByIp.Should().Be("192.168.1.1");
    }

    [Fact]
    public void GenerateRefreshToken_ExpiresAfterConfiguredDays()
    {
        var before = DateTime.UtcNow;
        var token = CreateSut().GenerateRefreshToken(Guid.NewGuid(), "127.0.0.1");
        var after = DateTime.UtcNow;

        token.ExpiresAtUtc.Should()
            .BeOnOrAfter(before.AddDays(Settings.RefreshTokenExpiryDays))
            .And
            .BeOnOrBefore(after.AddDays(Settings.RefreshTokenExpiryDays));
    }

    [Fact]
    public void GenerateRefreshToken_TwoCallsProduce_DifferentTokens()
    {
        var sut = CreateSut();
        var userId = Guid.NewGuid();

        var first  = sut.GenerateRefreshToken(userId, "127.0.0.1");
        var second = sut.GenerateRefreshToken(userId, "127.0.0.1");

        first.Token.Should().NotBe(second.Token);
    }
}
