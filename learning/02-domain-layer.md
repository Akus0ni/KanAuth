# The Domain Layer — Entities and Business Rules

## What Lives Here

`KanAuth.Domain` contains the two core entities and all domain exceptions. It has **zero NuGet package dependencies** — just pure C#.

---

## The User Entity

**File**: `src/KanAuth.Domain/Entities/User.cs`

```csharp
public class User
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    private User() { }  // prevents "new User()" from outside

    public static User Create(string email, string passwordHash, string firstName, string lastName) { ... }
    public void RecordLogin() { ... }
    public void Deactivate() { ... }
}
```

### Design Decisions Worth Understanding

**1. Private constructor + static factory**

`private User() { }` means you cannot write `new User()` from outside the class. You must use `User.Create(...)`. This guarantees the entity is always created in a valid state (e.g., email is always lowercased, `IsActive` is always `true`, timestamps are set).

This is the **Factory Method** pattern. It's more expressive than a constructor when creation involves logic.

**2. `private set` on immutable fields**

`Id`, `Email`, and `PasswordHash` have `private set` — they can only be set during creation, never modified afterward. This prevents accidental mutations. If a password-change feature is added later, it goes through a dedicated method (e.g., `UpdatePassword(string newHash)`) rather than direct property assignment.

**3. All timestamps are UTC**

Every datetime is suffixed `Utc` and uses `DateTime.UtcNow`. This is critical in distributed systems where servers may be in different timezones.

**4. Navigation property**

`ICollection<RefreshToken> RefreshTokens` is the EF Core navigation property. The Domain entity "knows" it has refresh tokens (which is a business fact), but it doesn't know how they're stored.

---

## The RefreshToken Entity

**File**: `src/KanAuth.Domain/Entities/RefreshToken.cs`

```csharp
public class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; set; }      // null = not revoked
    public string? ReplacedByToken { get; set; }     // token that superseded this one
    public string CreatedByIp { get; private set; } = string.Empty;
    public string? RevokedByIp { get; set; }

    public User User { get; set; } = null!;          // navigation property back to User

    // Computed properties — no storage needed
    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;
    public bool IsRevoked => RevokedAtUtc.HasValue;
    public bool IsActive  => !IsRevoked && !IsExpired;

    private RefreshToken() { }

    public static RefreshToken Create(Guid userId, string token, DateTime expiresAtUtc, string ipAddress) =>
        new() { Id = Guid.NewGuid(), UserId = userId, Token = token, ... };
}
```

### Key Concepts

**Factory pattern — consistent with `User`**

`RefreshToken` now mirrors `User`: private constructor, static `Create(...)` factory, `private set` on fields written at creation time. Fields mutated during revocation (`RevokedAtUtc`, `RevokedByIp`, `ReplacedByToken`) stay `public set` since `AuthService` legitimately writes them after creation.

**Nullable vs non-nullable fields**

- `RevokedAtUtc` is `DateTime?` — null means the token has never been revoked. This is a deliberate domain fact: a token exists in two states (active / revoked).
- `ReplacedByToken` is `string?` — only set when a new token replaces this one during a refresh.

**Computed boolean properties**

`IsExpired`, `IsRevoked`, and `IsActive` are computed from other fields. They are not stored in the database — EF Core ignores them because there's no setter. This is cleaner than calculating these conditions throughout the codebase.

**IP tracking**

Both the creation IP (`CreatedByIp`) and revocation IP (`RevokedByIp`) are recorded. This enables security auditing: if someone uses a token from an unexpected IP, it's detectable.

---

## Domain Exceptions

**Files**: `src/KanAuth.Domain/Exceptions/`

```
DomainException           ← base class for all domain errors
  ├── InvalidCredentialsException   ← wrong email/password
  ├── TokenExpiredException         ← token past its expiry
  ├── TokenReuseException           ← token used after being rotated
  └── UserNotFoundException         ← user ID not found
```

### Why Custom Exceptions?

Each exception type maps to a specific HTTP status code in `ExceptionHandlingMiddleware`. Having distinct types means the middleware can pattern-match cleanly:

```csharp
var (statusCode, title, detail) = exception switch
{
    InvalidCredentialsException => (401, "Unauthorized", ...),
    TokenExpiredException       => (401, "Unauthorized", ...),
    TokenReuseException         => (401, "Unauthorized", ...),
    UserNotFoundException       => (404, "Not Found",    ...),
    ...
};
```

If everything threw a generic `Exception`, you couldn't differentiate 401 from 404 from 500.

### Base Class Pattern

`InvalidCredentialsException`, `TokenExpiredException`, etc., all extend `DomainException`, which extends `Exception`. This lets you catch either the specific type (for precise handling) or `DomainException` (for any domain error).

---

## Try It Yourself

Open `src/KanAuth.Domain/Entities/User.cs` and answer:

1. What happens if you try to change a user's email after creation? (hint: look at `private set`)
2. Why does `RecordLogin()` update both `LastLoginAtUtc` **and** `UpdatedAtUtc`?
3. When would `Deactivate()` be called? What effect does it have at login time? (check `AuthService.LoginAsync`)
