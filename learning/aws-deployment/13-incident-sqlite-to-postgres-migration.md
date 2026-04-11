# Incident: 500 errors on first production deployment (SQLite migration ran against PostgreSQL)

**Date:** 2026-04-11
**Environment:** `kanauth-prod` (Elastic Beanstalk, ap-south-2)
**Symptom:** `POST /api/v1/auth/register` returned `500 Internal Server Error` on every call after the first successful deployment.

---

## 1. How it was debugged

### Step 1 — Reproduce the failure

```powershell
Invoke-RestMethod -Method POST `
  -Uri "http://kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com/api/v1/auth/register" `
  -ContentType "application/json" `
  -Body '{"email":"test@example.com","password":"Test@1234","firstName":"Test","lastName":"User"}'
# → The remote server returned an error: (500) Internal Server Error.
```

Health check (`/health`) returned `200`, so the container was alive — only the DB path was failing. That narrowed it to something the register flow touches: EF Core + PostgreSQL.

### Step 2 — Pull the tail logs from EB

```bash
aws elasticbeanstalk request-environment-info --environment-name kanauth-prod --info-type tail --region ap-south-2
aws elasticbeanstalk retrieve-environment-info --environment-name kanauth-prod --info-type tail --region ap-south-2
```

The returned S3 URL contained the bundled tail log. Key line:

```
PostgresException (0x80004005): 42804:
column "IsActive" is of type integer but expression is of type boolean
```

Stack trace traced back through:
- `AuthService.RegisterAsync()` → `IssueTokenPair()` → `DbContext.SaveChangesAsync()`
- Caught by `ExceptionHandlingMiddleware` → 500

### Step 3 — Inspect the migration

Opened `src/KanAuth.Infrastructure/Migrations/20260410082854_InitialCreate.cs`. Every column had **SQLite**-specific type hints:

```csharp
IsActive = table.Column<bool>(type: "INTEGER", nullable: false),  // ← SQLite
Id       = table.Column<Guid>(type: "TEXT",    nullable: false),  // ← SQLite
CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false), // ← SQLite
```

The migration had clearly been scaffolded while `Database:Provider=sqlite` was active locally.

---

## 2. Root cause

The `InitialCreate` migration was generated against the **SQLite provider** but executed against **PostgreSQL** in production (`AutoMigrate=true`, provider overridden via `Database__Provider=postgresql`).

- On SQLite those explicit types are fine (SQLite stores booleans in `INTEGER` columns and GUIDs/DateTimes in `TEXT`).
- On PostgreSQL, EF Core passed those literal SQL types straight through. So the migration created `Users.IsActive` as `integer`, but the C# model maps `bool` → `boolean`. Every insert/update attempted a `bool → integer` write and PostgreSQL (strictly typed) rejected it with SQLSTATE `42804`.

It was a latent bug from day one — the container had been up, but no request that touched `Users` or `RefreshTokens` had ever succeeded.

---

## 3. The fix

### 3.1 Add a new migration that corrects the schema

Locally, with the PostgreSQL provider selected so EF Core uses PostgreSQL's type mappings:

```bash
export Database__Provider=postgresql
export "ConnectionStrings__DefaultConnection=Host=...;Port=5432;Database=kanauth;Username=...;Password=..."

dotnet ef migrations add FixPostgresColumnTypes \
  --project src/KanAuth.Infrastructure \
  --startup-project src/KanAuth.API
```

EF Core scaffolded `AlterColumn<>` calls — but PostgreSQL refuses to auto-cast `TEXT → timestamp with time zone` without an explicit `USING` clause, and `AlterColumn<>` doesn't emit one. The first redeploy failed with:

```
42804: column "UpdatedAtUtc" cannot be cast automatically to type timestamp with time zone
```

Since the tables were empty (no registration had ever succeeded), the pragmatic fix was to **drop and recreate** the tables inside the new migration rather than fight PostgreSQL's type coercion. Final `FixPostgresColumnTypes.Up()`:

```csharp
migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""RefreshTokens"" CASCADE;");
migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""Users"" CASCADE;");

// Recreate with correct PostgreSQL types: uuid, text, boolean,
// timestamp with time zone, character varying(...).
migrationBuilder.CreateTable(name: "Users", ...);
migrationBuilder.CreateTable(name: "RefreshTokens", ...);
migrationBuilder.CreateIndex(...);
```

The original `20260410082854_InitialCreate` file was left untouched so the migration history row already present in `__EFMigrationsHistory` still matches.

### 3.2 Rebuild the image and push to ECR

```bash
docker build -t kanauth-api:latest .
docker tag  kanauth-api:latest 116743944666.dkr.ecr.ap-south-2.amazonaws.com/kanauth-api:latest
docker push 116743944666.dkr.ecr.ap-south-2.amazonaws.com/kanauth-api:latest
```

The migration is compiled into `KanAuth.Infrastructure.dll`, so the image must be rebuilt.

### 3.3 Redeploy via `eb deploy`

```bash
eb deploy kanauth-prod
```

> **Important:** do **not** deploy via `aws elasticbeanstalk create-application-version` with only `Dockerrun.aws.json` uploaded to S3. That path omits `.ebextensions/`, so `02-ssm-secrets.config`'s `container_commands` never runs and the container falls back to `Data Source=kanauth.db` from `appsettings.json` — the app then crashes with `SQLite Error 14: unable to open database file` because the non-root `appuser` can't write to `/app`. `eb deploy` packages the whole source tree including `.ebextensions/`.

With `Dockerrun.aws.json` setting `"Update": "true"`, `eb deploy` causes EB to re-pull the `:latest` tag from ECR.

Deployment output:

```
2026-04-11 10:59:16    INFO    Environment update is starting.
2026-04-11 10:59:20    INFO    Deploying new version to instance(s).
2026-04-11 10:59:36    INFO    Instance deployment completed successfully.
2026-04-11 10:59:42    INFO    Environment update completed successfully.
```

---

## 4. Verification

Full auth flow exercised against production:

| Step     | Request                                   | Result        |
|----------|-------------------------------------------|---------------|
| Health   | `GET /health`                             | `200 OK`      |
| Register | `POST /api/v1/auth/register`              | `201 Created` — returned access + refresh tokens |
| Login    | `POST /api/v1/auth/login`                 | `200 OK` — `lastLoginAtUtc` correctly populated |
| Me       | `GET /api/v1/auth/me` (with bearer)       | `200 OK` — JWT validation passed |
| Logout   | `POST /api/v1/auth/logout` (with refresh) | `204 No Content` |

User id persisted: `907a17a9-447e-4054-8d52-be3b70449d40` (`verify-test@example.com`).

Example commands (bash/curl):

```bash
curl -X POST http://kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"verify-test@example.com","password":"Test@1234","firstName":"Verify","lastName":"Test"}'
# → 201 Created

curl -X POST http://kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"verify-test@example.com","password":"Test@1234"}'
# → 200 OK

curl http://kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com/api/v1/auth/me \
  -H "Authorization: Bearer <accessToken>"
# → 200 OK

curl -X POST http://kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com/api/v1/auth/logout \
  -H "Authorization: Bearer <accessToken>" \
  -H "Content-Type: application/json" \
  -d '{"refreshToken":"<refreshToken>"}'
# → 204 No Content
```

---

## 5. Lessons and follow-ups

1. **Generate EF migrations against the target provider.** When an app supports multiple DB providers, migrations generated on SQLite are not portable to PostgreSQL because EF Core bakes provider-specific types into the scaffolded migration. Either (a) always scaffold with PostgreSQL selected, or (b) maintain separate migration assemblies per provider.
2. **PowerShell `curl` ≠ curl.** On Windows, `curl` aliases to `Invoke-WebRequest` which doesn't accept Unix flags. Use `curl.exe` or `Invoke-RestMethod`.
3. **`eb deploy` is the supported deployment path.** Ad-hoc deploys via `aws elasticbeanstalk create-application-version` pointing at a bare `Dockerrun.aws.json` silently drop `.ebextensions/` and break SSM secret injection.
4. **Tail logs are the fastest RCA tool.** `request-environment-info` + `retrieve-environment-info` produces a pre-signed S3 URL with `eb-engine.log`, the container's stdout, and `unexpected-quit.log` bundled — that's where both this bug and the follow-up `USING`-cast bug were diagnosed.
5. **Potential improvement:** switch secret loading from the `container_commands → env.list` pattern in `02-ssm-secrets.config` to a first-class SSM configuration provider (`Amazon.Extensions.Configuration.SystemsManager`) inside `Program.cs`. That removes the hidden dependency on EB's env-file mechanism and the `kms:Decrypt` permission edge case.
