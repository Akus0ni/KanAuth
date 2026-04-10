# JWT Tokens and Refresh Tokens — How They Work

## The Two-Token Strategy

KanAuth uses **two different tokens** for two different purposes:

| | Access Token | Refresh Token |
|-|-------------|---------------|
| Format | JWT (JSON Web Token) | Opaque random string |
| Stored? | Client only | Client + database |
| Expiry | 15 minutes | 30 days |
| Validated by | Decoding the JWT signature | DB lookup |
| Used for | Proving identity on every API request | Getting a new access token |

**Why two tokens?**

Access tokens expire quickly (15 min) to limit damage if intercepted. But you don't want users to log in every 15 minutes. The refresh token (30 days) lets the client silently get a new access token when the old one expires.

---

## What Is a JWT?

A JWT is three Base64-encoded parts separated by dots:

```
header.payload.signature
```

**Header** — algorithm used:
```json
{ "alg": "HS256", "typ": "JWT" }
```

**Payload** — the claims (data inside the token):
```json
{
  "sub": "3fa85f64-5717-4562-b3fc-2c963f66afa6",  ← user ID
  "email": "user@example.com",
  "given_name": "Alice",
  "family_name": "Smith",
  "jti": "a unique token ID",                      ← prevents replay attacks
  "exp": 1712345678                                 ← expiry timestamp
}
```

**Signature** — HMAC-SHA256 of (header + payload) using the secret key. This prevents tampering.

Nobody can modify the payload without invalidating the signature. That's why the server can trust the user ID inside it without a database lookup.

---

## The TokenService

**File**: `src/KanAuth.Application/Services/TokenService.cs`

### Generating an Access Token

`GenerateAccessToken` returns an `AccessTokenResult` record — not just the token string. The record carries both the token and the expiry timestamp computed from config:

```csharp
public record AccessTokenResult(string Token, DateTime ExpiresAt);

public AccessTokenResult GenerateAccessToken(User user)
{
    var key       = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Secret));
    var creds     = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var expiresAt = DateTime.UtcNow.AddMinutes(_jwt.AccessTokenExpiryMinutes);  // from config

    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub,        user.Id.ToString()),
        new Claim(JwtRegisteredClaimNames.Email,      user.Email),
        new Claim(JwtRegisteredClaimNames.GivenName,  user.FirstName),
        new Claim(JwtRegisteredClaimNames.FamilyName, user.LastName),
        new Claim(JwtRegisteredClaimNames.Jti,        Guid.NewGuid().ToString())
    };

    var token = new JwtSecurityToken(
        issuer:             _jwt.Issuer,
        audience:           _jwt.Audience,
        claims:             claims,
        expires:            expiresAt,
        signingCredentials: creds);

    return new AccessTokenResult(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
}
```

**Why return a record instead of just a string?** `AuthService` needs to put the expiry time into `AuthResponse.AccessTokenExpiresAt`. Previously this was hardcoded as `DateTime.UtcNow.AddMinutes(15)` — a bug waiting to happen if the config value ever changed. Now the expiry is computed once in `TokenService` and flows through to the response, so the JWT's actual expiry and the value reported to the client are always identical.
```

**What each part does:**
- `SymmetricSecurityKey` — converts the secret string into a cryptographic key
- `SigningCredentials` — declares "sign this token using HMAC-SHA256 with this key"
- `claims` — the data embedded in the token (user ID, email, etc.)
- `JwtSecurityToken` — assembles header + payload
- `WriteToken` — encodes it to the final `xxxxx.yyyyy.zzzzz` string

### Generating a Refresh Token

```csharp
public RefreshToken GenerateRefreshToken(Guid userId, string ipAddress)
{
    var randomBytes = RandomNumberGenerator.GetBytes(64);  // cryptographically secure random
    var token = Base64UrlEncoder.Encode(randomBytes);      // URL-safe string

    return new RefreshToken
    {
        Id           = Guid.NewGuid(),
        UserId       = userId,
        Token        = token,
        CreatedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddDays(_jwt.RefreshTokenExpiryDays),
        CreatedByIp  = ipAddress
    };
}
```

The refresh token is **not a JWT** — it's just 64 random bytes encoded as a URL-safe string. It has no self-contained claims. To use it, the server looks it up in the database.

`RandomNumberGenerator.GetBytes(64)` uses the OS's cryptographically secure random number generator. Do NOT use `Random` for security tokens — it's predictable.

### Validating an Access Token

Access token validation is handled entirely by the ASP.NET Core JWT Bearer middleware, configured in `Program.cs`. There is no `ValidateAccessToken` method in `TokenService` — it would be dead code since the middleware already does this job before any controller action runs.

The middleware uses these parameters (configured in `Program.cs`):

```csharp
new TokenValidationParameters
{
    ValidateIssuerSigningKey = true,
    IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
    ValidateIssuer           = true,  ValidIssuer   = jwtSection["Issuer"],
    ValidateAudience         = true,  ValidAudience = jwtSection["Audience"],
    ValidateLifetime         = true,
    ClockSkew                = TimeSpan.Zero   // no grace period on expiry
}
```

`ClockSkew = TimeSpan.Zero` means a token expired at 12:00:00 is rejected at 12:00:01. No grace period. This is intentional for security.

---

## The JwtSettings Configuration

**File**: `src/KanAuth.Application/Settings/JwtSettings.cs`

```csharp
public class JwtSettings
{
    public string Secret   { get; set; } = string.Empty;  // signing key
    public string Issuer   { get; set; } = string.Empty;  // who issued the token
    public string Audience { get; set; } = string.Empty;  // who the token is for
    public int AccessTokenExpiryMinutes { get; set; } = 15;
    public int RefreshTokenExpiryDays   { get; set; } = 30;
}
```

In `appsettings.json` (or environment variables), this looks like:
```json
"Jwt": {
  "Secret": "your-super-secret-key-at-least-32-chars",
  "Issuer": "KanAuth",
  "Audience": "KanAuth-Clients",
  "AccessTokenExpiryMinutes": 15,
  "RefreshTokenExpiryDays": 30
}
```

The `Secret` must be at least 32 characters for HMAC-SHA256. In production, this comes from an environment variable or secrets manager, never a config file.

---

## Try It Yourself

1. Paste any JWT into `jwt.io` and observe the decoded header and payload.
2. What happens if someone edits the `sub` (user ID) in the payload? Why doesn't it work?
3. Why is `Jti` (JWT ID) included as a claim? What attack does it help prevent?
4. Why does `ValidateLifetime = true` matter? What would happen without it?
