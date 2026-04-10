# The Auth Flow — End-to-End Request Lifecycle

This file traces each auth operation from HTTP request to database and back.

---

## 1. Registration — `POST /api/v1/auth/register`

**Request body:**
```json
{ "firstName": "Alice", "lastName": "Smith", "email": "alice@example.com", "password": "Secret1!" }
```

**Step-by-step:**

```
HTTP POST /register
    │
    ▼
ExceptionHandlingMiddleware (wraps everything in try/catch)
    │
    ▼
FluentValidation (automatic, runs before controller)
    validates: email format, password length, uppercase, digit, special char
    → 400 if invalid
    │
    ▼
AuthController.Register()
    extracts client IP from X-Forwarded-For or connection
    calls _auth.RegisterAsync(req, ip, ct)
    │
    ▼
AuthService.RegisterAsync()
    1. Normalize email: "Alice@Example.COM" → "alice@example.com"
    2. Check: await _users.EmailExistsAsync(email)
       → throws InvalidOperationException (409) if already registered
    3. Hash password: BCrypt.HashPassword(req.Password, workFactor: 12)
    4. Create entity: User.Create(email, hash, firstName, lastName)
    5. Track: _users.AddAsync(user, ct)              ← stages insert, no DB write yet
    6. Issue tokens: IssueTokenPair(user, ip, ct)
    │
    ▼
IssueTokenPair()
    1. GenerateAccessToken(user)  → AccessTokenResult(token, expiresAt)
    2. GenerateRefreshToken(userId, ip) → RefreshToken.Create(...)
    3. Track: _refreshTokens.AddAsync(refreshToken, ct)  ← stages insert, no DB write yet
    4. await _uow.SaveChangesAsync()  ← ONE transaction: user row + refresh token row
    5. return AuthResponse(accessToken.Token, refreshToken.Token, accessToken.ExpiresAt, ...)
    │
    ▼
AuthController returns 201 Created with AuthResponse body
```

**Response:**
```json
{
  "accessToken": "eyJ...",
  "refreshToken": "dGhpcyBpcyBh...",
  "accessTokenExpiresAt": "2026-04-10T14:15:00Z",
  "refreshTokenExpiresAt": "2026-05-10T14:00:00Z",
  "user": { "id": "...", "email": "alice@example.com", ... }
}
```

---

## 2. Login — `POST /api/v1/auth/login`

```
AuthService.LoginAsync()
    1. Normalize email
    2. Load user: await _users.GetByEmailAsync(email)
       → throws InvalidCredentialsException (401) if not found
    3. Verify password: BCrypt.Verify(req.Password, user.PasswordHash)
       → throws InvalidCredentialsException (401) if wrong
    4. Check: if (!user.IsActive) → throws InvalidCredentialsException (401)
    5. user.RecordLogin() — updates LastLoginAtUtc and UpdatedAtUtc
    6. await _users.UpdateAsync(user)
    7. IssueTokenPair(user, ip, ct) → same as registration
```

**Security note on step 2 and 3:** Both a missing user and a wrong password throw the **same exception** (`InvalidCredentialsException`) with the same message. This is deliberate — it prevents **user enumeration** (an attacker discovering which emails are registered by getting different error messages).

---

## 3. Token Refresh — `POST /api/v1/auth/refresh`

This is the most security-sensitive operation. Read carefully.

```
Request body: { "refreshToken": "dGhpcyBpcyBh..." }

AuthService.RefreshTokenAsync()
    1. Load token: await _refreshTokens.GetByTokenAsync(refreshToken)
       → throws TokenExpiredException (401) if not found in DB

    2. if (token.IsRevoked):
       ← SECURITY EVENT: Someone used an already-rotated token
       → RevokeAllForUserAsync(token.UserId, ip)  ← revoke ALL tokens for this user
       → throw TokenReuseException (401)

    3. if (token.IsExpired):
       → throw TokenExpiredException (401)

    4. Revoke old token:
       token.RevokedAtUtc = DateTime.UtcNow
       token.RevokedByIp  = ip

    5. Generate new tokens:
       newRefreshToken = _tokenService.GenerateRefreshToken(user.Id, ip)
       token.ReplacedByToken = newRefreshToken.Token  ← audit trail

    6. Stage both changes:
       await _refreshTokens.UpdateAsync(token)        ← tracks revocation, no DB write
       await _refreshTokens.AddAsync(newRefreshToken) ← tracks new token, no DB write
       await _uow.SaveChangesAsync()  ← ONE transaction: both rows committed atomically

    7. Generate new access token: AccessTokenResult(token, expiresAt)
    8. Return AuthResponse with new pair
```

**Token Rotation:** Each refresh consumes the current token and produces a new one. The old token is revoked and linked to its replacement via `ReplacedByToken`.

**Reuse Detection:** If a token is used *after* it has already been rotated (step 2), it means either:
- The client code has a bug, OR
- An attacker stole a refresh token

The response is to revoke **every token** for that user, forcing a full re-login. This is defense in depth.

---

## 4. Logout — `POST /api/v1/auth/logout`

```
Requires: Authorization: Bearer <access_token>
Request body: { "refreshToken": "dGhpcyBpcyBh..." }

AuthService.LogoutAsync()
    1. Load token from DB
    2. if token is null or already inactive → return silently (idempotent)
    3. token.RevokedAtUtc = DateTime.UtcNow
    4. token.RevokedByIp = ip
    5. await _refreshTokens.UpdateAsync(token)
```

**Why silently succeed on an invalid token?** If logout throws an error when the token is already revoked, a client that calls logout twice would get an error the second time. Making it idempotent is safer and simpler for clients.

Note: Logout only revokes the **provided refresh token**. The access token continues to work until its 15-minute expiry. This is a known trade-off with JWTs — there's no built-in "revoke access token" mechanism without a blocklist.

---

## 5. Get Current User — `GET /api/v1/auth/me`

```
Requires: Authorization: Bearer <access_token>

AuthController.Me()
    1. JWT middleware validates the Bearer token automatically
    2. Extract userId from claims:
       User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")
    3. await _auth.GetCurrentUserAsync(userId, ct)
    4. Return UserDto

AuthService.GetCurrentUserAsync()
    1. await _users.GetByIdAsync(userId)
       → throws UserNotFoundException (404) if not found
    2. return ToUserDto(user)
```

The user ID in the JWT claim is trusted because the JWT signature was verified by the middleware before the controller runs.

---

## The Full Token Lifecycle (Diagram)

```
REGISTER / LOGIN
     │
     ▼
┌──────────────┐         ┌──────────────────┐
│ Access Token │         │  Refresh Token   │
│  (15 min)    │         │   (30 days)      │
│  JWT, client │         │  Opaque, in DB   │
└──────────────┘         └──────────────────┘
       │                          │
       │ use on every request     │ use when access token expires
       ▼                          ▼
  [Authorize]              POST /refresh
  endpoints                      │
                                  ▼
                         Old token → REVOKED
                         New token → ISSUED
                                  │
                          ┌───────┴───────┐
                          │ New pair sent │
                          └───────────────┘

If stolen token is replayed:
     ▼
ALL tokens for user REVOKED
     ▼
User must log in again
```
