# KanAuth — Identity Management Platform Plan

## Context
Build a greenfield .NET 8 Identity Management API for developers. The directory is completely empty. Core features: Signup, Login, Logout, Token Refresh, and Get Current User — all JWT-based. DB provider is user-configurable via a single config key (no code changes needed to switch). Deployed to AWS Elastic Beanstalk via Docker.

---

## Solution Structure

```
KanAuth/
├── KanAuth.sln
├── .gitignore
├── .dockerignore
├── Dockerfile                          ← multi-stage build
├── docker-compose.yml                  ← local dev (postgres)
├── src/
│   ├── KanAuth.Domain/                 ← pure entities, no dependencies
│   │   ├── Entities/User.cs
│   │   ├── Entities/RefreshToken.cs
│   │   └── Exceptions/                 ← DomainException, InvalidCredentialsException, etc.
│   ├── KanAuth.Application/            ← services, interfaces, DTOs
│   │   ├── Interfaces/IUserRepository.cs
│   │   ├── Interfaces/IRefreshTokenRepository.cs
│   │   ├── Interfaces/ITokenService.cs
│   │   ├── Interfaces/IAuthService.cs
│   │   ├── DTOs/Requests/              ← RegisterRequest, LoginRequest, RefreshTokenRequest
│   │   ├── DTOs/Responses/             ← AuthResponse, UserDto
│   │   ├── Services/AuthService.cs
│   │   └── Services/TokenService.cs
│   ├── KanAuth.Infrastructure/         ← EF Core, repos, DB provider switching
│   │   ├── Data/AppDbContext.cs
│   │   ├── Data/Configurations/        ← UserConfiguration, RefreshTokenConfiguration
│   │   ├── Repositories/UserRepository.cs
│   │   ├── Repositories/RefreshTokenRepository.cs
│   │   └── DependencyInjection.cs      ← multi-DB provider switch
│   └── KanAuth.API/                    ← ASP.NET Core host
│       ├── Controllers/AuthController.cs
│       ├── Middleware/ExceptionHandlingMiddleware.cs
│       ├── Program.cs
│       ├── appsettings.json
│       └── appsettings.Production.json
└── .ebextensions/
    └── 01_environment.config
```

---

## Domain Layer

### User Entity (`src/KanAuth.Domain/Entities/User.cs`)
Fields: `Id (Guid)`, `Email`, `PasswordHash`, `FirstName`, `LastName`, `CreatedAtUtc`, `UpdatedAtUtc`, `IsActive (bool)`, `LastLoginAtUtc (DateTime?)`, `RefreshTokens (navigation)`

- Private setters on `Id` and `CreatedAtUtc` (immutable)
- Static factory `User.Create(email, hash, firstName, lastName)` — initializes Id + timestamps
- Methods: `RecordLogin()`, `Deactivate()`
- Email stored lowercase

### RefreshToken Entity (`src/KanAuth.Domain/Entities/RefreshToken.cs`)
Fields: `Id`, `UserId`, `Token`, `ExpiresAtUtc`, `CreatedAtUtc`, `RevokedAtUtc?`, `ReplacedByToken?`, `CreatedByIp`, `RevokedByIp?`, `User (nav)`

Computed properties: `IsExpired`, `IsRevoked`, `IsActive`

### Domain Exceptions (`src/KanAuth.Domain/Exceptions/`)
- `DomainException` (base)
- `InvalidCredentialsException` — bad email or password (same message, don't leak which)
- `UserNotFoundException`
- `TokenExpiredException`

---

## Application Layer

### Interfaces
```csharp
// IUserRepository
Task<User?> GetByIdAsync(Guid id, CancellationToken ct);
Task<User?> GetByEmailAsync(string email, CancellationToken ct);
Task<bool> EmailExistsAsync(string email, CancellationToken ct);
Task AddAsync(User user, CancellationToken ct);
Task UpdateAsync(User user, CancellationToken ct);

// IRefreshTokenRepository
Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken ct);
Task AddAsync(RefreshToken token, CancellationToken ct);
Task UpdateAsync(RefreshToken token, CancellationToken ct);
Task RevokeAllForUserAsync(Guid userId, string revokedByIp, CancellationToken ct);

// ITokenService
string GenerateAccessToken(User user);
RefreshToken GenerateRefreshToken(Guid userId, string ipAddress);
ClaimsPrincipal? ValidateAccessToken(string token);  // null = invalid

// IAuthService
Task<AuthResponse> RegisterAsync(RegisterRequest req, string ip, CancellationToken ct);
Task<AuthResponse> LoginAsync(LoginRequest req, string ip, CancellationToken ct);
Task LogoutAsync(string refreshToken, string ip, CancellationToken ct);
Task<AuthResponse> RefreshTokenAsync(string refreshToken, string ip, CancellationToken ct);
Task<UserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct);
```

### DTOs
- `RegisterRequest(FirstName, LastName, Email, Password)` — FluentValidation: email format, password 8–128 chars
- `LoginRequest(Email, Password)`
- `RefreshTokenRequest(RefreshToken)`
- `AuthResponse(AccessToken, RefreshToken, AccessTokenExpiresAt, RefreshTokenExpiresAt, User: UserDto)`
- `UserDto(Id, Email, FirstName, LastName, CreatedAtUtc, LastLoginAtUtc?)`

### AuthService Logic

**Register:** Normalize email → check EmailExists (409 if true) → BCrypt.HashPassword(pwd, workFactor:12) → User.Create() → AddAsync → generate both tokens → AddAsync refresh token → return AuthResponse

**Login:** GetByEmail (null → InvalidCredentialsException) → BCrypt.Verify (false → InvalidCredentialsException) → check IsActive → RecordLogin() → generate tokens → return AuthResponse

**Logout:** GetByToken → if null/inactive, return silently (idempotent) → set RevokedAtUtc/RevokedByIp → UpdateAsync

**RefreshToken:** GetByToken (null → TokenExpiredException) → if IsRevoked → **token reuse detected**: RevokeAllForUser + throw SecurityException → if IsExpired → TokenExpiredException → revoke old token, set ReplacedByToken → generate new token pair → return AuthResponse

### TokenService Logic
- Access token: HS256 JWT, claims: `sub`=userId, `email`, `given_name`, `family_name`, `jti`=Guid.NewGuid(). Expiry from `JwtSettings.AccessTokenExpiryMinutes`
- Refresh token: `RandomNumberGenerator.GetBytes(64)` → base64url. Expiry from `JwtSettings.RefreshTokenExpiryDays`
- ClockSkew = `TimeSpan.Zero` on validation

---

## Infrastructure Layer

### AppDbContext (`src/KanAuth.Infrastructure/Data/AppDbContext.cs`)
Standard EF Core DbContext with `DbSet<User>` and `DbSet<RefreshToken>`. Uses `ApplyConfigurationsFromAssembly`.

### EF Core Configurations
- **UserConfiguration**: unique index on Email (max 320), cascade delete RefreshTokens
- **RefreshTokenConfiguration**: unique index on Token (max 200)

### DB Provider Switching (`src/KanAuth.Infrastructure/DependencyInjection.cs`)
```csharp
// Reads "Database:Provider" from config — case-insensitive switch:
// "sqlserver" → UseSqlServer()
// "postgresql" → UseNpgsql()
// "mysql" → UseMySql() with ServerVersion.AutoDetect()
// "sqlite" → UseSqlite()
// Unknown value → throws InvalidOperationException with helpful message
```
All providers point `MigrationsAssembly("KanAuth.Infrastructure")`.

### Repositories
- **UserRepository**: standard EF Core CRUD. GetByEmail uses `.ToLowerInvariant()`. AsNoTracking on reads.
- **RefreshTokenRepository**: GetByToken includes `.Include(rt => rt.User)`. RevokeAllForUser uses EF Core 8 `ExecuteUpdateAsync` (bulk, no entity load).

### Auto-Migration
`appsettings.json` key `Database:AutoMigrate: true` triggers `db.Database.MigrateAsync()` at startup (before `app.Run()`). Works for whichever provider is configured.

---

## API Layer

### NuGet Packages (KanAuth.API.csproj)
- `Microsoft.AspNetCore.Authentication.JwtBearer 8.*`
- `Swashbuckle.AspNetCore 6.*`
- `AspNetCoreRateLimit 5.*`
- `FluentValidation.AspNetCore 11.*`
- `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore 8.*`

### Program.cs Middleware Order
1. `UseExceptionHandlingMiddleware` (must be first)
2. `UseSwagger` / `UseSwaggerUI` (dev only)
3. `UseForwardedHeaders` (for ALB/proxy IP passthrough)
4. `UseHttpsRedirection`
5. `UseIpRateLimiting`
6. `UseAuthentication`
7. `UseAuthorization`
8. `MapControllers`
9. `MapHealthChecks("/health")`

### AuthController (`[Route("api/v1/auth")]`)

| Method | Path | Auth | Returns |
|---|---|---|---|
| POST | `/register` | No | 201 + AuthResponse |
| POST | `/login` | No | 200 + AuthResponse |
| POST | `/logout` | Yes | 204 |
| POST | `/refresh` | No | 200 + AuthResponse |
| GET | `/me` | Yes | 200 + UserDto |

IP address helper: check `X-Forwarded-For` header first (for ALB), fallback to `RemoteIpAddress`.

### ExceptionHandlingMiddleware
Maps typed exceptions → RFC 7807 Problem Details JSON:
- `InvalidCredentialsException` → 401
- `TokenExpiredException` → 401
- `UserNotFoundException` → 404
- `ValidationException` → 400 (includes field errors)
- `InvalidOperationException` → 409
- `SecurityException` (token reuse) → 401
- Any other `Exception` → 500 (no details in prod)

### appsettings.json Key Structure
```json
{
  "Database": { "Provider": "sqlite", "AutoMigrate": true },
  "ConnectionStrings": { "DefaultConnection": "Data Source=kanauth.db" },
  "Jwt": {
    "Secret": "CHANGE_IN_PROD",
    "Issuer": "KanAuth",
    "Audience": "KanAuth.Clients",
    "AccessTokenExpiryMinutes": 15,
    "RefreshTokenExpiryDays": 30
  },
  "IpRateLimiting": {
    "EnableEndpointRateLimiting": true,
    "GeneralRules": [
      { "Endpoint": "POST:/api/v1/auth/login",    "Period": "1m", "Limit": 10 },
      { "Endpoint": "POST:/api/v1/auth/register", "Period": "1h", "Limit": 5  },
      { "Endpoint": "POST:/api/v1/auth/refresh",  "Period": "1m", "Limit": 20 }
    ]
  }
}
```
Production overrides come from environment variables (double-underscore notation: `Jwt__Secret`, `ConnectionStrings__DefaultConnection`, `Database__Provider`).

---

## AWS Elastic Beanstalk Deployment

### Dockerfile (multi-stage)
- **Stage 1 (sdk:8.0):** Copy .csproj files first for layer caching, `dotnet restore`, copy source, `dotnet publish -c Release`
- **Stage 2 (aspnet:8.0):** Non-root user (`appuser:appgroup`), `EXPOSE 80`, `ENTRYPOINT ["dotnet", "KanAuth.API.dll", "--migrate"]`

The `--migrate` flag runs `db.Database.MigrateAsync()` before server starts, keeping schema current on every deploy.

### docker-compose.yml (local dev)
Spins up API + PostgreSQL. API waits for DB health check. All secrets via environment variables. No secrets in source.

### .ebextensions/01_environment.config
Sets `ASPNETCORE_ENVIRONMENT=Production`. **Secrets (Jwt__Secret, connection string) set via `eb setenv` or EB Console — never in config files.**

### Deployment Commands
```bash
eb init KanAuth --platform "Docker" --region us-east-1
eb create kanauth-prod --elb-type application --min-instances 2 --max-instances 4
eb setenv Jwt__Secret="<generated>" ConnectionStrings__DefaultConnection="<rds-string>"
eb deploy
```

Use **RDS PostgreSQL** in same VPC. TLS terminated at ALB (ACM cert), ALB → container on port 80.

---

## Security Hardening
- BCrypt work factor 12 (≈300ms, prevents brute force)
- `ClockSkew = TimeSpan.Zero` on JWT validation (no grace period)
- Same error message for unknown email vs wrong password
- Token family revocation on reuse detection
- `Jwt__Secret` minimum 32 bytes; stored in AWS Secrets Manager in production
- Password validation: uppercase + digit + special char required
- Security headers: `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`
- HSTS enabled in production

---

## Build Order (Iterative)
1. `dotnet new sln` + 4 projects + add references
2. Domain entities + exceptions (pure C#)
3. Application interfaces + DTOs
4. `TokenService` (pure logic, no DB)
5. FluentValidation validators
6. `AppDbContext` + EF Configurations
7. `DependencyInjection.cs` (provider switch)
8. Repositories
9. `AuthService`
10. `Program.cs` wiring
11. `ExceptionHandlingMiddleware`
12. `AuthController` (all 5 endpoints)
13. Swagger verification
14. `docker-compose up` local test
15. Rate limiting config
16. `eb deploy`

---

## Verification
1. **Local SQLite:** `dotnet run --project src/KanAuth.API` → hit `/swagger` → test all 5 endpoints in order (register → login → me → refresh → logout)
2. **Docker local:** `docker-compose up` → same test flow against Postgres
3. **Health check:** `GET /health` returns `{"status":"Healthy"}`
4. **Rate limit test:** 11 rapid POST `/login` requests → 11th returns 429
5. **Token reuse test:** Use a refresh token → use same refresh token again → expect 401 + all sessions revoked
6. **AWS:** `eb open` → repeat endpoint tests against prod URL
