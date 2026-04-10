# Clean Architecture — The Layer Structure

## The Core Idea

Clean Architecture (by Robert C. Martin) separates code into layers based on **how stable it is**. Business rules are the most stable — they should never change because a database or framework changed. So they live at the center, with no external dependencies.

```
         ┌─────────────────────────────────┐
         │           KanAuth.API           │  ← changes when HTTP details change
         │  ┌───────────────────────────┐  │
         │  │  KanAuth.Infrastructure   │  │  ← changes when DB/ORM changes
         │  │  ┌─────────────────────┐  │  │
         │  │  │ KanAuth.Application │  │  │  ← changes when use cases change
         │  │  │  ┌───────────────┐  │  │  │
         │  │  │  │ KanAuth.Domain│  │  │  │  ← changes only when core rules change
         │  │  │  └───────────────┘  │  │  │
         │  │  └─────────────────────┘  │  │
         │  └───────────────────────────┘  │
         └─────────────────────────────────┘
```

---

## Layer-by-Layer Breakdown

### 1. KanAuth.Domain — The Core

**No NuGet dependencies at all.**

Contains:
- **Entities**: `User`, `RefreshToken` — the real business objects
- **Exceptions**: `DomainException`, `InvalidCredentialsException`, `TokenExpiredException`, `TokenReuseException`, `UserNotFoundException`

The `User` entity enforces its own rules. For example, it normalizes email to lowercase and sets `IsActive = true` on creation. This logic lives here, not in a controller.

```csharp
// Domain knows what a valid User looks like
public static User Create(string email, string passwordHash, ...)
{
    return new User
    {
        Email = email.ToLowerInvariant(),  // enforced here, always
        IsActive = true,
        ...
    };
}
```

**Key insight**: If you change ORMs from EF Core to Dapper, the Domain layer is untouched.

---

### 2. KanAuth.Application — The Use Cases

**Depends on Domain only.**

Contains:
- **Interfaces**: `IAuthService`, `ITokenService`, `IUserRepository`, `IRefreshTokenRepository`
- **Services**: `AuthService`, `TokenService` — implement the actual auth logic
- **DTOs**: `RegisterRequest`, `LoginRequest`, `AuthResponse`, `UserDto`
- **Validators**: `RegisterRequestValidator`, `LoginRequestValidator`
- **Settings**: `JwtSettings`

This layer defines *what* the application can do (via interfaces) and *how* it does it (via service implementations). Crucially, `AuthService` only talks to `IUserRepository` — it has no idea whether that's EF Core, a REST call, or an in-memory list.

```csharp
// AuthService only knows interfaces, not concrete classes
public class AuthService : IAuthService
{
    private readonly IUserRepository _users;          // interface!
    private readonly IRefreshTokenRepository _tokens; // interface!
    private readonly ITokenService _tokenService;     // interface!
}
```

**Key insight**: You could write unit tests for `AuthService` by injecting fake repository implementations — no database needed.

---

### 3. KanAuth.Infrastructure — The Plumbing

**Depends on Application (for interfaces it implements) and Domain (for entities).**

Contains:
- **AppDbContext**: The EF Core database context
- **Repositories**: `UserRepository`, `RefreshTokenRepository` — concrete implementations
- **Entity Configurations**: `UserConfiguration`, `RefreshTokenConfiguration`
- **Migrations**: auto-generated EF Core schema migrations
- **DependencyInjection.cs**: Registers all services into the DI container

Infrastructure is the only place EF Core is imported. If you swap to a different ORM, only this project changes.

---

### 4. KanAuth.API — The Entry Point

**Depends on Application (for service interfaces) and Infrastructure (to call `AddInfrastructure`).**

Contains:
- **Controllers**: `AuthController`
- **Middleware**: `ExceptionHandlingMiddleware`
- **Program.cs**: Startup, DI setup, middleware pipeline

The controller is deliberately thin. It extracts the client IP, calls the service, and returns an HTTP result. No business logic lives here.

```csharp
[HttpPost("register")]
public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
{
    var response = await _auth.RegisterAsync(req, GetClientIp(), ct);
    return CreatedAtAction(nameof(Me), response);  // just maps result to HTTP
}
```

---

## Why This Structure Matters for Learning

When you read a bug or add a feature, this structure tells you **exactly where to look**:

| Question | Look In |
|----------|---------|
| How is a User stored in the DB? | Infrastructure → `UserConfiguration.cs` |
| What does registration actually do? | Application → `AuthService.RegisterAsync` |
| What fields does a User have? | Domain → `User.cs` |
| What HTTP status is returned on bad login? | API → `ExceptionHandlingMiddleware.cs` |
| How is JWT validated on incoming requests? | API → `Program.cs` (JWT middleware config) |
