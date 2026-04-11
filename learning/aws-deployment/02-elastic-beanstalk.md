# 02 — Elastic Beanstalk (EB)

## The Story: The Robot Restaurant Manager

Imagine you own a pizza shop but you hate dealing with HR, maintenance,
hiring, and cleaning. You hire a **robot manager** and tell it:

> "Here is the pizza recipe. Keep one cook working at all times. If the cook
> quits, hire a new one. If the oven breaks, replace it. If a customer
> complains, tell me. When I hand you a new recipe, roll it out without
> losing any customers."

The robot does all of that automatically. You never touch the kitchen yourself.

**Elastic Beanstalk is that robot manager.** You hand it a Docker image and
a couple of config files, and EB silently creates:

- An EC2 instance (the computer)
- A security group (the firewall)
- An Application Load Balancer (the greeter)
- A CloudFormation stack (the blueprint)
- An S3 bucket (the file drop-off)
- A launch template (for auto-scaling later)
- Enhanced health reporting (the "is the app okay?" checker)

All of that — from **one command**.

## Why Elastic Beanstalk For KanAuth?

KanAuth is a small single-container API. It does not need fancy orchestration
like Kubernetes. Elastic Beanstalk is the Goldilocks middle:

- **More automated than raw EC2** — no manual SSH to install Docker.
- **Less complex than ECS/EKS** — no task definitions, no cluster, no YAML jungle.
- **Works natively with Docker** — the `Docker running on 64-bit Amazon Linux 2023`
  platform just takes a `Dockerrun.aws.json` and runs it.

Think of the spectrum:

```
Less work ◄────────────────────────────────────────► More control
Lambda    App Runner    Beanstalk    ECS    EKS    Plain EC2
                           ▲
                           │
                        KanAuth
```

## What EB Manages For You

When you type `eb create`, EB builds a **CloudFormation stack** with all of
these resources. You never write the CloudFormation yourself — EB writes it
for you.

| Resource EB Creates | Why It Exists |
|---|---|
| **EC2 instance** (`t3.small`) | Runs the Docker container |
| **Auto Scaling Group** | Replaces the EC2 if it dies; can add more if traffic spikes |
| **Security Groups** | Firewall rules: allow ALB → EC2 on port 80 |
| **Application Load Balancer** | Public entry point, does health checks |
| **Target Group** | Tracks which EC2 instances are healthy enough to receive traffic |
| **S3 bucket** | Stores the deployment zip (`Dockerrun.aws.json` + `.ebextensions`) |
| **CloudWatch alarms** | Alerts when CPU or health degrades |
| **IAM service role** | EB itself needs permissions to manage EC2, ALB, etc. |

This is **a lot of stuff** for one command. That is the magic.

## KanAuth's EB Configuration (In This Repo)

Three files tell EB what to do:

### 1. `Dockerrun.aws.json` (project root)

```json
{
  "AWSEBDockerrunVersion": "1",
  "Image": {
    "Name": "116743944666.dkr.ecr.ap-south-2.amazonaws.com/kanauth-api:latest",
    "Update": "true"
  },
  "Ports": [{ "ContainerPort": "80" }],
  "Logging": "/app/logs"
}
```

**Line by line:**
- `AWSEBDockerrunVersion: "1"` — single-container mode (v2 is for multi-container, deprecated).
- `Image.Name` — the full ECR address. EB does `docker pull` on this.
- `Update: "true"` — always pull the latest layers, even if cached.
- `ContainerPort: "80"` — the port the .NET app listens on inside the container.
- `Logging: "/app/logs"` — which directory to tail into CloudWatch logs.

### 2. `.ebextensions/01_environment.config`

Injects **non-secret** env vars via EB's native `option_settings`:

```yaml
option_settings:
  aws:elasticbeanstalk:application:environment:
    ASPNETCORE_ENVIRONMENT: Production
    Database__Provider: postgresql
    Database__AutoMigrate: "true"
    Jwt__Issuer: KanAuth
    Jwt__Audience: KanAuth.Clients
```

These show up as environment variables in the container at runtime.

### 3. `.ebextensions/02-ssm-secrets.config`

Runs a shell command **before** the container starts. It fetches secrets
from SSM and writes them to `env.list`, which EB passes to
`docker run --env-file`.

This is why the JWT secret never sits in git.

### 4. `.ebextensions/03-healthcheck.config`

Tells the ALB to check `/health` and expect HTTP 200:

```yaml
option_settings:
  aws:elasticbeanstalk:environment:process:default:
    HealthCheckPath: /health
    MatcherHTTPCode: "200"
  aws:elasticbeanstalk:healthreporting:system:
    SystemType: enhanced
```

> **Gotcha:** `MatcherHTTPCode` must be a **quoted string**, not an integer.
> If you write `MatcherHTTPCode: 200` (no quotes), EB silently ignores the
> entire namespace — health checks never get configured. This bit us in real
> life; see `DEPLOYMENT.md` troubleshooting table.

## Exact Steps: First-Time EB Setup

### Step 1 — Install the EB CLI

```bash
pip install awsebcli botocore[crt]
```

**What it does:** Installs the `eb` command, which is just a friendlier
wrapper around the AWS CLI. You can do everything in the AWS Console too,
but the CLI is faster and more reproducible.

### Step 2 — Initialize the app in this repo (once)

```bash
eb init kanauth --platform "Docker" --region ap-south-2
```

**What it does:** Creates `.elasticbeanstalk/config.yml`. This file tells
the EB CLI which AWS account, region, application, and platform to use, so
you do not have to repeat them every command.

**Why:** Without this, `eb` has no idea where you want to deploy.

### Step 3 — Create the environment (once)

```bash
eb create kanauth-prod \
  --instance-types t3.small \
  --elb-type application \
  --instance_profile kanauth-eb-instance-profile \
  --envvars ASPNETCORE_ENVIRONMENT=Production
```

**What it does:** Provisions **everything** in the table above. Takes 5–10
minutes the first time because CloudFormation has to create ~25 resources.

- `--instance-types t3.small` — 2 vCPU, 2 GB RAM. Enough for a low-traffic API.
- `--elb-type application` — use ALB (not classic or network LB).
- `--instance_profile` — the IAM role the EC2 will assume (see `07-iam-and-security.md`).
- `--envvars` — shortcut to set one env var; equivalent to
  `option_settings` in a config file.

> **Gotcha:** The flag is spelled with an **underscore** (`--instance_profile`),
> not `--instance-profile`. This is a long-standing EB CLI quirk.

### Step 4 — Deploy a new version

```bash
eb deploy
```

**What it does** (in order):
1. Zips `Dockerrun.aws.json` + `.ebextensions/` (respects `.ebignore`).
2. Uploads the zip to the EB S3 bucket.
3. Tells EB there is a new "application version".
4. EB triggers a rolling deploy: stops the old container, runs the new one.
5. ALB re-registers the instance once health checks pass.

> **Important:** `eb deploy` does **not** build or push a new Docker image.
> It only updates config files. If your code changed, you must re-run
> `docker build` → `docker push` **first**, then `eb deploy`.

### Step 5 — Watch it happen

```bash
eb events -f      # live event stream
eb status         # current health
eb logs --all     # download logs from the instance
```

### Step 6 — Open it

```bash
eb open
```

Opens `http://kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com` in
your browser.

## Alternatives: Other Ways To Run A Container On AWS

| Alternative | What It Is | When It Beats EB |
|---|---|---|
| **AWS App Runner** | Newer, even simpler: point it at an image, it runs. Auto-scales to zero. | A truly simple single-container app with no need for VPC/RDS control. **Downside:** no easy way to put the container in a private VPC with an RDS. |
| **ECS on Fargate** | Managed container orchestrator, no EC2 to think about. Bills per second per container. | When you want multi-container apps, or need per-task IAM roles, or want to not manage EC2 at all. **Downside:** more config (task definitions, cluster, service). |
| **ECS on EC2** | Same as Fargate but you own the EC2s. | When you want Fargate's orchestration but Fargate is too expensive at scale. |
| **EKS (Kubernetes)** | Full-fat Kubernetes control plane as a service. | Huge teams, polyglot workloads, existing K8s expertise. **Downside:** massive operational overhead for a small API like KanAuth. |
| **Plain EC2 + Docker** | SSH into a VM, `docker run`. | Hobby project, learning. No HA, no rolling deploys, no auto-scale. |
| **AWS Lightsail Containers** | Simplified container service; flat monthly pricing. | Predictable small workloads. Less AWS integration. |
| **Lambda with container image** | Serverless, image up to 10 GB. | Spiky / infrequent traffic. **Downside:** 15 min max per request; no WebSockets; cold starts. |

### How To Decide

A quick decision tree:

1. Is the app **a simple HTTP API**? → EB, App Runner, or Lightsail.
2. Do you need **VPC-level control** (talking to a private RDS)? → Prefer EB or ECS (App Runner has limits here).
3. Do you already know **Kubernetes**? → EKS.
4. Is traffic **spiky and low-average**? → Lambda or App Runner (scales to zero).
5. Is it a **learning project**? → Plain EC2 to learn fundamentals, then migrate.

KanAuth picked **EB** because:
- It is a simple container app → EB/App Runner/Lightsail territory.
- It needs a **private RDS** → rules out App Runner's simplest mode.
- The team wants **learning value** without full Kubernetes complexity → EB wins.

## Key Takeaways

- **EB is a robot restaurant manager.** You hand it a recipe; it runs the shop.
- **One command (`eb create`) creates ~25 AWS resources** via CloudFormation.
- **Three config files** (`Dockerrun.aws.json` + two `.ebextensions` files)
  fully describe the deployment.
- **EB is not the only option** — ECS, EKS, App Runner, Lambda, plain EC2
  all exist, each with different trade-offs.

## Next

Read [03-ec2.md](03-ec2.md) to learn about the actual computer EB runs the
container on.
