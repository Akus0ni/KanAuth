# 09 — `.ebextensions/` Configuration Files

## The Story: The Instruction Sheet For A LEGO Kit

Imagine buying a LEGO kit. The box has all the pieces but also a **little
booklet** telling the assembler "put this piece on that one, then screw
these together, then paint it blue." Without the booklet, the assembler
would have a pile of parts and no idea how to combine them.

**`.ebextensions/` is that booklet for Elastic Beanstalk.** It is a folder
of YAML files in your project root. When EB deploys your app, it reads the
booklet in alphabetical order and follows each instruction.

- **`option_settings`** — set EB environment options (like setting dials on a
  machine).
- **`container_commands`** — run a shell command during deploy, **after the
  app is unpacked but before the container starts**.
- **`commands`** — run a shell command during deploy, **before the app is unpacked**.
- **`files`** — write a file to the instance with specific content and perms.
- **`packages`** — install yum/rpm/gem packages.
- **`services`** — start/stop/restart system services.
- **`users` / `groups`** — create Linux users or groups.
- **`sources`** — download and unpack an archive onto the instance.

You rarely use all of these. KanAuth uses just **`option_settings`** and
**`container_commands`**.

## The KanAuth `.ebextensions/` Walk-Through

There are exactly three files. We'll walk every line.

### File 1 — `01_environment.config`

```yaml
option_settings:
  aws:elasticbeanstalk:application:environment:
    ASPNETCORE_ENVIRONMENT: Production
    Database__Provider: postgresql
    Database__AutoMigrate: "true"
    Jwt__Issuer: KanAuth
    Jwt__Audience: KanAuth.Clients
```

#### What Each Line Does

- **`option_settings:`** — the top-level EB config key.
- **`aws:elasticbeanstalk:application:environment:`** — the namespace for
  "application environment variables". Everything under this appears as
  env vars inside the container.
- **`ASPNETCORE_ENVIRONMENT: Production`** — tells ASP.NET Core to use the
  Production configuration. In `Program.cs`, this disables Swagger,
  enables real HTTPS redirection, and reads `appsettings.Production.json`.
- **`Database__Provider: postgresql`** — tells the Infrastructure DI code
  to wire up the Npgsql driver (not SQLite).
- **`Database__AutoMigrate: "true"`** — a quoted string (YAML safety) that
  tells `Program.cs` to run `db.Database.Migrate()` on startup.
- **`Jwt__Issuer: KanAuth`** and **`Jwt__Audience: KanAuth.Clients`** —
  the issuer and audience claims stamped into every JWT access token.
  These are not secrets, so they live here in plain text.

#### Why `__` Instead Of `:`

.NET config uses `:` to denote hierarchy (`Jwt:Issuer`). Unix env vars
cannot contain colons. The convention is to use `__` (double underscore),
and .NET substitutes it for `:` internally.

### File 2 — `02-ssm-secrets.config`

```yaml
container_commands:
  01_fetch_ssm_secrets:
    command: |
      JWT_SECRET=$(aws ssm get-parameter \
        --name "/kanauth/prod/Jwt__Secret" \
        --with-decryption \
        --query Parameter.Value \
        --output text \
        --region $(curl -s http://169.254.169.254/latest/meta-data/placement/region))
      DB_CONN=$(aws ssm get-parameter \
        --name "/kanauth/prod/ConnectionStrings__DefaultConnection" \
        --with-decryption \
        --query Parameter.Value \
        --output text \
        --region $(curl -s http://169.254.169.254/latest/meta-data/placement/region))
      cat >> /opt/elasticbeanstalk/deployment/env.list << EOF
      Jwt__Secret=$JWT_SECRET
      ConnectionStrings__DefaultConnection=$DB_CONN
      Database__Provider=postgresql
      Database__AutoMigrate=true
      Jwt__Issuer=KanAuth
      Jwt__Audience=KanAuth.Clients
      EOF
```

#### The Big Picture

This file runs a shell script during the deploy. The script:

1. Fetches two SecureString parameters from SSM.
2. Appends them (and a few plaintext env vars) to `env.list`.
3. EB then runs `docker run --env-file /opt/elasticbeanstalk/deployment/env.list <image>`.

#### Line By Line

- **`container_commands:`** — the YAML namespace for "run commands after
  source is unpacked but before the container starts". This is the perfect
  hook for injecting secrets.

- **`01_fetch_ssm_secrets:`** — the **name** of the command. Commands run
  in **alphabetical order of their names**, so prefix with `01_`, `02_`,
  etc. to control order.

- **`command: |`** — `|` is YAML's "literal block scalar" — it preserves
  newlines and hands the whole block to the shell as one script.

- **`JWT_SECRET=$(...)`** — shell command substitution: run the inner
  command, capture stdout into `JWT_SECRET`.

- **`aws ssm get-parameter --name "..." --with-decryption`** — fetches the
  parameter. `--with-decryption` is required because SecureString values
  are encrypted at rest with KMS.

- **`--query Parameter.Value --output text`** — extracts just the value as
  raw text (no JSON quotes).

- **`--region $(curl -s http://169.254.169.254/latest/meta-data/placement/region)`**
  — reads the current EC2's region from the **instance metadata service**.
  `169.254.169.254` is the magic "link-local" address that every EC2 can
  hit to learn about itself. This avoids hard-coding `ap-south-2`, so the
  same config works in any region.

- **`cat >> /opt/elasticbeanstalk/deployment/env.list << EOF ... EOF`** —
  a heredoc that appends lines to `env.list`. The `>>` is append (not
  overwrite), so this plays nicely with other files that might add env
  vars too.

- The heredoc repeats the same non-secret values as `01_environment.config`.
  This is **redundant** but safe — `env.list` takes precedence for Docker,
  so duplication is harmless. If you cleaned it up, you'd rely only on
  `option_settings`, which also works.

#### Why Not Use `option_settings` For Secrets?

`option_settings` values end up in the CloudFormation template, the
environment variables exposed in the EB console, and possibly in logs. They
are **plaintext**. SSM SecureStrings fetched in `container_commands` never
leave the instance as plaintext — they are decrypted in memory and written
to a file that `root` owns.

### File 3 — `03-healthcheck.config`

```yaml
option_settings:
  aws:elasticbeanstalk:environment:process:default:
    HealthCheckPath: /health
    MatcherHTTPCode: "200"
  aws:elasticbeanstalk:healthreporting:system:
    SystemType: enhanced
```

#### What Each Line Does

- **`aws:elasticbeanstalk:environment:process:default`** — the **default
  process** in EB. For single-container EB, this is the only process. The
  namespace contains target group / ALB settings.

- **`HealthCheckPath: /health`** — the ALB target group will hit
  `http://<ec2-private-ip>/health` to check liveness.

- **`MatcherHTTPCode: "200"`** — success is HTTP 200, nothing else.

- **`aws:elasticbeanstalk:healthreporting:system`** — the EB health
  reporting namespace.

- **`SystemType: enhanced`** — turn on EB's fancy dashboard that reports
  CPU, memory, nginx response codes, and per-instance health in detail.
  (The other option, `basic`, only uses ALB checks.)

#### The Gotcha

**`MatcherHTTPCode` must be a quoted string.** If you write:

```yaml
MatcherHTTPCode: 200   # NO QUOTES
```

YAML parses `200` as an integer. EB silently rejects the entire
`process:default` namespace (including `HealthCheckPath`!), because the
schema requires a string. The result: no health check is configured, ALB
uses the default `/` path, the `/health` endpoint is never called, and
your environment goes permanently Yellow.

This happened in real deployment and is documented in `DEPLOYMENT.md`'s
troubleshooting table. Always quote `"200"`.

## Deployment Order Of Operations

When you run `eb deploy`, EB:

1. **Bundles** the current directory into a zip, respecting `.ebignore`.
2. **Uploads** the zip to the EB S3 artifact bucket.
3. **Instructs** the EC2 instances in the environment to download the zip.
4. On each instance:
    - a. **`commands:`** section runs (before source unpack). KanAuth has none.
    - b. **Source is unpacked** — `Dockerrun.aws.json` + `.ebextensions/`
      are placed on disk.
    - c. **`files:`, `packages:`, `services:`, `users:`, `sources:`** are
      applied. KanAuth has none of these.
    - d. **`container_commands:`** section runs (after unpack, before
      container start). This is where our `02-ssm-secrets.config` runs.
    - e. **`option_settings`** — EB applies the options from `01_environment`
      and `03-healthcheck` (most take effect from here on out).
    - f. **EB starts the container:** `docker pull` the image from ECR,
      `docker run --env-file env.list -p 80:80 <image>`.
    - g. **EB waits for health checks to pass** on the ALB target group.
5. **Once healthy**, EB marks the deploy successful.

Understanding this order is essential for debugging. If something runs
"too early", move it from `commands` to `container_commands` (or the other
way).

## The `.ebignore` File

Sibling to `.ebextensions/` is `.ebignore` in the project root:

```
Dockerfile
docker-compose.yml
src/
tests/
```

This tells the EB CLI **what to exclude** from the deployment zip.
Without it, `eb deploy` would bundle the whole source tree (hundreds of MB)
just to deploy a `Dockerrun.aws.json` file (200 bytes).

**Critical:** The EB CLI uses `git archive` to bundle if you're in a git
repo. Any file **not tracked by git** is excluded. If you forget to
`git add .ebextensions/03-healthcheck.config`, the file is simply missing
from the deploy, even though it is on your disk. This is another common
mistake — always commit `.ebextensions/` changes before deploying.

## Exact Steps: Modifying `.ebextensions`

### Step 1 — Make the change

Edit a file under `.ebextensions/`.

### Step 2 — Stage and commit

```bash
git add .ebextensions/02-ssm-secrets.config
git commit -m "ebext: add new parameter"
```

Otherwise the change will not be in the deployment zip.

### Step 3 — Deploy

```bash
eb deploy
```

### Step 4 — Watch for failures

```bash
eb events -f
```

Look for lines like `ERROR: Instance deployment failed`. If you see one,
check the cause:

```bash
eb logs --all
```

Then look under the downloaded log folder for
`eb-engine.log` — that's the file that records every
`container_command` execution and its output.

## Alternatives: Other EB Configuration Mechanisms

### Platform Hooks (`.platform/hooks/`)

Introduced in Amazon Linux 2 platforms. Shell scripts dropped into
`.platform/hooks/prebuild/`, `.platform/hooks/predeploy/`,
`.platform/hooks/postdeploy/`, etc. Run in that lifecycle phase.

**Pros:**
- Cleaner than YAML heredocs; you write plain shell.
- Better for complex multi-step logic.
- Runs on both Amazon Linux 2 and Amazon Linux 2023.

**Cons:**
- Not portable back to Amazon Linux 1 (dead anyway).
- One more directory convention to remember.

**When to switch:** If your `container_commands` starts having multiple
scripts or real logic, graduate to `.platform/hooks/`.

### `.platform/nginx/conf.d/` (Nginx Overrides)

Drop `.conf` files here to customize the nginx reverse proxy in front of
your container. Useful for:
- Adding request size limits (`client_max_body_size`)
- Adding custom headers
- Tuning buffering

KanAuth does not override nginx — the defaults are fine for a JSON API.

### `.ebextensions` vs User-Data

EB runs the same instance bootstrap process as a raw EC2 **user-data**
script, just higher-level. You could theoretically launch an EC2 by hand
with a user-data bash script that does the same thing `.ebextensions` does.
EB exists so you don't.

### Other Tools Entirely

| Alternative | Philosophy |
|---|---|
| **Terraform** | Define ALL infra as code, including `.ebextensions` equivalents through `local_file` resources |
| **AWS CDK** | Same as Terraform but in TypeScript / Python / C# |
| **CloudFormation** (raw) | Skip EB; declare all resources yourself |
| **Pulumi** | Like CDK but more polyglot |
| **Ansible** | Classic config management; not container-native |

For KanAuth's size, plain `.ebextensions` is the right level of complexity.

## Key Takeaways

- **`.ebextensions/` is the LEGO instruction booklet for EB.** YAML files,
  alphabetical order, placed in the project root.
- **`option_settings`** sets EB options (env vars, health check, etc.).
- **`container_commands`** runs shell scripts after source unpack, before
  container start — the perfect hook for injecting SSM secrets.
- **Quote `MatcherHTTPCode` as `"200"`** or EB silently drops the block.
- **Commit `.ebextensions/` to git** before deploying; `git archive` is
  how the bundle is produced.
- **Platform hooks (`.platform/hooks/`)** are the newer, cleaner
  alternative for complex logic.

## Next

Read [10-cloudformation-s3.md](10-cloudformation-s3.md) to see what EB
silently creates in the background.
