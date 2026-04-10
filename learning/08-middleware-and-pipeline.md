# Middleware and the Request Pipeline

## What Is Middleware?

In ASP.NET Core, a request passes through a **pipeline of middleware components** before reaching a controller. Each middleware can:
- Inspect or modify the request
- Short-circuit (return a response without calling the next middleware)
- Call the next middleware and then inspect/modify the response

```
Request → [MW1] → [MW2] → [MW3] → Controller → [MW3] → [MW2] → [MW1] → Response
```

Each middleware wraps the rest of the pipeline — this is why order matters.

---

## The Middleware Pipeline in KanAuth

**File**: `src/KanAuth.API/Program.cs`

```csharp
app.UseExceptionHandlingMiddleware(); // 1. catch all exceptions
app.UseForwardedHeaders();            // 2. read real client IP from load balancer
app.UseHttpsRedirection();            // 3. redirect HTTP to HTTPS
app.UseIpRateLimiting();             // 4. block throttled IPs
app.UseAuthentication();             // 5. validate JWT, populate HttpContext.User
app.UseAuthorization();              // 6. enforce [Authorize] attribute
// security headers added here as inline middleware
app.MapControllers();                // 7. route to controller action
```

Order matters critically. Examples of what breaks if you swap:
- `UseAuthentication` after `UseAuthorization`: `[Authorize]` checks fire before identity is established — everything gets 401
- `UseExceptionHandlingMiddleware` not first: exceptions from auth or rate limiting bypass error formatting

---

## The ExceptionHandlingMiddleware

**File**: `src/KanAuth.API/Middleware/ExceptionHandlingMiddleware.cs`

This is a **global exception handler**. Instead of try/catch in every controller, one middleware catches everything.

```csharp
public async Task InvokeAsync(HttpContext context)
{
    try
    {
        await _next(context);  // run the rest of the pipeline
    }
    catch (Exception ex)
    {
        await HandleExceptionAsync(context, ex);
    }
}
```

When an exception is caught, it first maps the exception type to an HTTP status, then logs at the appropriate level:

```csharp
var (statusCode, title, detail) = exception switch
{
    InvalidCredentialsException => (401, "Unauthorized",        exception.Message),
    TokenExpiredException       => (401, "Unauthorized",        exception.Message),
    TokenReuseException         => (401, "Unauthorized",        exception.Message),
    UserNotFoundException        => (404, "Not Found",           exception.Message),
    ValidationException ve       => (400, "Validation Failed",  FormatValidationErrors(ve)),
    InvalidOperationException    => (409, "Conflict",           exception.Message),
    _                            => (500, "Internal Server Error", <safe message>)
};

// LogError only for genuine surprises — domain exceptions are expected business outcomes
if (statusCode == 500)
    _logger.LogError(exception, "Unhandled exception on {Method} {Path}", ...);
else
    _logger.LogWarning("Request failed with {StatusCode}: {Message} on {Method} {Path}", ...);
```

**Why split log levels?** A wrong password is a normal event — logging it as `Error` floods error dashboards, making real failures hard to spot. `LogWarning` with the path and status code gives enough signal to detect patterns (e.g., repeated 401s on `/login` = brute-force attempt) without alerting on every bad password. Only truly unexpected exceptions (status 500) warrant `LogError` with the full exception object (stack trace included).

The response follows **RFC 7807 Problem Details** format:

```json
{
  "type": "https://httpstatuses.com/401",
  "title": "Unauthorized",
  "status": 401,
  "detail": "Invalid credentials.",
  "instance": "/api/v1/auth/login"
}
```

**Production safety**: For unhandled exceptions (the `_` case), if the app is in production, a generic message is returned instead of the real exception message. This prevents leaking stack traces or internal details to attackers.

```csharp
_ => (500, "Internal Server Error", _env.IsProduction()
    ? "An unexpected error occurred."   // production: safe
    : exception.Message)                // development: full detail
```

### The Extension Method Pattern

```csharp
public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseExceptionHandlingMiddleware(this IApplicationBuilder app) =>
        app.UseMiddleware<ExceptionHandlingMiddleware>();
}
```

This wraps the standard `app.UseMiddleware<T>()` call in a named extension method. The caller writes `app.UseExceptionHandlingMiddleware()` instead of `app.UseMiddleware<ExceptionHandlingMiddleware>()`. More readable, same result.

---

## FluentValidation — Input Validation

**Package**: `FluentValidation.AspNetCore`

FluentValidation runs automatically before controllers when registered with:

```csharp
builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();
```

### RegisterRequestValidator

**File**: `src/KanAuth.Application/Validators/RegisterRequestValidator.cs`

```csharp
public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);

        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(320);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128)
            .Matches("[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
            .Matches("[0-9]").WithMessage("Password must contain at least one digit.")
            .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain at least one special character.");
    }
}
```

When validation fails, FluentValidation throws a `ValidationException`. The `ExceptionHandlingMiddleware` catches it and returns a 400 with all the error messages.

**Why FluentValidation instead of Data Annotations?**

Data annotations (`[Required]`, `[EmailAddress]`) are fine for simple cases but:
- Complex rules (regex + custom messages) get messy
- Rules are scattered on the DTO class itself (mixed concerns)
- FluentValidation validators are classes — they're testable and composable

---

## Forwarded Headers

**Why**: When behind a load balancer (AWS ALB, nginx), the request's `RemoteIpAddress` is the load balancer's IP, not the real client.

```csharp
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();   // trust all proxies (configure for your network in prod)
    options.KnownProxies.Clear();
});
```

After `app.UseForwardedHeaders()`, `HttpContext.Connection.RemoteIpAddress` and `HttpContext.Request.Scheme` reflect the real client values from the `X-Forwarded-For` and `X-Forwarded-Proto` headers.

`AuthController.GetClientIp()` checks `X-Forwarded-For` first, then falls back to `RemoteIpAddress`.

---

## Auto-Migration on Startup

```csharp
var shouldMigrate = builder.Configuration.GetValue<bool>("Database:AutoMigrate")
    || args.Contains("--migrate");

if (shouldMigrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}
```

**Why `CreateScope()`?** `AppDbContext` is scoped — it's designed to live for one request. But at startup, there's no request. `CreateScope()` creates a temporary DI scope that you control, so you can resolve scoped services safely.

This is a common pattern in ASP.NET Core for any startup task that needs scoped services (seeding data, warming caches, etc.).

---

## ProducesResponseType — Self-Documenting Controllers

**File**: `src/KanAuth.API/Controllers/AuthController.cs`

Every endpoint is annotated with `[ProducesResponseType]` attributes that declare which status codes and response body types the action can return:

```csharp
[HttpPost("register")]
[ProducesResponseType(typeof(AuthResponse), StatusCodes.Status201Created)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
public async Task<IActionResult> Register(...)
```

**Why add these?**

1. **Swagger/OpenAPI** reads these attributes and generates accurate API documentation — including response body schemas for each status code, not just "200 OK"
2. **Consumers of the API** (frontend devs, integration tests, API clients) can see upfront what to expect without reading the source
3. **`[Produces("application/json")]`** on the class declares the content type once, so Swagger marks all endpoints as JSON producers

The combination produces a complete OpenAPI spec that tools like Swagger UI, Postman, and code generators can consume.

---

## Swagger / OpenAPI

```csharp
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "KanAuth API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { ... });
    c.AddSecurityRequirement(...);
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
```

Swagger is only enabled in development. The "Bearer" security definition adds a padlock icon in the Swagger UI where you paste a JWT — then all subsequent API calls include the `Authorization: Bearer <token>` header automatically.

---

## Try It Yourself

1. What response format does the API return for a 400 validation error? Trace it from `RegisterRequestValidator` through `ExceptionHandlingMiddleware`.
2. Why is `UseAuthentication` placed after `UseIpRateLimiting`? What would happen if they were swapped?
3. In `ExceptionHandlingMiddleware`, what happens to exceptions that aren't in the `switch` expression (the `_` case)?
4. Why does `MigrateAsync()` need `CreateScope()` at startup?
