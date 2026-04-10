# KanAuth

A greenfield .NET 8 Identity Management API. Provides JWT-based authentication with token refresh and rotation. DB provider is configurable via a single config key — no code changes needed to switch.

## Features

- Register / Login / Logout / Refresh Token / Get Current User
- JWT access tokens (HS256, 15-min expiry) + refresh tokens (30-day, rotating)
- Token-family revocation on reuse detection
- BCrypt password hashing (work factor 12)
- Configurable DB: SQLite, PostgreSQL, SQL Server, MySQL
- Auto-migration on startup (config **or** CLI flag)
- IP-based rate limiting on auth endpoints
- RFC 7807 Problem Details error responses
- Health check at `GET /health`
- Docker + AWS Elastic Beanstalk deployment

## Quick Start (SQLite)

```bash
dotnet run --project src/KanAuth.API
# Swagger UI: https://localhost:5001/swagger
```

## Quick Start (Docker / PostgreSQL)

```bash
docker-compose up
# API: http://localhost:8080/swagger
```

## Configuration

All secrets are supplied via environment variables. Double-underscore (`__`) is the separator for nested keys.

| Variable | Description |
|---|---|
| `Database__Provider` | `sqlite` \| `postgresql` \| `sqlserver` \| `mysql` |
| `ConnectionStrings__DefaultConnection` | Provider-appropriate connection string |
| `Jwt__Secret` | **Required in prod.** Minimum 32 characters. |
| `Jwt__Issuer` | JWT issuer claim |
| `Jwt__Audience` | JWT audience claim |
| `Database__AutoMigrate` | `true` to auto-run EF migrations on startup |

### Auto-migration

Migrations run automatically when **either** condition is true:

1. `Database:AutoMigrate` is `true` in configuration, **or**
2. The `--migrate` flag is passed as a CLI argument (e.g. the Docker entrypoint does this).

Both can be used together — they're additive.

## API Endpoints

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/v1/auth/register` | No | Register a new user |
| POST | `/api/v1/auth/login` | No | Login, receive token pair |
| POST | `/api/v1/auth/logout` | Bearer | Revoke a refresh token |
| POST | `/api/v1/auth/refresh` | No | Exchange refresh token for new pair |
| GET | `/api/v1/auth/me` | Bearer | Get current user profile |

## Password Requirements

- 8–128 characters
- At least one uppercase letter
- At least one digit
- At least one special character

## Running Tests

```bash
dotnet test tests/KanAuth.Tests
```

59 tests across four suites: `AuthService`, `TokenService`, `RegisterRequestValidator`, and `ExceptionHandlingMiddleware`. Uses xUnit + NSubstitute + FluentAssertions. No database required — all infrastructure dependencies are mocked.

## EF Core Migrations

```bash
# From repo root — run once per schema change
dotnet ef migrations add <MigrationName> \
  --project src/KanAuth.Infrastructure \
  --startup-project src/KanAuth.API
```

## AWS Elastic Beanstalk Deployment

```bash
eb init KanAuth --platform "Docker" --region us-east-1
eb create kanauth-prod --elb-type application --min-instances 2 --max-instances 4
eb setenv \
  Jwt__Secret="<generated-32+-char-secret>" \
  ConnectionStrings__DefaultConnection="<rds-connection-string>" \
  Database__Provider="postgresql" \
  Database__AutoMigrate="false"
eb deploy
```

> **Secrets**: Never commit `Jwt__Secret` or connection strings. Set them via `eb setenv` or AWS Secrets Manager. The Docker entrypoint passes `--migrate` so schema stays current on every deploy — set `Database__AutoMigrate=false` in EB and rely on the flag instead.

## Security Notes

- BCrypt work factor 12 (~300ms, prevents brute force)
- `ClockSkew = TimeSpan.Zero` on JWT validation
- Same error message for unknown email vs wrong password (no user enumeration)
- Token-family revocation on reuse detection (all sessions wiped)
- `Jwt__Secret` minimum 32 bytes; store in AWS Secrets Manager in production
- Security headers: `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`
- HSTS enabled in production
