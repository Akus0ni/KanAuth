# KanAuth — AWS Deployment Learning Guide

Welcome! This folder teaches you **how KanAuth is deployed to AWS** in a way
that a curious 10-year-old could follow. You will learn what every AWS piece
does, why we picked it, and what else we could have used instead.

## Who Is This For?

- You have never deployed anything to the cloud before.
- You have heard of AWS but it still feels like alphabet soup (EC2? ECR? RDS?).
- You want to understand **why** each service exists, not just how to click buttons.

## How To Read This

Read the files in order. Each one builds on the last, like stacking LEGO blocks.
Every file has the same four parts:

1. **The Story** — a simple real-world analogy a kid would understand.
2. **What It Does** — the technical role of the service in KanAuth.
3. **Exact Steps** — the real commands, copy-pasteable, with notes explaining each.
4. **Alternatives** — other services that could do the same job, with trade-offs.

## The Files

| # | File | What You Learn |
|---|------|----------------|
| 00 | [00-big-picture.md](00-big-picture.md) | The whole deployment on one page — request flow from browser to database |
| 01 | [01-docker-and-ecr.md](01-docker-and-ecr.md) | Docker images and Elastic Container Registry (where we store the image) |
| 02 | [02-elastic-beanstalk.md](02-elastic-beanstalk.md) | Elastic Beanstalk — the "auto-pilot" that runs our app |
| 03 | [03-ec2.md](03-ec2.md) | EC2 — the actual computer running the container |
| 04 | [04-load-balancer-alb.md](04-load-balancer-alb.md) | Application Load Balancer — the receptionist at the front door |
| 05 | [05-rds-postgres.md](05-rds-postgres.md) | RDS PostgreSQL — the managed database |
| 06 | [06-ssm-parameter-store.md](06-ssm-parameter-store.md) | SSM Parameter Store — the secret safe for passwords |
| 07 | [07-iam-and-security.md](07-iam-and-security.md) | IAM — the keycard system for who-can-do-what |
| 08 | [08-vpc-subnets-security-groups.md](08-vpc-subnets-security-groups.md) | VPC, subnets, and security groups — the private network |
| 09 | [09-ebextensions.md](09-ebextensions.md) | `.ebextensions/` — the configuration folder that glues everything together |
| 10 | [10-cloudformation-s3.md](10-cloudformation-s3.md) | CloudFormation + S3 — what EB creates behind the scenes |
| 11 | [11-full-deploy-walkthrough.md](11-full-deploy-walkthrough.md) | Step-by-step runbook from zero to deployed |
| 12 | [12-alternatives-cheatsheet.md](12-alternatives-cheatsheet.md) | Every service compared to its alternatives in one table |

## The Big Idea (In One Sentence)

> We put our app in a **box** (Docker), store the box in a **warehouse** (ECR),
> tell an **auto-pilot** (Elastic Beanstalk) to run the box on a **computer**
> (EC2), protected by a **receptionist** (ALB), talking to a **managed
> database** (RDS), with secrets kept in a **safe** (SSM).

That is the entire deployment in one sentence. The files below expand each
bolded word into its own chapter.

## Before You Start

- Read [../00-overview.md](../00-overview.md) first to understand what KanAuth is.
- Skim [../../DEPLOYMENT.md](../../DEPLOYMENT.md) — that is the real production
  reference document. This folder exists to **explain** what that document does.
- You do not need an AWS account to read these files. If you want to follow
  along hands-on, a free-tier AWS account is enough for everything except RDS
  (which has a free tier too for `db.t3.micro`).
