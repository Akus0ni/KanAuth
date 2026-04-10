# KanAuth — Project Overview

## What Is This Project?

KanAuth is a **production-ready JWT authentication API** built with ASP.NET Core. It handles:

- User registration and login
- Issuing short-lived **access tokens** (JWT, 15 min)
- Issuing long-lived **refresh tokens** (opaque, 30 days)
- Rotating refresh tokens securely (with reuse detection)
- Logout (token revocation)
- Fetching the currently authenticated user's profile

Think of it as the "auth service" you would plug in front of any application that needs user accounts.

---

## Why Study This Project?

This project is a **learning goldmine** because it demonstrates real-world patterns used in enterprise .NET backends:

| Pattern | Where Used |
|---------|-----------|
| Clean Architecture | 4-layer project structure |
| Repository Pattern | `IUserRepository`, `IRefreshTokenRepository` |
| Interface Segregation | Application layer only depends on abstractions |
| Factory Method | `User.Create(...)` static factory |
| Middleware Pipeline | `ExceptionHandlingMiddleware` |
| Token Rotation Security | Refresh token reuse detection |
| FluentValidation | Input validation decoupled from controllers |
| EF Core Fluent API | Entity configurations in separate classes |

---

## The 4 Projects (Layers)

```
KanAuth/
├── src/
│   ├── KanAuth.Domain          ← Core business rules, no dependencies
│   ├── KanAuth.Application     ← Use cases, interfaces, DTOs, validators
│   ├── KanAuth.Infrastructure  ← EF Core, repositories, DB config
│   └── KanAuth.API             ← HTTP controllers, middleware, startup
└── tests/
    └── KanAuth.Tests           ← xUnit unit tests (59 tests, no DB required)
```

### Dependency Rule (Critical!)
Dependencies always point **inward**. Outer layers know about inner layers, never the reverse:

```
API → Application → Domain
Infrastructure → Application → Domain
```

`Domain` has zero external dependencies. `Application` only knows interfaces — it has no idea EF Core exists.

---

## The API Endpoints

| Method | Route | Auth Required | What It Does |
|--------|-------|--------------|--------------|
| POST | `/api/v1/auth/register` | No | Create account, get tokens |
| POST | `/api/v1/auth/login` | No | Verify credentials, get tokens |
| POST | `/api/v1/auth/logout` | Yes (Bearer) | Revoke refresh token |
| POST | `/api/v1/auth/refresh` | No | Swap refresh token for new tokens |
| GET | `/api/v1/auth/me` | Yes (Bearer) | Get current user profile |
| GET | `/health` | No | DB health check |

---

## Learning Path

Read the files in this order:

1. `01-clean-architecture.md` — Understand the layer structure
2. `02-domain-layer.md` — Entities and business rules
3. `03-jwt-tokens.md` — How JWT and refresh tokens work
4. `04-auth-flow.md` — Full request lifecycle, end-to-end
5. `05-dependency-injection.md` — How everything is wired together
6. `06-data-access.md` — EF Core, repositories, migrations
7. `07-security-patterns.md` — Token rotation, BCrypt, rate limiting
8. `08-middleware-and-pipeline.md` — Middleware, validation, error handling
9. `09-tests.md` — Test project structure, what is tested and how
