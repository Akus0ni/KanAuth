# Dependency Injection — How Everything Is Wired Together

## What Is Dependency Injection?

Instead of a class creating its own dependencies (`new UserRepository()`), dependencies are **provided from outside**. This makes classes:
- Testable (inject fakes/mocks in tests)
- Loosely coupled (depend on interfaces, not concrete classes)
- Easily swappable (change implementation without touching consumers)

ASP.NET Core has a built-in DI container. You register services during startup, and the container creates and injects them automatically.

---

## The Three Lifetimes

| Lifetime | Meaning | Use When |
|----------|---------|----------|
| `Singleton` | One instance for the whole app | Stateless, thread-safe services (e.g., config) |
| `Scoped` | One instance per HTTP request | DB contexts, repositories, services that need request scope |
| `Transient` | New instance every time | Lightweight, stateless utilities |

**All repos and services in KanAuth are `Scoped`.**

Why? `AppDbContext` is scoped (EF Core standard). Everything that depends on it must also be scoped — otherwise you get a "captive dependency" bug where a singleton holds a stale DB context.

---

## Where Registration Happens

**File**: `src/KanAuth.Infrastructure/DependencyInjection.cs`

```csharp
public static IServiceCollection AddInfrastructure(
    this IServiceCollection services,
    IConfiguration configuration)
{
    // 1. Register EF Core AppDbContext
    services.AddDbContext<AppDbContext>(options => { ... });

    // 2. IUnitOfWork — resolved from the same scoped AppDbContext instance
    services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());

    // 3. Register repositories (interface → concrete class)
    services.AddScoped<IUserRepository,         UserRepository>();
    services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

    // 4. Register application services
    services.AddScoped<IAuthService,  AuthService>();
    services.AddScoped<ITokenService, TokenService>();

    return services;
}
```

This is an **extension method** on `IServiceCollection`. It keeps `Program.cs` clean — one line (`AddInfrastructure`) sets up the entire infrastructure layer.

**In `Program.cs`:**
```csharp
builder.Services.AddInfrastructure(builder.Configuration);
```

---

## The Full DI Graph

When a request hits `POST /register`, the container builds this object graph:

```
AuthController
    └── IAuthService → AuthService
            ├── IUnitOfWork → AppDbContext  (same instance as below — one commit saves all)
            ├── IUserRepository → UserRepository
            │       └── AppDbContext
            ├── IRefreshTokenRepository → RefreshTokenRepository
            │       └── AppDbContext (same instance — they share the DB context!)
            └── ITokenService → TokenService
                    └── IOptions<JwtSettings>  (singleton, from config)
```

The container resolves all of this automatically. You never write `new AuthService(new UserRepository(new AppDbContext(...)))`.

---

## How Interfaces Enable Testing

Because `AuthService` only knows `IUserRepository`, you can write tests like:

```csharp
// A fake repository that doesn't touch the database
var fakeUsers = new FakeUserRepository();
fakeUsers.Add(User.Create("test@test.com", hash, "Test", "User"));

var authService = new AuthService(fakeUsers, fakeTokens, fakeTokenService);

var result = await authService.LoginAsync(req, "127.0.0.1");
// Assert on result without ever hitting a database
```

This is why the Application layer defines interfaces — to enable this decoupling.

---

## The Multi-Database Setup

**File**: `src/KanAuth.Infrastructure/DependencyInjection.cs`

```csharp
var provider = configuration["Database:Provider"] ?? "sqlite";

services.AddDbContext<AppDbContext>(options =>
{
    switch (provider.ToLowerInvariant())
    {
        case "sqlserver":  options.UseSqlServer(connectionString, ...); break;
        case "postgresql": options.UseNpgsql(connectionString, ...);    break;
        case "mysql":      options.UseMySql(connectionString, ...);     break;
        case "sqlite":     options.UseSqlite(connectionString, ...);    break;
    }
});
```

The database provider is chosen at runtime from configuration. To switch from SQLite (local dev) to PostgreSQL (production), you only change config — no code changes. This is the **Open/Closed Principle** in practice.

---

## How JWT Auth Is Registered

**File**: `src/KanAuth.API/Program.cs`

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ValidateIssuer   = true,  ValidIssuer   = jwtSection["Issuer"],
            ValidateAudience = true,  ValidAudience = jwtSection["Audience"],
            ValidateLifetime = true,
            ClockSkew        = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();
```

This registers the JWT Bearer middleware. Any controller action decorated with `[Authorize]` will require a valid JWT. The middleware automatically:
1. Reads the `Authorization: Bearer <token>` header
2. Validates the token using the parameters above
3. Populates `HttpContext.User` with the claims from the token

---

## The Middleware Registration Order Matters

```csharp
app.UseExceptionHandlingMiddleware(); // must be FIRST — catches everything
app.UseForwardedHeaders();            // read X-Forwarded-For before routing
app.UseHttpsRedirection();
app.UseIpRateLimiting();             // before auth — reject throttled requests early
app.UseAuthentication();             // verify JWT
app.UseAuthorization();              // check [Authorize] attribute
app.MapControllers();                // route to controller
```

Order is critical. `UseAuthentication` must come before `UseAuthorization`. `UseExceptionHandlingMiddleware` must wrap everything so it catches exceptions from anywhere in the pipeline.

---

## Try It Yourself

1. What would happen if you registered `AppDbContext` as `Singleton` instead of `Scoped`?
2. Why does `TokenService` need `IOptions<JwtSettings>` instead of just `JwtSettings`?
   (Hint: `IOptions<T>` is the ASP.NET Core way to bind config sections — it's singleton-safe)
3. Add a new fake repository for `IUserRepository` on paper — what methods would it need to implement?
