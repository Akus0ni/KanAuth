namespace KanAuth.Domain.Entities;

public class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? ReplacedByToken { get; set; }
    public string CreatedByIp { get; private set; } = string.Empty;
    public string? RevokedByIp { get; set; }

    public User User { get; set; } = null!;

    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
    public bool IsRevoked => RevokedAtUtc.HasValue;
    public bool IsActive => !IsRevoked && !IsExpired;

    private RefreshToken() { }

    public static RefreshToken Create(Guid userId, string token, DateTime expiresAtUtc, string ipAddress) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Token = token,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAtUtc,
            CreatedByIp = ipAddress
        };
}
