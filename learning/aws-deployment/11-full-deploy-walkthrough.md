# 11 — Full Deploy Walkthrough (Zero → Deployed)

## What This File Is

A single, ordered runbook that takes you from a brand-new AWS account with
**nothing** to a working, publicly-reachable KanAuth API. Every step links
back to the chapter that explains the "why".

Read chapters 00–10 first, then use this as the cheat sheet when you
actually run the commands.

> **Notation:** replace `<account-id>` with your AWS account number and
> `<region>` with the AWS region (KanAuth uses `ap-south-2`). Everywhere
> you see `<strong-password>` or `<jwt-secret>`, pick something random and
> at least 32 characters long.

## Phase 0 — Prerequisites (One-Time Per Laptop)

### Install the tools

```bash
# AWS CLI v2
# (download installer from https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html)

# EB CLI (Python)
pip install awsebcli botocore[crt]

# Docker Desktop
# (install from https://www.docker.com/products/docker-desktop/)

# .NET 8 SDK
# (install from https://dotnet.microsoft.com/download)
```

### Configure AWS credentials

```bash
aws configure
# AWS Access Key ID:     <your IAM user's access key>
# AWS Secret Access Key: <your IAM user's secret>
# Default region:        ap-south-2
# Default output format: json
```

Or, better for security, configure an **SSO profile** or set up IAM
Identity Center.

### Verify

```bash
aws sts get-caller-identity
# Should print your account ID and the ARN of your current user/role.

eb --version
docker --version
dotnet --version
```

---

## Phase 1 — Create The Docker Image (Chapter 01)

### Step 1.1 — Build for the right CPU

```bash
docker build --platform linux/amd64 -t kanauth-api .
```

**Why `--platform linux/amd64`?** EB's default Amazon Linux platform runs
on Intel/AMD. If you're on Apple Silicon, without this flag you'd build an
ARM image that crashes on AWS.

### Step 1.2 — Run it locally to sanity-check

```bash
docker run --rm -p 8080:80 \
  -e Database__Provider=sqlite \
  -e Jwt__Secret=test-secret-at-least-32-chars-long \
  kanauth-api
```

Visit `http://localhost:8080/swagger`. If this works, your image is good.
Ctrl-C to stop.

---

## Phase 2 — Push The Image To ECR (Chapter 01)

### Step 2.1 — Create the ECR repository (one time)

```bash
aws ecr create-repository \
  --repository-name kanauth-api \
  --region <region>
```

### Step 2.2 — Authenticate Docker to ECR

```bash
aws ecr get-login-password --region <region> | \
  docker login --username AWS --password-stdin \
  <account-id>.dkr.ecr.<region>.amazonaws.com
```

### Step 2.3 — Tag & push

```bash
docker tag kanauth-api:latest \
  <account-id>.dkr.ecr.<region>.amazonaws.com/kanauth-api:latest

docker push \
  <account-id>.dkr.ecr.<region>.amazonaws.com/kanauth-api:latest
```

**What just happened?** The image is now sitting in ECR, ready for an EC2
to pull it.

---

## Phase 3 — Provision RDS (Chapter 05)

### Step 3.1 — Create a DB subnet group

```bash
aws rds create-db-subnet-group \
  --db-subnet-group-name kanauth-db-subnet \
  --db-subnet-group-description "KanAuth DB subnet group" \
  --subnet-ids <subnet-id-1> <subnet-id-2> \
  --region <region>
```

> Use **two subnets in two different AZs** (even if Single-AZ) so RDS can
> pick one.

### Step 3.2 — Create the RDS instance

```bash
aws rds create-db-instance \
  --db-instance-identifier kanauth-db \
  --db-instance-class db.t3.micro \
  --engine postgres \
  --engine-version "18" \
  --master-username kanauth \
  --master-user-password "<strong-password>" \
  --db-name kanauth \
  --db-subnet-group-name kanauth-db-subnet \
  --no-publicly-accessible \
  --allocated-storage 20 \
  --storage-type gp3 \
  --backup-retention-period 7 \
  --region <region>
```

Wait ~10 minutes. Check progress:

```bash
aws rds describe-db-instances \
  --db-instance-identifier kanauth-db \
  --region <region> \
  --query "DBInstances[0].[DBInstanceStatus,Endpoint.Address]"
```

Once status is `available`, note the endpoint address. You'll need it next.

---

## Phase 4 — Store Secrets In SSM (Chapter 06)

### Step 4.1 — Store the JWT secret

```bash
aws ssm put-parameter \
  --name "/kanauth/prod/Jwt__Secret" \
  --value "<jwt-secret-at-least-32-chars>" \
  --type SecureString \
  --region <region>
```

### Step 4.2 — Store the DB connection string

```bash
aws ssm put-parameter \
  --name "/kanauth/prod/ConnectionStrings__DefaultConnection" \
  --value "Host=<rds-endpoint>;Port=5432;Database=kanauth;Username=kanauth;Password=<strong-password>" \
  --type SecureString \
  --region <region>
```

---

## Phase 5 — Create The IAM Instance Profile (Chapter 07)

> Skip this phase if the instance profile already exists in your account.

### Step 5.1 — Create the role

```bash
aws iam create-role \
  --role-name kanauth-eb-instance-profile \
  --assume-role-policy-document '{
    "Version":"2012-10-17",
    "Statement":[{
      "Effect":"Allow",
      "Principal":{"Service":"ec2.amazonaws.com"},
      "Action":"sts:AssumeRole"
    }]
  }'
```

### Step 5.2 — Attach policies

```bash
aws iam attach-role-policy \
  --role-name kanauth-eb-instance-profile \
  --policy-arn arn:aws:iam::aws:policy/AWSElasticBeanstalkWebTier

aws iam attach-role-policy \
  --role-name kanauth-eb-instance-profile \
  --policy-arn arn:aws:iam::aws:policy/AmazonEC2ContainerRegistryReadOnly

aws iam attach-role-policy \
  --role-name kanauth-eb-instance-profile \
  --policy-arn arn:aws:iam::aws:policy/AmazonSSMReadOnlyAccess
```

### Step 5.3 — Create the instance profile and bind the role

```bash
aws iam create-instance-profile \
  --instance-profile-name kanauth-eb-instance-profile

aws iam add-role-to-instance-profile \
  --instance-profile-name kanauth-eb-instance-profile \
  --role-name kanauth-eb-instance-profile
```

---

## Phase 6 — Configure `.ebextensions/` And `Dockerrun.aws.json` (Chapter 09)

These files are already in the KanAuth repo. The only value you must
update is the ECR image URI in `Dockerrun.aws.json` if you changed the
account ID or region:

```json
{
  "AWSEBDockerrunVersion": "1",
  "Image": {
    "Name": "<account-id>.dkr.ecr.<region>.amazonaws.com/kanauth-api:latest",
    "Update": "true"
  },
  "Ports": [{ "ContainerPort": "80" }],
  "Logging": "/app/logs"
}
```

Verify these three `.ebextensions/` files are committed to git:

```bash
git status .ebextensions/
# Should show nothing (all clean) or show staged edits
git add .ebextensions/ Dockerrun.aws.json
git commit -m "infra: eb configs"
```

> **Must be committed.** `eb deploy` uses `git archive`, so untracked
> files are invisible.

---

## Phase 7 — Initialize And Create The EB Environment (Chapter 02)

### Step 7.1 — Initialize (once per machine/clone)

```bash
eb init kanauth --platform "Docker" --region <region>
```

When prompted:
- Application name: `kanauth`
- Platform: `Docker running on 64bit Amazon Linux 2023`
- SSH: optional (say yes if you want to `eb ssh` later)

### Step 7.2 — Create the environment

```bash
eb create kanauth-prod \
  --instance-types t3.small \
  --elb-type application \
  --instance_profile kanauth-eb-instance-profile \
  --envvars ASPNETCORE_ENVIRONMENT=Production
```

Takes 5–10 minutes. EB will create ~25 resources via CloudFormation.
Stream the events:

```bash
eb events -f
# Ctrl-C to exit
```

---

## Phase 8 — Wire Up The RDS Security Group (Chapter 08)

> This is the one manual rule that EB cannot create for you because it
> does not know about your RDS.

### Step 8.1 — Find the EB EC2 security group

```bash
aws ec2 describe-security-groups \
  --region <region> \
  --filters "Name=tag:elasticbeanstalk:environment-name,Values=kanauth-prod" \
  --query "SecurityGroups[?starts_with(GroupName, 'awseb')].GroupId"
```

Save the output as `<eb-sg-id>`.

### Step 8.2 — Find the RDS security group

```bash
aws rds describe-db-instances \
  --db-instance-identifier kanauth-db \
  --region <region> \
  --query "DBInstances[0].VpcSecurityGroups[0].VpcSecurityGroupId" \
  --output text
```

Save the output as `<rds-sg-id>`.

### Step 8.3 — Add the inbound rule

```bash
aws ec2 authorize-security-group-ingress \
  --region <region> \
  --group-id <rds-sg-id> \
  --protocol tcp --port 5432 \
  --source-group <eb-sg-id>
```

Now the app can reach the database.

---

## Phase 9 — Verify The Deploy

### Step 9.1 — Environment status

```bash
eb status
```

Expect:
```
Health:          Green
Status:          Ready
```

### Step 9.2 — Hit the health endpoint

```bash
curl http://<eb-cname>/health
# Expected: 200 OK, body "Healthy"
```

### Step 9.3 — Browse Swagger (only if ASPNETCORE_ENVIRONMENT isn't Production)

```bash
eb open
# Opens http://<eb-cname>/ in your browser
```

Note: `Program.cs` disables Swagger in Production. For a real sanity check,
call an actual endpoint:

```bash
curl -X POST http://<eb-cname>/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"test@example.com","password":"SuperSecret1!"}'
```

Expect a JSON response with `accessToken` and `refreshToken`.

### Step 9.4 — Check logs if anything is wrong

```bash
eb logs --all
```

Then open the downloaded folder and read:
- `eb-engine.log` — deploy lifecycle
- `eb-docker/containers/eb-current-app/stdouterr.log` — the app's console output
- `nginx/error.log` — nginx issues
- `messages` — kernel/syslog (platform mismatches show up here)

---

## Phase 10 — Subsequent Deploys (Every Release)

After the first deploy, pushing a new version takes **three commands**:

```bash
# 1. Build the new image
docker build --platform linux/amd64 -t kanauth-api .

# 2. Push it (docker login already expired; re-auth first if needed)
aws ecr get-login-password --region <region> | docker login --username AWS --password-stdin <account-id>.dkr.ecr.<region>.amazonaws.com
docker push <account-id>.dkr.ecr.<region>.amazonaws.com/kanauth-api:latest

# 3. Redeploy
eb deploy
```

If you only changed `.ebextensions/` or `Dockerrun.aws.json` (no code
change), skip steps 1 and 2. `eb deploy` alone is enough because EB will
re-pull the same `latest` image and re-run `container_commands`.

---

## The Visual Summary

```
Phase 1–2: Build & Push Image
    docker build → docker push → ECR

Phase 3: Database
    aws rds create-db-subnet-group
    aws rds create-db-instance

Phase 4: Secrets
    aws ssm put-parameter × 2

Phase 5: IAM (one-time per account)
    aws iam create-role
    aws iam attach-role-policy × 3
    aws iam create-instance-profile
    aws iam add-role-to-instance-profile

Phase 6: Config files in repo
    Dockerrun.aws.json + .ebextensions/ committed to git

Phase 7: Elastic Beanstalk
    eb init
    eb create

Phase 8: Security group rule
    aws ec2 authorize-security-group-ingress   (port 5432: EB SG → RDS SG)

Phase 9: Verify
    eb status | curl /health | eb logs

Phase 10: Every subsequent release
    docker build/push → eb deploy
```

## Common Failure Modes And Fixes

| Symptom | Most Likely Cause | Fix |
|---|---|---|
| EB environment goes Red immediately after create | Image platform mismatch (ARM built on Apple Silicon) | Rebuild with `--platform linux/amd64`, push, `eb deploy` |
| App starts but `/health` returns 503 | Cannot reach RDS | Phase 8 — add the inbound rule |
| Container exits with "Couldn't set data source" | `ConnectionStrings__DefaultConnection` missing | Verify `.ebextensions/02-ssm-secrets.config` is committed; SSM parameter exists |
| Environment goes Yellow permanently | `MatcherHTTPCode` not quoted | Change to `"200"` in `03-healthcheck.config`, commit, redeploy |
| Deploy fails with "AccessDenied fetching SSM" | Instance profile missing `AmazonSSMReadOnlyAccess` | Attach the policy (Phase 5) |
| Deploy hangs at `Creating security group` | IAM propagation lag | Wait 30 seconds and retry |

## Key Takeaways

- **Phase 0–2** are one-time image plumbing.
- **Phase 3–5** are one-time AWS resources (DB, secrets, IAM).
- **Phase 6–8** are the first EB environment bootstrap.
- **Phase 10** is what you do every time you ship a new version.
- The **one manual security group rule** (phase 8) is the rite of passage.
  Everyone forgets it the first time.

## Next

Read [12-alternatives-cheatsheet.md](12-alternatives-cheatsheet.md) for a
consolidated table of every service and its alternatives.
