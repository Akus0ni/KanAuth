# 12 — Alternatives Cheatsheet

One-page overview of every AWS service KanAuth uses and what else could
have filled the same role. Use it when someone at a party asks "why
didn't you use X?" — or when you need to justify a migration.

## Quick Lookup Table

| KanAuth Uses | Role | Cheap Alternatives | Fancy Alternatives | Different Philosophy |
|---|---|---|---|---|
| **Docker** | App packaging | Plain zip deploy | BuildPacks, Nix | Serverless (no container) |
| **ECR** | Image registry | Docker Hub (free public), GHCR | Harbor self-hosted, Quay.io | Bake image into AMI |
| **Elastic Beanstalk** | Container orchestrator | Plain EC2, Lightsail Containers | ECS/Fargate, EKS, App Runner | Lambda containers |
| **EC2 (t3.small)** | Compute | `t3.micro` (free tier), Lightsail | Fargate, Graviton (`t4g`) | Lambda, App Runner |
| **ALB** | Load balancer | Single-instance EB (no LB), nginx on EC2 | NLB, CloudFront, API Gateway | Cloudflare, Fastly |
| **RDS PostgreSQL** | Database | Self-host Postgres on EC2, SQLite | Aurora, Aurora Serverless v2 | DynamoDB, Cognito (for users), Supabase |
| **SSM Parameter Store** | Secret storage | Env vars in EB, plain config | Secrets Manager, Vault | Doppler, 1Password |
| **IAM Roles** | Auth | Access keys (bad), IAM user (okay) | Permissions Boundaries | SSO / Identity Center |
| **VPC + Security Groups** | Network isolation | Default VPC, no SG changes | NACLs, VPC endpoints, PrivateLink | Flat exposure (bad) |
| **CloudFormation** (via EB) | IaC | Click-Ops in console | Terraform, CDK, Pulumi, SAM | Bash + AWS CLI |
| **S3 (EB bucket)** | Artifact store | Local zip on EC2 (bad) | EFS, FSx | GCS, Azure Blob |

## The Decision Matrix

### "I want it to be cheaper"

1. Use `t3.micro` (free tier) instead of `t3.small`. **Saves ~$15/month.**
2. Switch to a single-instance EB environment (no ALB). **Saves ~$20/month.**
3. Switch ECR → Docker Hub public. **Saves pennies; loses privacy.**
4. Switch SSM → plaintext env vars. **Saves $0; loses security.** Not worth it.
5. Use an RDS Reserved Instance (1-year commitment). **Saves ~30%.**
6. Move to AWS App Runner with free-tier limits. **Saves compute cost for low traffic.**

### "I want it to be more reliable"

1. Enable **Multi-AZ** on RDS. Doubles cost; survives AZ failures.
2. Run **≥2 EB instances** across AZs. Doubles compute cost; survives AZ failures and in-place deploys are zero-downtime.
3. Add **CloudWatch alarms** and SNS notifications. Near-free, huge value.
4. Add **Route 53 health checks** + failover records for DNS-level failover.
5. Turn on **RDS automated backups + point-in-time recovery** (already default).
6. Add **WAF** in front of the ALB for layer-7 attack mitigation.

### "I want it to scale 100× bigger"

1. Move from EB to **ECS/Fargate** or **EKS**. Better rolling deploys,
   per-container IAM, no EC2 patching.
2. Move RDS to **Aurora** (3× faster writes, better failover).
3. Put **CloudFront** in front of the ALB for edge caching, DDoS
   protection via Shield.
4. Split read traffic to **RDS read replicas**.
5. Add **Redis (ElastiCache)** for session/refresh-token caching to take
   load off RDS.
6. Switch to **SQS/SNS** for async work so HTTP handlers stay fast.

### "I want to move off AWS"

1. GCP: Cloud Run (≈ App Runner), Cloud SQL (≈ RDS), Secret Manager (≈
   SSM), Artifact Registry (≈ ECR).
2. Azure: App Service or Container Apps, Azure Database for PostgreSQL,
   Key Vault, Azure Container Registry.
3. Fly.io: Docker-first PaaS with built-in Postgres, secrets, and global
   edge deploys.
4. Render.com: Modern Heroku replacement, auto-scales, managed Postgres.
5. Self-host: Hetzner/OVH/DigitalOcean + Docker Compose or K3s.

## Service-By-Service Deep Dive

### Container Orchestration

| Service | When To Pick It | Monthly Cost (KanAuth-sized) |
|---|---|---|
| **Elastic Beanstalk** (current) | Simple single-container app | ~$50 |
| **AWS App Runner** | Even simpler; stateless HTTP only | ~$30 |
| **ECS on Fargate** | Per-task IAM, no EC2 patching | ~$40 |
| **ECS on EC2** | You want the control of EC2 with ECS orchestration | ~$35 |
| **EKS** | Kubernetes shop, multi-team | ~$120 (EKS control plane is $72 alone) |
| **Plain EC2 + Docker Compose** | Learning or hobby | ~$20 |
| **Lambda (container image)** | Sporadic traffic | ~$5 (pay per request) |

### Database

| Service | When To Pick It | Monthly Cost |
|---|---|---|
| **RDS PostgreSQL db.t3.micro** (current) | Standard web app | ~$15 |
| **Aurora PostgreSQL** | 3× faster writes, auto-failover | ~$50 |
| **Aurora Serverless v2** | Spiky traffic, can scale to 0.5 ACU | ~$15–40 |
| **Self-hosted PG on EC2** | Ultra-cheap, full control | ~$5 |
| **DynamoDB** | Pay-per-request, infinite scale | ~$1 for small workloads |
| **Supabase / Neon / Railway** | Great DX, free tier | $0–25 |
| **SQLite (Database__Provider=sqlite)** | Local dev / tiny deployments | $0 |

### Secret Storage

| Service | When To Pick It | Monthly Cost |
|---|---|---|
| **SSM Parameter Store (Standard)** (current) | Small app with a handful of secrets | $0 |
| **SSM Parameter Store (Advanced)** | Need >4 KB values or higher throughput | $0.05/param/month |
| **AWS Secrets Manager** | Automatic RDS password rotation | $0.40/secret/month |
| **HashiCorp Vault** | Multi-cloud, dynamic secrets, fine-grained ACLs | Depends on hosting |
| **Doppler / Infisical / 1Password Secrets Automation** | Better team UX, dev+prod unified | ~$0–15/user/month |

### Load Balancer

| Service | When To Pick It | Monthly Cost |
|---|---|---|
| **Application Load Balancer** (current) | HTTP/HTTPS routing, health checks, TLS termination | ~$20 |
| **Network Load Balancer** | Ultra-low-latency, TCP/UDP, static IP | ~$20 |
| **Classic Load Balancer** | Legacy apps, being retired | ~$20 |
| **CloudFront** | Global CDN + LB in one | $0 baseline + per-request |
| **API Gateway** | Per-request IAM, throttling, API keys | $3.50 per million requests |
| **Self-hosted nginx / HAProxy on EC2** | Cost-obsessed | $0 extra |
| **Cloudflare / Fastly** | Global edge, DDoS protection | $0–200/month |

### Image Registry

| Service | When To Pick It | Monthly Cost |
|---|---|---|
| **ECR Private** (current) | AWS-native workload | ~$0.10 per GB-month |
| **ECR Public** | Open-source distribution | Free egress from public gallery |
| **Docker Hub** | Open-source, public repos | Free public / $5+ private |
| **GitHub Container Registry (ghcr.io)** | GitHub Actions workflows | Free for public / included in GitHub plans |
| **GitLab Registry** | GitLab CI workflows | Included in GitLab |
| **Quay.io** | Vulnerability scanning included | Paid |
| **Self-hosted (`registry:2`)** | Air-gapped environments | Your EC2 cost |

## Decision Trees

### "Should I keep using EB or move to ECS/Fargate?"

```
Are you okay patching an EC2 OS now and then?
├── Yes → Stay on EB
└── No  → Move to Fargate
             Do you already know ECS?
             ├── Yes → ECS on Fargate
             └── No  → Is your app stateless HTTP-only?
                       ├── Yes → App Runner (simpler than ECS)
                       └── No  → Learn ECS; it's worth it
```

### "Should I switch from SSM to Secrets Manager?"

```
Do you need automatic RDS password rotation?
├── Yes → Secrets Manager
└── No  → Are your secrets >4 KB?
          ├── Yes → Secrets Manager
          └── No  → Stay on SSM (free, simpler)
```

### "Should I switch from RDS to Aurora?"

```
Is your DB >20% CPU at peak?
├── Yes → Aurora (3× write throughput)
└── No  → Do you need <1 min failover time?
          ├── Yes → Aurora Multi-AZ
          └── No  → Stay on RDS (half the cost)
```

## The Philosophical Alternatives

Sometimes the right answer isn't a different AWS service — it's a
different **architecture**.

### "Stateless + Managed Services" (KanAuth's current approach)

Run a container. Put all state in managed services (RDS, SSM). Scale
horizontally when needed. **Pros:** simple, cheap, everything is an
AWS feature. **Cons:** lock-in to AWS idioms.

### "Serverless-First"

Each endpoint is a Lambda. Data lives in DynamoDB. Auth uses Cognito.
Static files on S3 + CloudFront. **Pros:** scales to zero, pay per
request, no servers. **Cons:** cold starts, 15-min request cap,
different mental model, harder local dev.

### "Platform-As-A-Service"

Use Heroku / Render / Fly.io / Railway. Git push to deploy. Zero AWS
knowledge needed. **Pros:** unbeatable DX. **Cons:** more expensive at
scale, less control, less portable.

### "Self-Hosted On A VPS"

Rent a Hetzner box for $5/month. Install Docker, Caddy, and PostgreSQL.
**Pros:** ~10× cheaper for small-to-medium apps. **Cons:** you own
uptime, patching, backups, monitoring.

### "Kubernetes Everywhere"

Run EKS. Everything is a CRD. Every service has a Deployment, Service,
and Ingress. **Pros:** one model for everything, huge ecosystem. **Cons:**
massive operational overhead, not worth it for apps with <5 services.

## Why KanAuth Chose What It Chose

| Decision | Why |
|---|---|
| **Elastic Beanstalk** | Simpler than ECS, more capable than App Runner; single-container works out of the box |
| **EC2 t3.small** | Enough for thousands of users; cheap; plays well with EB defaults |
| **ALB** | Free with EB; handles health checks, TLS termination, future auto-scaling |
| **RDS PostgreSQL** | EF Core has great PG support; we want relations + foreign keys |
| **SSM Parameter Store** | Free tier; SecureString is enough; no rotation needed yet |
| **IAM instance profile** | Industry standard; no static keys |
| **VPC default + custom SGs** | Enough isolation for a single-env app |
| **`.ebextensions/`** | Built-in to EB; zero extra tools needed |

All of these choices are **reversible**. Start simple, measure, migrate
when the pain is real.

## Key Takeaways

- There is **no single right architecture** — every service has
  alternatives, and every alternative has trade-offs.
- KanAuth's stack optimizes for **low cost + simplicity**, with a clear
  upgrade path if traffic grows.
- **Change one thing at a time.** The worst thing you can do is rewrite
  on all five axes at once.
- **Every chapter in this folder explained one service in depth.** This
  file is the 30,000-foot view. Zoom back to the chapters when you need
  specifics.

## The End Of The Guide

Congratulations — you have read everything. You should now be able to:

1. Look at `DEPLOYMENT.md` and understand every row in the table.
2. Look at the `.ebextensions/` folder and explain each file line by line.
3. Diagnose a broken deploy by reading logs in the right places.
4. Confidently answer "why did we pick X?" for every AWS service in the stack.
5. Propose a sensible migration path when the app outgrows EB.

If something is still unclear, re-read the chapter it lives in. And if
you find something wrong or outdated, fix it — this folder is yours.
