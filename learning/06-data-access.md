# Data Access — EF Core, Repositories, and Migrations

## The Stack

- **ORM**: Entity Framework Core (EF Core)
- **Pattern**: Repository Pattern (abstracts EF Core behind interfaces)
- **Configuration**: Fluent API via `IEntityTypeConfiguration<T>`
- **Migrations**: Code-first (schema generated from C# classes)

---

## AppDbContext

**File**: `src/KanAuth.Infrastructure/Data/AppDbContext.cs`

```csharp
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
```

`DbSet<T>` is EF Core's "table handle" — it exposes LINQ queries against the `Users` or `RefreshTokens` table.

`ApplyConfigurationsFromAssembly` scans the assembly and applies all classes that implement `IEntityTypeConfiguration<T>`. This keeps `AppDbContext` clean — you don't configure tables here.

---

## Entity Configurations (Fluent API)

Instead of data annotations (`[Required]`, `[MaxLength]`), this project uses separate configuration classes. This keeps the domain entities clean of infrastructure concerns.

### UserConfiguration

**File**: `src/KanAuth.Infrastructure/Data/Configurations/UserConfiguration.cs`

```csharp
public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Email)
            .IsRequired()
            .HasMaxLength(320);       // RFC 5321 max email length

        builder.HasIndex(u => u.Email)
            .IsUnique();              // DB-level uniqueness constraint

        builder.Property(u => u.PasswordHash).IsRequired();
        builder.Property(u => u.FirstName).IsRequired().HasMaxLength(100);
        builder.Property(u => u.LastName).IsRequired().HasMaxLength(100);

        builder.HasMany(u => u.RefreshTokens)
            .WithOne(rt => rt.User)
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.Cascade);  // deleting a user deletes their tokens
    }
}
```

**Key choices:**
- Email has a **unique DB index** — even if application code checks first, the DB is the final guarantor
- `Cascade` delete — if a user is deleted, all their refresh tokens go too (no orphan rows)

### RefreshTokenConfiguration

**File**: `src/KanAuth.Infrastructure/Data/Configurations/RefreshTokenConfiguration.cs`

```csharp
builder.HasKey(rt => rt.Id);

builder.Property(rt => rt.Token)
    .IsRequired()
    .HasMaxLength(200);

builder.HasIndex(rt => rt.Token)
    .IsUnique();                   // each token string is globally unique

builder.Property(rt => rt.CreatedByIp).IsRequired().HasMaxLength(45);  // max IPv6 length
builder.Property(rt => rt.RevokedByIp).HasMaxLength(45);               // optional
builder.Property(rt => rt.ReplacedByToken).HasMaxLength(200);          // optional
```

`HasMaxLength(45)` for IP addresses accommodates IPv6 (max 39 chars) with some buffer.

---

## The Repository Pattern

Instead of controllers or services calling `_db.Users.Where(...)` directly, all DB access goes through repository interfaces.

**Why?**
1. **Testability** — swap the real DB for an in-memory fake in tests
2. **Encapsulation** — EF Core details (`.Include()`, `.ExecuteUpdateAsync()`) stay in Infrastructure
3. **Single Responsibility** — one place to change if the DB query needs tuning

### UserRepository

**File**: `src/KanAuth.Infrastructure/Repositories/UserRepository.cs`

```csharp
// AsNoTracking — only used by GetCurrentUserAsync (read-only, no update follows)
public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
    _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);

// No AsNoTracking — LoginAsync reads this entity and immediately updates it
public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default) =>
    _db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default) =>
    _db.Users.AnyAsync(u => u.Email == email, ct);

// Only attaches if detached — allows EF change tracker to generate minimal UPDATE
public Task UpdateAsync(User user, CancellationToken ct = default)
{
    if (_db.Entry(user).State == EntityState.Detached)
        _db.Users.Update(user);
    return Task.CompletedTask;  // no SaveChanges — caller (AuthService) commits via IUnitOfWork
}
```

`EmailExistsAsync` uses `.AnyAsync()` instead of `.FirstOrDefaultAsync()` — it stops at the first match and doesn't load the full entity. Faster for a simple existence check.

**Why no `AsNoTracking` on `GetByEmailAsync`?** `LoginAsync` reads the user and immediately calls `RecordLogin()` then `UpdateAsync`. With a tracked entity, EF Core's change tracker sees only `LastLoginAtUtc` and `UpdatedAtUtc` changed and generates `UPDATE Users SET LastLoginAtUtc=..., UpdatedAtUtc=... WHERE Id=...`. With a detached entity, `Update()` marks every property dirty and generates a full-column UPDATE — wasteful and potentially dangerous if another request updated a different column concurrently.

**Why do repository methods return `Task.CompletedTask` instead of calling `SaveChangesAsync`?** Repositories only track changes in the EF Core change tracker. Committing is done explicitly by `AuthService` via `IUnitOfWork.SaveChangesAsync()`. This means multiple tracked changes (e.g., user insert + refresh token insert) can be committed in a single database transaction.

### RefreshTokenRepository

**File**: `src/KanAuth.Infrastructure/Repositories/RefreshTokenRepository.cs`

```csharp
public Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken ct = default) =>
    _db.RefreshTokens
        .Include(rt => rt.User)   // ← eager load the related User in the same query
        .FirstOrDefaultAsync(rt => rt.Token == token, ct);
```

`.Include(rt => rt.User)` is a JOIN — it fetches the `User` row alongside the `RefreshToken` in one SQL query. Without it, accessing `token.User` would trigger a separate "lazy load" query (or throw if lazy loading is disabled).

### Bulk Revocation (EF Core 7+ feature)

```csharp
public async Task RevokeAllForUserAsync(Guid userId, string revokedByIp, CancellationToken ct = default)
{
    var now = DateTime.UtcNow;
    await _db.RefreshTokens
        .Where(rt => rt.UserId == userId && rt.RevokedAtUtc == null)
        .ExecuteUpdateAsync(s => s
            .SetProperty(rt => rt.RevokedAtUtc, now)
            .SetProperty(rt => rt.RevokedByIp, revokedByIp), ct);
}
```

`.ExecuteUpdateAsync()` generates a single SQL `UPDATE` statement — it does **not** load entities into memory first. This is efficient for bulk operations. Compare to the naive approach which would load all tokens, modify each, then save — N+1 problem.

---

## Migrations

**Folder**: `src/KanAuth.Infrastructure/Migrations/`

EF Core migrations track schema changes as C# files. The workflow:

```bash
# When you change an entity or config, generate a migration:
dotnet ef migrations add <MigrationName> --project src/KanAuth.Infrastructure --startup-project src/KanAuth.API

# Apply migrations to the database:
dotnet ef database update --project src/KanAuth.Infrastructure --startup-project src/KanAuth.API
```

The project has auto-migration enabled:
```csharp
// Program.cs
if (shouldMigrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}
```

`Database:AutoMigrate: true` in config (or `--migrate` argument) triggers this on startup. Useful for containers/CI — the app migrates itself on first run.

---

## The Database Schema (What Gets Created)

```sql
CREATE TABLE Users (
    Id            GUID/UUID PRIMARY KEY,
    Email         VARCHAR(320) NOT NULL UNIQUE,
    PasswordHash  TEXT NOT NULL,
    FirstName     VARCHAR(100) NOT NULL,
    LastName      VARCHAR(100) NOT NULL,
    CreatedAtUtc  DATETIME NOT NULL,
    UpdatedAtUtc  DATETIME NOT NULL,
    IsActive      BOOLEAN NOT NULL,
    LastLoginAtUtc DATETIME NULL
);

CREATE TABLE RefreshTokens (
    Id              GUID/UUID PRIMARY KEY,
    UserId          GUID NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    Token           VARCHAR(200) NOT NULL UNIQUE,
    ExpiresAtUtc    DATETIME NOT NULL,
    CreatedAtUtc    DATETIME NOT NULL,
    RevokedAtUtc    DATETIME NULL,
    ReplacedByToken VARCHAR(200) NULL,
    CreatedByIp     VARCHAR(45) NOT NULL,
    RevokedByIp     VARCHAR(45) NULL
);
```

---

## Try It Yourself

1. In `RefreshTokenRepository.GetByTokenAsync`, what SQL does `.Include(rt => rt.User)` generate? (Think: JOIN vs two separate SELECTs)
2. Why does `EmailExistsAsync` use `.AnyAsync()` instead of `.FirstOrDefaultAsync() != null`?
3. If you removed `OnDelete(DeleteBehavior.Cascade)`, what would happen when you delete a user who has refresh tokens?
