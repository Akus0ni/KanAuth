# 06 — SSM Parameter Store (The Secret Safe)

## The Story: The Hotel Safe

When you stay at a hotel, you have valuables — a passport, some cash — and
you don't want to leave them on the desk. The room has a **small safe**.
You put valuables inside, set a PIN, and lock it. When you need them back,
you unlock the safe and take them out.

A real application has "valuables" too: JWT signing secrets, database
passwords, API keys. If you put these in the source code, **they end up in
git forever**. If you put them in a plain text file on disk, anyone who can
read the file can read your secrets.

**AWS SSM Parameter Store is your hotel safe for secrets.** You put the
secret in once, encrypted with a key only your app can access, and then the
app fetches it at boot.

- **SSM** = *AWS Systems Manager* — a big family of ops tools. The
  Parameter Store is one piece of it.
- **Parameter** = a named key/value pair, like `/kanauth/prod/Jwt__Secret`.
- **SecureString** = a parameter whose value is encrypted at rest with KMS.

## Why Not Just Use Environment Variables In EB?

You *can* set env vars directly in EB via `option_settings` or
`eb setenv KEY=value`. That works — but:

1. **They show up in the EB console in plaintext** (anyone with EB read
   access sees them).
2. **They land in CloudFormation templates** in plaintext.
3. **They can be accidentally exported** in `eb printenv` output.
4. **You cannot rotate a secret without a code/deploy cycle** unless you
   remember exactly which env var holds it.

SSM parameters are:

1. **Encrypted at rest** by default (SecureString type).
2. **Access-controlled by IAM** — only the EC2 instance profile can decrypt them.
3. **Audited** — every `GetParameter` call is logged in CloudTrail.
4. **Rotatable** — update the parameter, restart the container, done.

## KanAuth's Secrets

From `DEPLOYMENT.md`:

| Parameter Name | Type | Purpose |
|---|---|---|
| `/kanauth/prod/Jwt__Secret` | SecureString | Signs JWT access tokens. Minimum 32 characters. |
| `/kanauth/prod/ConnectionStrings__DefaultConnection` | SecureString | Full Postgres connection string including DB password |

### Why The `__` (Double Underscore)?

.NET's configuration system maps `__` in env var names to `:` in config
paths. So:

| Env Var | C# Configuration Key |
|---|---|
| `Jwt__Secret` | `Jwt:Secret` |
| `ConnectionStrings__DefaultConnection` | `ConnectionStrings:DefaultConnection` |

This lets you put nested values into flat env vars without any code change.
The .NET runtime handles the translation.

### Why The `/kanauth/prod/` Prefix?

SSM uses **hierarchical paths** like a file system. The prefix lets you:
- Group parameters by app and environment.
- Grant IAM access to `"/kanauth/prod/*"` without accidentally exposing
  another app's secrets.
- Run `aws ssm get-parameters-by-path --path /kanauth/prod/ --recursive`
  to fetch all related parameters in one call.

## How KanAuth Fetches Secrets At Boot

Look at `.ebextensions/02-ssm-secrets.config`:

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

### Line By Line

1. **`container_commands:`** — a special `.ebextensions` section that runs
   **after the app source is unpacked but before the container starts**.
   This is the exact hook point where we want to inject secrets.

2. **`aws ssm get-parameter --name "/kanauth/prod/Jwt__Secret"`** — asks SSM
   for the parameter. Returns JSON by default.

3. **`--with-decryption`** — tells SSM "this is a SecureString, please
   decrypt it with KMS before returning". Without this flag, a SecureString
   returns its ciphertext, which is useless.

4. **`--query Parameter.Value --output text`** — extracts just the value
   field from the JSON response and prints it raw, so shell can capture it.

5. **`--region $(curl -s http://169.254.169.254/latest/meta-data/placement/region)`**
   — fetches the current region from the **EC2 Instance Metadata Service**
   (the magic `169.254.169.254` IP). Avoids hard-coding the region.

6. **`cat >> /opt/elasticbeanstalk/deployment/env.list`** — appends to the
   file EB reads when running `docker run --env-file`. Every line here
   becomes one `KEY=VALUE` environment variable inside the container.

### Result

When the container starts, it sees these env vars:

```
Jwt__Secret=<the decrypted value>
ConnectionStrings__DefaultConnection=Host=...;Password=...
Database__Provider=postgresql
Database__AutoMigrate=true
Jwt__Issuer=KanAuth
Jwt__Audience=KanAuth.Clients
```

Which the .NET config system picks up as:

```csharp
configuration["Jwt:Secret"]
configuration["ConnectionStrings:DefaultConnection"]
```

The secrets never touch git, never appear in Docker image layers, and never
appear in the EB config UI.

## Exact Steps: Storing And Updating Secrets

### Step 1 — Store a new SecureString

```bash
aws ssm put-parameter \
  --name "/kanauth/prod/Jwt__Secret" \
  --value "ThisIsAVeryLongRandomStringAtLeast32Chars!" \
  --type SecureString \
  --region ap-south-2
```

**What it does:** Creates or updates a SecureString parameter. Encrypted
with the default `aws/ssm` KMS key.

**Why SecureString (not String)?** Anything sensitive should be encrypted.
`String` is plain text — fine for non-secret config like feature flags, but
not for passwords.

### Step 2 — Rotate a secret

```bash
aws ssm put-parameter \
  --name "/kanauth/prod/Jwt__Secret" \
  --value "ANewEvenLongerRandomStringForRotation!!!" \
  --type SecureString \
  --overwrite
```

Then trigger a container restart so the new value is picked up:

```bash
eb deploy
```

**Why you must redeploy:** The `container_commands` script only runs at
deploy time. SSM does not push updates to running containers. If you want
live rotation, you need a library like
[`Chef/ssm-parameter-store`](#) or AWS SDK code to poll SSM from inside the app.

### Step 3 — List all KanAuth parameters

```bash
aws ssm get-parameters-by-path \
  --path "/kanauth/prod/" \
  --recursive \
  --with-decryption \
  --region ap-south-2
```

**Caution:** Using `--with-decryption` on a list call reveals every secret
value — run it locally only, never in CI logs.

### Step 4 — Delete a parameter

```bash
aws ssm delete-parameter --name "/kanauth/prod/Old__Key"
```

## IAM Permissions Needed

The EC2 instance profile `kanauth-eb-instance-profile` has
`AmazonSSMReadOnlyAccess`, which allows `ssm:GetParameter` on anything. For
**production hardening**, you should replace this with a narrower policy:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["ssm:GetParameter", "ssm:GetParameters"],
      "Resource": "arn:aws:ssm:ap-south-2:116743944666:parameter/kanauth/prod/*"
    },
    {
      "Effect": "Allow",
      "Action": "kms:Decrypt",
      "Resource": "arn:aws:kms:ap-south-2:116743944666:key/alias/aws/ssm"
    }
  ]
}
```

This follows the **principle of least privilege**: the EC2 can read only
its own parameters, nothing else in the account.

## Alternatives: Other Ways To Store Secrets

| Alternative | Pros | Cons | When |
|---|---|---|---|
| **AWS Secrets Manager** | Automatic rotation for RDS/Redshift/DocumentDB, cross-region replication, native JSON | Costs $0.40/secret/month (SSM is free for Standard tier) | You need automated password rotation |
| **HashiCorp Vault** | Cloud-agnostic, dynamic secrets, fine-grained ACLs | You have to run and HA it yourself | Multi-cloud, advanced PKI, heavy compliance |
| **Kubernetes Secrets** | Native to K8s | Base64, not really encrypted at rest by default; need `etcd` encryption enabled | Already on K8s; pairing with external-secrets operator |
| **Azure Key Vault / GCP Secret Manager** | Same concept on other clouds | Wrong cloud | Azure/GCP-native apps |
| **Environment variables in EB** | Simplest | Plaintext in UI; no rotation | Dev/staging only |
| **`.env` file in the image** | No external service | Committed to git or baked into the image — terrible for secrets | **Never do this.** Not even for hobby projects. |
| **Doppler / Infisical / 1Password Secrets Automation** | Great DX, UI, team-oriented | Extra vendor, another account to manage | Startups wanting a unified dev+prod secrets UX |

### SSM vs Secrets Manager: The Real Comparison

| Feature | SSM Parameter Store | Secrets Manager |
|---|---|---|
| **Cost** | Free (Standard tier, <10 KB values) | $0.40/secret/month |
| **Encryption** | KMS (SecureString) | KMS (always) |
| **Max value size** | 4 KB (Standard) / 8 KB (Advanced) | 64 KB |
| **Automatic rotation** | No | Yes (Lambda-based) |
| **Cross-region replication** | No | Yes |
| **Hierarchical naming** | Yes (`/app/env/key`) | Flat names |
| **Versioning** | Yes (100 versions/param) | Yes |

**Rule of thumb:** Start with SSM SecureString. Migrate to Secrets Manager
only when you need automatic RDS password rotation or values larger than 4
KB. KanAuth is well inside SSM's sweet spot.

### What About `.env` Files?

Some tutorials say "just use a `.env` file." **Do not do this for secrets
in production.** A `.env` file:

- Ends up in git if you forget to gitignore it.
- Is baked into Docker images if you COPY it.
- Has no access control — anyone with shell access can read it.
- Has no audit trail.

`.env` files are fine for **local development**, where the secrets are dev
values that cannot harm anyone.

## Key Takeaways

- **SSM Parameter Store is the hotel safe for app secrets.** Free,
  encrypted, IAM-controlled.
- KanAuth stores the **JWT secret** and **DB connection string** as
  SecureStrings under `/kanauth/prod/`.
- Secrets are **fetched at container boot** by `.ebextensions/02-ssm-secrets.config`.
- The EC2 proves who it is using its **instance profile** — no static
  credentials anywhere.
- To rotate a secret: `put-parameter --overwrite` then `eb deploy`.
- **Alternatives:** Secrets Manager (pay for rotation), Vault
  (self-hosted), env vars (insecure).

## Next

Read [07-iam-and-security.md](07-iam-and-security.md) to understand the
"keycard system" that decides who can decrypt those secrets.
