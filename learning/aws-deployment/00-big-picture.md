# 00 — The Big Picture

## The Story: The Pizza Shop

Imagine you built a **pizza recipe** at home. It works perfectly in your
kitchen. Now you want to **sell pizzas to the whole internet**. What do you
need?

1. A **kitchen** to cook in (a computer).
2. A **shop front** so customers can walk in (a public web address).
3. A **greeter** who takes the order and passes it to the right cook (a load
   balancer).
4. A **fridge** to store ingredients long-term (a database).
5. A **locked safe** for your secret sauce recipe (secret storage).
6. A **security guard** who checks ID badges (permissions / IAM).
7. A **manager** who hires cooks, replaces broken ovens, and watches for
   problems (the deployment auto-pilot).

Every AWS service in this folder is just one of those pizza-shop roles. That
is the entire mental model. Everything else is detail.

## The Real Architecture

```
                  🌐 Internet (any browser, any phone)
                           │
                           ▼
        ┌─────────────────────────────────────┐
        │  Application Load Balancer (ALB)    │  ← the greeter
        │  listens on port 80                 │
        └──────────────┬──────────────────────┘
                       │ forwards to
                       ▼
        ┌─────────────────────────────────────┐
        │  EC2 instance (t3.small)            │  ← the kitchen
        │  managed by Elastic Beanstalk       │  ← the manager
        │                                     │
        │   ┌─────────────────────────────┐   │
        │   │  Docker container           │   │  ← the box the app lives in
        │   │  KanAuth.API (.NET 8)       │   │
        │   │  listens on port 80         │   │
        │   └──────────┬──────────────────┘   │
        └──────────────┼──────────────────────┘
                       │ TCP 5432
                       ▼
        ┌─────────────────────────────────────┐
        │  RDS PostgreSQL 18.3                │  ← the fridge
        │  db.t3.micro, private subnet        │
        └─────────────────────────────────────┘

        Pulled from:              Secrets injected from:
        ┌──────────┐               ┌──────────────────────┐
        │   ECR    │               │ SSM Parameter Store  │  ← the safe
        │ (image   │               │ JWT secret           │
        │  store)  │               │ DB connection string │
        └──────────┘               └──────────────────────┘
```

## What Happens When A User Logs In

Trace the journey of one `POST /api/v1/auth/login` request:

1. **Browser → Internet.** The user sends a request to
   `kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com/api/v1/auth/login`.
2. **DNS → ALB.** AWS DNS resolves that name to the ALB's public IP.
3. **ALB → EC2.** The ALB picks a healthy EC2 instance (we only have one
   today) and forwards the HTTP request to it.
4. **nginx → container.** On the EC2 instance, nginx (installed by the EB
   Docker platform) proxies the request to our Docker container on port 80.
5. **.NET app → RDS.** The `AuthService` reads the user row from PostgreSQL,
   checks the password with BCrypt, and issues JWT tokens.
6. **Response flows back.** Container → nginx → ALB → internet → browser.

Every step uses one of the AWS services in this folder. If any one piece is
broken, the request fails — that is why you need to understand all of them.

## The Secret Injection Flow (Happens Once At Deploy)

Secrets never live in the Docker image or git. Here is how they reach the app:

```
  git push ──► eb deploy
                  │
                  │ 1. Bundles Dockerrun.aws.json + .ebextensions/ into a zip
                  │ 2. Uploads the zip to the EB S3 bucket
                  │ 3. Tells EB to deploy
                  ▼
         ┌──────────────────┐
         │  EC2 boot script │
         └────────┬─────────┘
                  │ 4. Runs .ebextensions/02-ssm-secrets.config
                  │    which calls `aws ssm get-parameter`
                  ▼
         ┌──────────────────┐
         │  SSM Parameter   │
         │  Store           │──► decrypted values written to /opt/.../env.list
         └──────────────────┘
                  │ 5. `docker run --env-file env.list` starts the container
                  ▼
         ┌──────────────────┐
         │  KanAuth.API     │
         │  reads env vars  │
         │  at startup      │
         └──────────────────┘
```

## Why So Many Services?

A 10-year-old might reasonably ask: **"Why can't one service do all of this?"**
The answer is **separation of concerns** — same reason a pizza shop has
different people for cooking, cashiering, and cleaning. When one thing breaks,
only one person has to fix it, and you can scale each role independently.

- Want to serve 10× more customers? Add more **EC2 cooks**. The kitchen,
  database, and safe do not change.
- Want to change the secret sauce? Update **SSM** and redeploy. No code change.
- Database full? Upgrade **RDS** storage. No app change.

## What's Next

Open [01-docker-and-ecr.md](01-docker-and-ecr.md). Every file from here builds
on this picture one service at a time.
