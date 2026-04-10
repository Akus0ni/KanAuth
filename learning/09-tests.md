# Testing — Unit Tests, Mocks, and What Gets Covered

## The Test Stack

| Package | Role |
|---------|------|
| **xUnit** | Test runner and `[Fact]` / `[Theory]` attributes |
| **NSubstitute** | Mocking library — creates fake implementations of interfaces |
| **FluentAssertions** | Readable assertions: `.Should().Be(...)`, `.ThrowAsync<...>()` |
| **BCrypt.Net-Next** | Used directly in tests to hash passwords at low work factor |

**Project**: `tests/KanAuth.Tests/KanAuth.Tests.csproj`

---

## What to Test (and What Not To)

Tests cover **business logic** — the rules that determine correct behavior. They don't test:
- EF Core (the library is already tested by Microsoft)
- Framework middleware like JWT validation
- `[Authorize]` attribute behavior (integration tests would cover that)

The test boundary is: given a service with mocked dependencies, does it make the right decisions?

---

## NSubstitute — How Mocking Works

`AuthService` depends on four interfaces: `IUserRepository`, `IRefreshTokenRepository`, `ITokenService`, `IUnitOfWork`. In production these are real classes backed by a database. In tests, they're fakes created by NSubstitute:

```csharp
private readonly IUserRepository _users = Substitute.For<IUserRepository>();
private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
```

**Configuring return values:**

```csharp
// When GetByEmailAsync is called with "alice@example.com", return this user
_users.GetByEmailAsync("alice@example.com", Arg.Any<CancellationToken>())
      .Returns(user);

// When GetByEmailAsync is called with any string, return null
_users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
      .ReturnsNull();
```

**Verifying calls were made:**

```csharp
// Assert that SaveChangesAsync was called exactly once
await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
```

This is how you test side effects — you can't inspect a database, but you can verify the mock was called.

---

## AuthServiceTests — 24 Tests

**File**: `tests/KanAuth.Tests/Services/AuthServiceTests.cs`

### Setup Pattern

```csharp
// BCrypt at workFactor 4 — same algorithm, just faster for tests
private static readonly string TestPasswordHash =
    BCrypt.Net.BCrypt.HashPassword(TestPassword, workFactor: 4);

// SUT constructed fresh per test (xUnit creates a new instance per [Fact])
private readonly AuthService _sut;

public AuthServiceTests()
{
    _sut = new AuthService(_users, _refreshTokens, _tokenService, _uow);

    // Most tests don't care what tokens look like — stub them once
    _tokenService.GenerateAccessToken(Arg.Any<User>())
        .Returns(new AccessTokenResult("access.token.stub", DateTime.UtcNow.AddMinutes(15)));

    _tokenService.GenerateRefreshToken(Arg.Any<Guid>(), Arg.Any<string>())
        .Returns(RefreshToken.Create(Guid.NewGuid(), "refresh-token-stub",
            DateTime.UtcNow.AddDays(30), "127.0.0.1"));
}
```

**Why stub tokens in the constructor?** Every happy-path test goes through `IssueTokenPair`. Stubbing once in the constructor means tests focus on the one thing they're actually testing, not on token generation details.

### RegisterAsync Tests

| Test | Verifies |
|------|---------|
| `NewEmail_ReturnsAuthResponse` | Happy path returns access + user in response |
| `NewEmail_SavesOnce` | Exactly one `SaveChangesAsync` call (atomicity) |
| `EmailNormalizedToLowercase` | `"ALICE@EXAMPLE.COM"` stored as `"alice@example.com"` |
| `EmailAlreadyExists_ThrowsInvalidOperation` | Duplicate email → 409-mapped exception |

### LoginAsync Tests

| Test | Verifies |
|------|---------|
| `ValidCredentials_ReturnsAuthResponse` | Correct password → token pair returned |
| `ValidCredentials_SavesOnce` | Login commits in one transaction |
| `UserNotFound_ThrowsInvalidCredentials` | Unknown email → same exception as wrong password |
| `WrongPassword_ThrowsInvalidCredentials` | Bad password → `InvalidCredentialsException` |
| `InactiveUser_ThrowsInvalidCredentials` | Deactivated account → same exception (prevents status enumeration) |

The last three throw the **same exception** — this is intentional security behavior (see `07-security-patterns.md`).

### LogoutAsync Tests

| Test | Verifies |
|------|---------|
| `ActiveToken_RevokesAndSaves` | Token revoked, IP recorded, saved |
| `TokenNotFound_Succeeds` | Missing token → no exception (idempotent) |
| `AlreadyRevokedToken_Succeeds` | Already-revoked token → no exception (idempotent) |

**Why test idempotent logout?** A client calling logout twice (e.g., network retry) must not get an error. Tests prove this behavior is intentional, not accidental.

### RefreshTokenAsync Tests

| Test | Verifies |
|------|---------|
| `ValidToken_ReturnsNewTokenPair` | New access + refresh tokens issued |
| `ValidToken_OldTokenRevoked` | Old token's `RevokedAtUtc` is set |
| `ValidToken_SavesOnce` | Both revocation + new token commit atomically |
| `TokenNotFound_ThrowsTokenExpired` | Unknown token → 401 |
| `ExpiredToken_Throws` | Past-expiry token → `TokenExpiredException` |
| `RevokedToken_RevokesAllAndThrows` | Already-rotated token → all user tokens nuked → `TokenReuseException` |

The reuse test is the most security-critical: presenting a previously rotated token is treated as a potential compromise and triggers a nuclear response.

### GetCurrentUserAsync Tests

| Test | Verifies |
|------|---------|
| `UserExists_ReturnsUserDto` | Valid ID → mapped DTO returned |
| `UserNotFound_ThrowsUserNotFoundException` | Unknown ID → 404-mapped exception |

---

## TokenServiceTests — 10 Tests

**File**: `tests/KanAuth.Tests/Services/TokenServiceTests.cs`

Unlike `AuthService`, `TokenService` has no dependencies that need mocking — it takes config values and pure cryptography. Tests use a real `JwtSettings` object:

```csharp
private static readonly JwtSettings Settings = new()
{
    Secret = "test-secret-key-long-enough-for-hmac256-algorithm",
    Issuer = "KanAuth-Test",
    Audience = "KanAuth-Test-Clients",
    AccessTokenExpiryMinutes = 15,
    RefreshTokenExpiryDays = 30
};

private static TokenService CreateSut() => new(Options.Create(Settings));
```

**Reading a JWT in tests** — to verify claims, decode the token directly:

```csharp
var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);
jwt.Subject.Should().Be(user.Id.ToString());  // "sub" claim
```

Note: `ReadJwtToken` does NOT validate the signature — it just parses the payload. That's fine here since we're testing what the token contains, not whether it's valid.

### Access Token Tests

| Test | Verifies |
|------|---------|
| `ReturnsNonEmptyToken` | Token string is not empty |
| `TokenContainsUserIdAsSub` | `sub` claim = user's GUID |
| `TokenContainsEmail` | `email` claim present |
| `ExpiresAtMatchesConfiguredMinutes` | `ExpiresAt` aligns with `AccessTokenExpiryMinutes` config |
| `TwoCallsProduceDifferentJtis` | Each token has a unique `jti` (prevents replay across tokens) |

The `ExpiresAt` test is important — it was the bug caught in the review (`AuthResponse` was previously hardcoding `AddMinutes(15)` instead of using the value from `TokenService`).

### Refresh Token Tests

| Test | Verifies |
|------|---------|
| `TokenIsNonEmpty` | Opaque token string is not empty |
| `SetsCorrectUserId` | `UserId` property matches what was passed in |
| `SetsCorrectIp` | `CreatedByIp` property matches what was passed in |
| `ExpiresAfterConfiguredDays` | Expiry aligns with `RefreshTokenExpiryDays` config |
| `TwoCallsProduceDifferentTokens` | Each call produces a different random token |

---

## RegisterRequestValidatorTests — 14 Tests

**File**: `tests/KanAuth.Tests/Validators/RegisterRequestValidatorTests.cs`

FluentValidation provides a `TestValidate` helper that runs validation and returns a result object:

```csharp
var result = _validator.TestValidate(new RegisterRequest(...));
result.ShouldHaveValidationErrorFor(x => x.Password);
result.ShouldNotHaveAnyValidationErrors();
```

Tests use `[Theory]` with `[InlineData]` for data-driven cases:

```csharp
[Theory]
[InlineData("notanemail")]
[InlineData("@domain.com")]
[InlineData("no-at-sign")]
public void InvalidEmail_FailsValidation(string email)
{
    var result = _validator.TestValidate(new RegisterRequest("A", "B", email, ValidPassword));
    result.ShouldHaveValidationErrorFor(x => x.Email);
}
```

**Why `[Theory]` over multiple `[Fact]` methods?** Testing 3 invalid email formats is the same test structure repeated — `[Theory]` expresses this without copy-paste, and each `[InlineData]` still shows as a separate test run.

---

## ExceptionHandlingMiddlewareTests — 11 Tests

**File**: `tests/KanAuth.Tests/Middleware/ExceptionHandlingMiddlewareTests.cs`

Middleware tests are more involved because middleware interacts with `HttpContext` directly. Tests construct a minimal context:

```csharp
var context = new DefaultHttpContext();
context.Response.Body = new MemoryStream();  // capture response bytes
```

The middleware's `_next` delegate is mocked to throw a specific exception:

```csharp
var middleware = new ExceptionHandlingMiddleware(
    next: _ => throw new InvalidCredentialsException(),
    logger: Substitute.For<ILogger<ExceptionHandlingMiddleware>>(),
    env: Substitute.For<IHostEnvironment>()
);

await middleware.InvokeAsync(context);
```

After invocation, the response body is read back and parsed:

```csharp
context.Response.Body.Seek(0, SeekOrigin.Begin);
var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
var problem = JsonSerializer.Deserialize<JsonElement>(body);
problem.GetProperty("status").GetInt32().Should().Be(401);
```

### What Gets Tested

| Test | Verifies |
|------|---------|
| `InvalidCredentials → 401` | Exception mapped to correct status |
| `TokenExpired → 401` | " |
| `TokenReuse → 401` | " |
| `UserNotFound → 404` | " |
| `ValidationException → 400` | " |
| `UnhandledException → 500` | Unknown exceptions become 500 |
| `ResponseBodyIsProblemDetails` | RFC 7807 fields present in response |
| `ContentTypeIsApplicationJson` | `Content-Type: application/problem+json` header set |
| `Production_HidesExceptionDetail` | 500 in production returns generic message |
| `Development_ExposesExceptionDetail` | 500 in development returns real message |
| `LogsError_For500` | `LogError` called with exception for status 500 |

The production/development split tests are the payoff from the refactor in `ExceptionHandlingMiddleware` — they document that the behavior difference is intentional and caught by CI.

---

## Running the Tests

```bash
dotnet test tests/KanAuth.Tests
```

Or with verbose output:

```bash
dotnet test tests/KanAuth.Tests --logger "console;verbosity=normal"
```

---

## Try It Yourself

1. Add a test for `RegisterAsync` that verifies `AddAsync` was called on `_users` (not just that `SaveChangesAsync` was called).
2. In `TokenServiceTests`, why does the test use `ReadJwtToken` without signature validation? When would that be a problem?
3. What would break in `ExceptionHandlingMiddlewareTests` if the middleware set `Content-Type: application/json` instead of `application/problem+json`?
4. The reuse detection test verifies that all tokens are revoked. How would you verify that the right user's tokens were revoked (not just any user's)?
