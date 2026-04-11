# KanAuth – AWS Deployment Reference

## Active Services

| Service | Resource | Detail |
|---------|----------|--------|
| **Elastic Beanstalk** | App: `kanauth` | Region: `ap-south-2` |
| | Environment: `kanauth-prod` | ID: `e-yxfecmr94u` |
| | Platform | 64-bit Amazon Linux 2 v4.8.0 running Docker |
| | URL | `kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com` |
| **EC2** | Instance type | `t3.small` |
| | Security group | `sg-0fbd0935c6d8bc906` (`awseb-e-yxfecmr94u-stack-AWSEBSecurityGroup-XE7Du0SZqKQy`) |
| | VPC | `vpc-0b9635f52b41d1e29` |
| | Subnet | `subnet-065e2aa91caa5de8d` |
| **Application Load Balancer** | DNS | `awseb--AWSEB-jf4L3n4aPEYI-1033739991.ap-south-2.elb.amazonaws.com` |
| | Health check path | `GET /health` → HTTP 200 |
| **ECR** | Repository | `116743944666.dkr.ecr.ap-south-2.amazonaws.com/kanauth-api` |
| | Tag | `latest` |
| | Encryption | AES-256 |
| **RDS (PostgreSQL)** | Identifier | `kanauth-db` |
| | Engine | PostgreSQL 18.3 |
| | Instance class | `db.t3.micro` |
| | Endpoint | `kanauth-db.cfeos8cmkeq9.ap-south-2.rds.amazonaws.com:5432` |
| | Storage | 20 GB gp3 |
| | Multi-AZ | No |
| | Security group | `sg-0a0ddaa51da38a02e` |
| **SSM Parameter Store** | `/kanauth/prod/Jwt__Secret` | SecureString |
| | `/kanauth/prod/ConnectionStrings__DefaultConnection` | SecureString |
| **IAM** | EC2 instance profile | `kanauth-eb-instance-profile` (role: `kanauth-eb-instance-profile`) |
| | EB service role | `aws-elasticbeanstalk-service-role` |
| **S3** | EB artifact bucket | `elasticbeanstalk-ap-south-2-116743944666` |
| **CloudFormation** | Stack | `awseb-e-yxfecmr94u-stack` |

---

## Architecture Overview

```
Internet
  │
  ▼
Application Load Balancer  (port 80)
  │
  ▼
EC2 t3.small  (Elastic Beanstalk managed)
  │  nginx reverse proxy → Docker container (port 80)
  │  Container: ECR image (linux/amd64, .NET 8 ASP.NET Core)
  │
  ▼
RDS PostgreSQL 18.3  (private, port 5432)

Secrets: SSM Parameter Store (SecureString) → injected via .ebextensions at deploy time
```

---

## Deployment Steps

### Prerequisites

1. **AWS CLI** installed and configured (`aws configure`) with credentials for account `116743944666`, region `ap-south-2`.

2. **EB CLI** installed via pip:
   ```powershell
   pip install awsebcli botocore[crt]
   ```
   Add the Scripts folder to your PATH permanently:
   ```powershell
   $scriptsPath = "$env:USERPROFILE\AppData\Local\Python\pythoncore-3.14-64\Scripts"
   [System.Environment]::SetEnvironmentVariable("PATH", $env:PATH + ";$scriptsPath", "User")
   ```
   Then restart your terminal and verify: `eb --version`

3. **Docker Desktop** running (for building and pushing the image).

4. The following AWS resources must already exist before first deploy:
   - RDS instance `kanauth-db` (PostgreSQL) — running and accessible within the VPC
   - IAM instance profile `kanauth-eb-instance-profile` with permissions for ECR, SSM, CloudWatch Logs, and EB managed policies
   - SSM parameters `/kanauth/prod/Jwt__Secret` and `/kanauth/prod/ConnectionStrings__DefaultConnection` stored as SecureString

---

### Step 1 — Build the Docker image for linux/amd64

EB runs on `x86_64` EC2. Always build explicitly for `linux/amd64` regardless of your local machine architecture (important on Apple Silicon / ARM).

```powershell
docker build --platform linux/amd64 -t kanauth-api .
```

---

### Step 2 — Authenticate Docker with ECR

```powershell
aws ecr get-login-password --region ap-south-2 | `
  docker login --username AWS --password-stdin `
  116743944666.dkr.ecr.ap-south-2.amazonaws.com
```

---

### Step 3 — Tag and push the image to ECR

```powershell
docker tag kanauth-api:latest `
  116743944666.dkr.ecr.ap-south-2.amazonaws.com/kanauth-api:latest

docker push 116743944666.dkr.ecr.ap-south-2.amazonaws.com/kanauth-api:latest
```

---

### Step 4 — Initialise the EB CLI (first time only)

Run once per machine/clone. Creates `.elasticbeanstalk/config.yml`.

```powershell
eb init kanauth --platform "Docker" --region ap-south-2
```

---

### Step 5 — Create the EB environment (first time only)

Run once to provision EC2, ALB, security groups, etc.

```powershell
eb create kanauth-prod `
  --instance-types t3.small `
  --elb-type application `
  --instance_profile kanauth-eb-instance-profile `
  --envvars ASPNETCORE_ENVIRONMENT=Production
```

> Note: the flag is `--instance_profile` (underscore), not `--instance-profile`.

After creation, the RDS security group needs a one-time inbound rule to allow port 5432 from the EB EC2 security group:

```powershell
aws ec2 authorize-security-group-ingress `
  --region ap-south-2 `
  --group-id sg-0a0ddaa51da38a02e `
  --protocol tcp --port 5432 `
  --source-group sg-0fbd0935c6d8bc906
```

---

### Step 6 — Deploy (every subsequent release)

After pushing a new image to ECR (Steps 1–3), deploy it:

```powershell
eb deploy
```

EB will:
1. Bundle `Dockerrun.aws.json` + `.ebextensions/` into a zip and upload to S3
2. Run `container_commands` from `.ebextensions/02-ssm-secrets.config`, which reads the two SSM SecureStrings and appends them to `/opt/elasticbeanstalk/deployment/env.list`
3. Start the container: `docker run --env-file env.list <ecr-image>`
4. The container runs `dotnet KanAuth.API.dll --migrate`, which applies any pending EF Core migrations against RDS, then starts the web server on port 80
5. nginx proxies traffic from the ALB through to the container

---

### Step 7 — Verify

```powershell
eb status                # Health should be Green
eb open                  # Opens the environment URL in browser
Invoke-WebRequest -UseBasicParsing "http://kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com/health"
# Expected: StatusCode 200, Body "Healthy"
```

---

## Configuration Files

| File | Purpose |
|------|---------|
| `Dockerrun.aws.json` | Tells EB to pull from ECR (instead of building locally). Defines container port 80. |
| `.ebignore` | Excludes `Dockerfile`, `docker-compose.yml`, `src/`, `tests/` from the EB bundle so EB uses the ECR image. |
| `.ebextensions/01_environment.config` | Sets `ASPNETCORE_ENVIRONMENT=Production` and other non-secret env vars via `option_settings`. |
| `.ebextensions/02-ssm-secrets.config` | `container_commands` hook — reads `Jwt__Secret` and `ConnectionStrings__DefaultConnection` from SSM and appends them to `env.list` so Docker injects them into the container. |
| `.ebextensions/03-healthcheck.config` | Sets the ALB health check path to `/health` and `MatcherHTTPCode: "200"` (must be a quoted string — YAML integer causes silent failure). |

---

## Troubleshooting

| Symptom | Where to look | Likely cause |
|---------|--------------|-------------|
| Container exits immediately (GPF in libc) | `eb logs --all` → `messages` | Platform mismatch — image was not built for `linux/amd64`. Rebuild with `--platform linux/amd64`. |
| `Couldn't set data source` on startup | `eb-docker/containers/eb-current-app/unexpected-quit.log` | `ConnectionStrings__DefaultConnection` not injected — SQLite fallback hits Npgsql. Check SSM injection in `02-ssm-secrets.config`. |
| `Failed to connect to <RDS-IP>:5432` | `eb-docker/containers/eb-current-app/eb-*-stdouterr.log` | RDS security group missing inbound rule for port 5432 from EB security group. Run Step 5 post-creation command. |
| Health `Yellow`, `/health` returns 200 but ALB returns 502 | `nginx/error.log` → `connect() failed (111)` | App hasn't bound to port 80 yet (still running migrations) — wait, or check for a crash in stdouterr.log. |
| Health `Yellow` permanently, `MatcherHTTPCode` ignored | `.ebextensions/03-healthcheck.config` | `MatcherHTTPCode` must be a quoted string `"200"`. An unquoted integer causes EB to silently skip the entire namespace block including `HealthCheckPath`. |
| `.ebextensions` changes not taking effect | `git status .ebextensions/` | EB CLI uses `git archive` for bundling. Untracked files are excluded. Always `git add .ebextensions/` and commit before deploying. |

---

## Quick Reference — Useful Commands

```powershell
# Check environment health and URL
eb status

# Stream live events
eb events -f

# Download all logs from the instance
eb logs --all

# SSH into the EC2 instance (if key pair configured)
eb ssh

# Update a single env var without redeployment
eb setenv KEY=value

# Check what the container is outputting right now
eb logs --all 2>&1 | Out-File "$env:TEMP\eblogs.txt"
# Then look at: .elasticbeanstalk\logs\latest\<instance>\var\log\eb-docker\containers\eb-current-app\

# Verify RDS is reachable from your machine (for debugging)
aws rds describe-db-instances --region ap-south-2 --db-instance-identifier kanauth-db

# Inspect the ECR image manifest (verify architecture)
aws ecr batch-get-image --repository-name kanauth-api --region ap-south-2 `
  --image-ids imageTag=latest --query "images[0].imageManifest" --output text
```
