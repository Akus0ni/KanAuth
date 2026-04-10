# KanAuth — Learning Guide

A structured, bottom-up guide to understanding this project by reading code and reasoning about decisions.

## Files

| File | Topic | What You Learn |
|------|-------|----------------|
| `00-overview.md` | Big picture | What the project does, API endpoints, the 4-layer structure |
| `01-clean-architecture.md` | Architecture | Why layers exist, dependency rules, where to look for anything |
| `02-domain-layer.md` | Domain | Entities, private constructors, domain exceptions, UTC timestamps |
| `03-jwt-tokens.md` | JWT & Tokens | JWT structure, access vs refresh tokens, TokenService |
| `04-auth-flow.md` | Auth lifecycle | Register/login/refresh/logout traced step-by-step |
| `05-dependency-injection.md` | DI | Lifetimes, interface contracts, wiring, multi-DB setup |
| `06-data-access.md` | EF Core | AppDbContext, repositories, fluent config, migrations |
| `07-security-patterns.md` | Security | BCrypt, token rotation, reuse detection, rate limiting, headers |
| `08-middleware-and-pipeline.md` | Middleware | Pipeline order, exception handling, FluentValidation, Swagger |
| `09-tests.md` | Testing | Unit test structure, NSubstitute mocks, what and why each test covers |

## Recommended Order

Start at `00` and work forward. Each file builds on the previous.

## Key Files to Read in the Codebase

After each learning file, open these corresponding source files:

```
Domain layer        → src/KanAuth.Domain/Entities/User.cs
                      src/KanAuth.Domain/Entities/RefreshToken.cs
                      src/KanAuth.Domain/Exceptions/

Auth logic          → src/KanAuth.Application/Services/AuthService.cs
Token logic         → src/KanAuth.Application/Services/TokenService.cs
Interfaces          → src/KanAuth.Application/Interfaces/

DB setup            → src/KanAuth.Infrastructure/Data/AppDbContext.cs
Entity config       → src/KanAuth.Infrastructure/Data/Configurations/
Repositories        → src/KanAuth.Infrastructure/Repositories/
DI registration     → src/KanAuth.Infrastructure/DependencyInjection.cs

HTTP layer          → src/KanAuth.API/Controllers/AuthController.cs
Error handling      → src/KanAuth.API/Middleware/ExceptionHandlingMiddleware.cs
Startup             → src/KanAuth.API/Program.cs

Tests               → tests/KanAuth.Tests/Services/AuthServiceTests.cs
                      tests/KanAuth.Tests/Services/TokenServiceTests.cs
                      tests/KanAuth.Tests/Validators/RegisterRequestValidatorTests.cs
                      tests/KanAuth.Tests/Middleware/ExceptionHandlingMiddlewareTests.cs
```
