# Security Patterns — Defense in Depth

KanAuth implements multiple independent security layers. If one fails, others still protect. This is called **defense in depth**.

---

## 1. Password Hashing — BCrypt

**Where**: `AuthService.RegisterAsync` and `AuthService.LoginAsync` (the only place — repositories receive already-normalized values)

```csharp
// Registration — hash the password
var hash = BCrypt.Net.BCrypt.HashPassword(req.Password, workFactor: 12);

// Login — verify
if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
    throw new InvalidCredentialsException();
```

### Why BCrypt?

**Never store plain passwords. Never use MD5/SHA.** Those are fast hashing algorithms designed for checksums, not passwords. An attacker with a GPU can compute billions of SHA hashes per second.

BCrypt is designed to be **slow**. The `workFactor: 12` means BCrypt performs `2^12 = 4096` rounds of hashing. Increasing the work factor doubles the time per hash, keeping pace with faster hardware.

A bcrypt hash looks like: `$2a$12$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy`

The `$12$` in the hash encodes the work factor, so future verifications use the right number of rounds even if you change the factor.

### Constant-Time Comparison

BCrypt.Verify performs a **constant-time** comparison. A naive string comparison `hashA == hashB` would return early when the first differing character is found — an attacker can measure response times to guess hash characters. BCrypt always compares all characters.

---

## 2. Refresh Token Rotation

**Where**: `AuthService.RefreshTokenAsync`

Every time a refresh token is used, it is:
1. Revoked (with timestamp and IP recorded)
2. Replaced with a new refresh token

```
Token A (active) → used → Token A (revoked, ReplacedBy: Token B)
                           Token B (active)
```

**Why rotate?** If Token A is stolen and the attacker uses it first, the real client then presents Token A and gets `TokenReuseException`. The user knows their token was stolen. Without rotation, the attacker silently gets new access tokens forever.

---

## 3. Token Reuse Detection

**Where**: `AuthService.RefreshTokenAsync`

```csharp
if (token.IsRevoked)
{
    // Someone used an already-rotated token — this is suspicious
    await _refreshTokens.RevokeAllForUserAsync(token.UserId, ip, ct);
    throw new TokenReuseException();
}
```

If a revoked token is presented, the system:
1. **Revokes all tokens** for that user (nuclear option)
2. Forces them to log in again

**Scenario where this protects you:**
- User has Token A
- Attacker steals Token A
- User refreshes: Token A → revoked, Token B issued
- Attacker tries to use Token A → detected! → all tokens revoked → attacker kicked out

The `ReplacedByToken` field in the database creates an audit trail of the entire token chain.

---

## 4. Credential Timing Attack Prevention

**Where**: `AuthService.LoginAsync`

```csharp
var user = await _users.GetByEmailAsync(email, ct)
    ?? throw new InvalidCredentialsException();

if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
    throw new InvalidCredentialsException();

if (!user.IsActive)
    throw new InvalidCredentialsException();
```

All three failure cases throw the **same exception** with the same message (`"Invalid credentials."`). This prevents:
- **User enumeration**: An attacker cannot tell if `alice@example.com` is registered by getting a different error than for `unknown@example.com`
- **Status enumeration**: An attacker cannot tell if an account is deactivated vs. non-existent

---

## 5. Rate Limiting

**Where**: `Program.cs` → `AspNetCoreRateLimit` package

```csharp
builder.Services.Configure<IpRateLimitOptions>(
    builder.Configuration.GetSection("IpRateLimiting"));
builder.Services.AddInMemoryRateLimiting();
```

In `appsettings.json`, you configure rules like:
```json
"IpRateLimiting": {
  "EnableEndpointRateLimiting": true,
  "GeneralRules": [
    { "Endpoint": "POST:/api/v1/auth/login",    "Limit": 10, "Period": "1m" },
    { "Endpoint": "POST:/api/v1/auth/register", "Limit": 5,  "Period": "1h" }
  ]
}
```

This blocks brute-force attacks. Without rate limiting, an attacker can try thousands of passwords per second against `/login`.

Rate limiting runs **before** authentication in the middleware pipeline — no point doing expensive password hashing if the client is throttled.

---

## 6. IP Tracking

**Where**: `AuthController.GetClientIp()` and `RefreshToken` entity

```csharp
private string GetClientIp()
{
    if (Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
    {
        var ip = forwarded.ToString().Split(',').FirstOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(ip)) return ip;
    }
    return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
```

`X-Forwarded-For` is set by load balancers (AWS ALB, nginx) to the real client IP. Without this, all requests look like they come from the load balancer's IP.

The IP is stored on every refresh token create/revoke event. Security teams can audit: "which IPs created tokens for this user?" — a token created in one country and used in another is suspicious.

---

## 7. Security Response Headers

**Where**: `Program.cs`

```csharp
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"]        = "DENY";
    context.Response.Headers["Referrer-Policy"]        = "strict-origin-when-cross-origin";
    await next();
});
```

| Header | Protection |
|--------|-----------|
| `X-Content-Type-Options: nosniff` | Prevents browsers from guessing MIME types (MIME sniffing attacks) |
| `X-Frame-Options: DENY` | Prevents embedding in iframes (clickjacking attacks) |
| `Referrer-Policy` | Controls what URL is sent in the Referer header to other sites |

These are minimal headers. A production deployment might also add `Content-Security-Policy` and `Strict-Transport-Security` (HSTS is enabled for production via `app.UseHsts()`).

---

## 8. Short-Lived Access Tokens

Access tokens expire in **15 minutes**. This limits the damage window if a token is stolen — the attacker can use it for at most 15 minutes before it stops working. They cannot get a new access token without the refresh token.

`ClockSkew = TimeSpan.Zero` ensures no grace period — expired means expired. This tightens the security window.

---

## 9. HTTPS Redirection + HSTS

```csharp
app.UseHttpsRedirection();  // redirect HTTP → HTTPS

if (app.Environment.IsProduction())
    app.UseHsts();  // tell browsers: ONLY connect via HTTPS for next year
```

HSTS (HTTP Strict Transport Security) instructs browsers to never make plain HTTP connections to this domain. This prevents downgrade attacks where a network attacker intercepts the initial HTTP connection before the redirect.

---

## Security Checklist (How This Project Measures Up)

| Threat | Defense |
|--------|---------|
| Stolen password | BCrypt hashing (workFactor 12) |
| Brute-force login | Rate limiting |
| Stolen access token | 15-min expiry |
| Stolen refresh token | Token rotation + reuse detection |
| User enumeration | Same error for wrong email / wrong password |
| MITM on transport | HTTPS + HSTS |
| Clickjacking | X-Frame-Options: DENY |
| Token tampering | JWT signature (HMAC-SHA256) |
| SQL injection | EF Core parameterized queries |
