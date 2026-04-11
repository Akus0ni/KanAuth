# 05 — RDS PostgreSQL (The Database)

## The Story: Renting A Librarian-Run Library

Imagine you want your own library. You could:

**Option A:** Buy a building, buy books, hire librarians, buy fire insurance,
worry about leaks, organize re-shelving, and fix things when they break.

**Option B:** Rent a room in a library that is **already built, staffed,
cleaned, backed up, and insured**. You just say "I want a library" and you
get one. If the lights go out, someone else fixes it. If a book catches fire,
a copy is restored from backup. You never touch any of the plumbing.

**RDS is Option B for databases.** You tell AWS "give me a PostgreSQL," and
AWS gives you a running database, keeps it patched, takes daily backups,
restarts it when it crashes, and lets you scale storage with one click.

- **RDS** = *Relational Database Service*
- **The "relational" part** = data stored as rows in tables (like Excel sheets)
- **PostgreSQL** = the specific database engine — free, open-source, very
  feature-rich. Alternatives: MySQL, MariaDB, Oracle, SQL Server, Aurora.

## Why Not Run PostgreSQL On Our Own EC2?

You *could* run PostgreSQL on the same EC2 as the app. It works! For about a
week. Then something goes wrong:

- The disk fills up → database crashes → app crashes → no users can log in.
- The EC2 dies (hardware failure) → all user data is gone forever.
- You try to upgrade Postgres 15 → 16 → you break it in production.
- Someone needs to take a backup at 2 AM → you forgot to set up a cron job.
- The server reboots → does Postgres come back up automatically? Did you
  write a systemd unit? Did you test it?

RDS handles **all** of that for you, in exchange for a small monthly fee.
That is the trade.

## The KanAuth RDS Instance

From `DEPLOYMENT.md`:

| Property | Value | Meaning |
|---|---|---|
| **Identifier** | `kanauth-db` | Human-friendly name |
| **Engine** | PostgreSQL 18.3 | The database software & version |
| **Instance class** | `db.t3.micro` | 2 vCPU, 1 GB RAM, burstable — same T3 family as EC2, just for RDS |
| **Endpoint** | `kanauth-db.cfeos8cmkeq9.ap-south-2.rds.amazonaws.com:5432` | The DNS address the app connects to |
| **Storage** | 20 GB gp3 | General-purpose SSD, 3000 baseline IOPS |
| **Multi-AZ** | No | Single-AZ (one replica for cost; flip for prod HA) |
| **Security group** | `sg-0a0ddaa51da38a02e` | Only the EB SG can reach port 5432 |

### Why `db.t3.micro`?

Small, cheap (~$15/month), and plenty for KanAuth. A single `db.t3.micro`
can comfortably handle thousands of users because authentication queries are
**tiny and infrequent**: each login is one SELECT + one INSERT. The database
will be idle 99% of the time.

### What Is Multi-AZ?

"AZ" = **Availability Zone** — a physically separate data center within the
same AWS region. An AZ is a campus of buildings; a region has 2–6 AZs.

**Single-AZ:** one primary database in one building. If the building burns
down, you restore from backup (downtime: hours).

**Multi-AZ:** one primary + a synchronous standby in a different building.
If the primary dies, AWS automatically promotes the standby within ~60
seconds. **Double the cost**, but the app survives full data-center
failures.

KanAuth is Single-AZ today (learning project). For real production users,
flip it to Multi-AZ with `--multi-az`.

## How The App Reaches RDS

```
  KanAuth.API container
       │
       │ Connection string from SSM:
       │   Host=kanauth-db.cfeos8cmkeq9.ap-south-2.rds.amazonaws.com
       │   Port=5432
       │   Database=kanauth
       │   Username=kanauth
       │   Password=<from SSM>
       ▼
  TCP 5432 through the VPC
       │
       ▼
  RDS security group (sg-0a0ddaa51da38a02e)
       │
       │ Rule: allow port 5432 from sg-0fbd0935c6d8bc906 (EB SG)
       ▼
  PostgreSQL 18.3 listener → queries → result rows → back to app
```

**Crucial:** The RDS is in a **private subnet**. It has no public IP. You
cannot connect to it from your laptop. The only thing allowed to talk to it
is the EB security group. This is a security win — even if the entire
internet knew the DB endpoint hostname, they could not open a connection.

## The Security Group Rule You Must Add Manually

When `eb create` finishes, RDS still blocks the EB EC2 by default. You have
to add this one rule **once**:

```bash
aws ec2 authorize-security-group-ingress \
  --region ap-south-2 \
  --group-id sg-0a0ddaa51da38a02e \
  --protocol tcp --port 5432 \
  --source-group sg-0fbd0935c6d8bc906
```

**What it says in English:** "On the RDS security group, allow inbound TCP
port 5432 from any member of the EB security group."

**Why it is not automatic:** EB doesn't know your RDS exists — it is a
separate resource. You have to wire them together.

If you skip this step, the app will start up but fail on the first DB query
with `Failed to connect to <IP>:5432`. This is the #1 mistake when standing
up the environment. See the troubleshooting table in `DEPLOYMENT.md`.

## Exact Steps: Provisioning The RDS

You already have one, but here is how it was created for the first time:

### Step 1 — Create a DB subnet group

```bash
aws rds create-db-subnet-group \
  --db-subnet-group-name kanauth-db-subnet \
  --db-subnet-group-description "KanAuth DB subnet group" \
  --subnet-ids subnet-xxx subnet-yyy
```

**What it does:** Tells RDS which subnets to place the database in. RDS
requires **at least two subnets in two different AZs** (even for Single-AZ,
so that if you flip to Multi-AZ later, both spots are available).

### Step 2 — Create the RDS instance

```bash
aws rds create-db-instance \
  --db-instance-identifier kanauth-db \
  --db-instance-class db.t3.micro \
  --engine postgres \
  --engine-version "18" \
  --master-username kanauth \
  --master-user-password "<STRONG_PASSWORD>" \
  --db-name kanauth \
  --db-subnet-group-name kanauth-db-subnet \
  --no-publicly-accessible \
  --allocated-storage 20 \
  --storage-type gp3 \
  --backup-retention-period 7
```

Flag by flag:

- `--db-instance-class db.t3.micro` — small, cheap, burstable.
- `--engine postgres --engine-version "18"` — PostgreSQL 18.x.
- `--master-username / --master-user-password` — the superuser the app connects as.
- `--db-name kanauth` — creates a database named `kanauth` inside the instance.
- `--db-subnet-group-name` — where to put it (step 1).
- `--no-publicly-accessible` — **critical**: no public IP. The RDS only
  exists inside the VPC.
- `--allocated-storage 20` — 20 GB of SSD.
- `--storage-type gp3` — general-purpose SSD, better price/perf than gp2.
- `--backup-retention-period 7` — keep daily automated backups for 7 days.

Takes ~10 minutes to provision.

### Step 3 — Store the connection string in SSM

```bash
aws ssm put-parameter \
  --name "/kanauth/prod/ConnectionStrings__DefaultConnection" \
  --type SecureString \
  --value "Host=kanauth-db.cfeos8cmkeq9.ap-south-2.rds.amazonaws.com;Port=5432;Database=kanauth;Username=kanauth;Password=<STRONG_PASSWORD>"
```

See [06-ssm-parameter-store.md](06-ssm-parameter-store.md) for why.

### Step 4 — Wire the security groups (the one above)

```bash
aws ec2 authorize-security-group-ingress \
  --group-id <RDS-SG> --protocol tcp --port 5432 \
  --source-group <EB-SG>
```

## How KanAuth's Code Talks To RDS

Look at `src/KanAuth.Infrastructure/DependencyInjection.cs`:

```csharp
if (provider == "postgresql")
{
    services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString));
}
```

`UseNpgsql` is the EF Core driver for PostgreSQL. EF Core translates C# LINQ
queries into SQL, sends them over TCP to RDS, and materialises the result
into objects. The RDS is "just a SQL server" — nothing RDS-specific in the code.

### Migrations On Startup

`src/KanAuth.API/Program.cs` calls `db.Database.Migrate()` when the app
starts in production. This applies any pending EF Core migrations. First
boot after a deploy does the schema change; subsequent boots are a no-op.

This is safe for a single-instance deployment. For multi-instance, you
should instead run migrations from CI before the deploy so two instances do
not try to migrate simultaneously.

## Alternatives: Other Databases For KanAuth

| Alternative | What It Is | Why You'd Pick It |
|---|---|---|
| **Amazon Aurora PostgreSQL** | Amazon's own PostgreSQL-compatible engine, 3× faster writes, storage auto-scales | Production workloads, larger data volumes, faster failover |
| **Amazon Aurora Serverless v2** | Aurora that scales CPU/RAM up and down, pay per ACU-second | Spiky workloads, dev/test, wanting to sleep at night |
| **Self-hosted PostgreSQL on EC2** | Install `postgresql-server` on an EC2 | Cheapest, full control, zero managed service fees |
| **Amazon DynamoDB** | AWS's native NoSQL key-value store | Massive scale, single-digit ms latency, pay-per-request |
| **Amazon DocumentDB** | MongoDB-compatible | Document-oriented data |
| **Supabase / Neon / Railway Postgres** | Third-party managed Postgres with free tiers | Hobby projects, better DX than RDS |
| **SQLite** | Single-file embedded DB, no server | Local dev, small single-writer apps. KanAuth supports this via `Database__Provider=sqlite` |
| **Microsoft SQL Server (RDS)** | Same RDS, different engine | Organisations with existing SQL Server licenses |

### Why Postgres (Not MySQL)?

Postgres has better:
- JSON support (`jsonb` column type, lots of operators).
- Transactional DDL — you can roll back a migration mid-flight.
- Window functions, CTEs, and advanced SQL.
- Open-source governance (not Oracle-owned).

MySQL is still a fine choice. The pick between them is now mostly team
preference. KanAuth picked Postgres because EF Core's PG support is mature
and the community leans PG for new greenfield .NET projects.

### Why Not DynamoDB?

DynamoDB is cheaper per request, scales to infinity, and has no connection
limits. But:

- **Joins don't exist.** You model access patterns up front, not relations.
- **EF Core doesn't support it.** You would throw out all the repository
  code and use the AWS SDK directly.
- **Migrations are a different beast.** Adding a column is free (schema-
  less), but changing access patterns can require data re-bucketing.

For a "users + refresh tokens + foreign keys" app, relational is the right
shape.

### Why Not Self-Host Postgres On EC2?

Saves ~$15/month but costs you:
- Manual backups, monitoring, patching, tuning, storage growth alerts.
- No point-in-time recovery without extra work.
- Harder HA story.

The RDS markup is paying AWS to be your DBA. For a serious app, it is
cheaper than hiring one.

## Production Hardening Checklist

| Item | Flag | Why |
|---|---|---|
| Multi-AZ | `--multi-az` | Survives AZ failure, automatic failover |
| Encryption at rest | `--storage-encrypted --kms-key-id ...` | Compliance, sleep at night |
| Deletion protection | `--deletion-protection` | Prevents accidental `aws rds delete-db-instance` |
| Enhanced monitoring | `--monitoring-interval 60` | Per-process OS metrics in CloudWatch |
| Performance Insights | `--enable-performance-insights` | Query-level profiling |
| IAM database auth | `--enable-iam-database-authentication` | Optional: use IAM instead of passwords |
| Automatic minor upgrades | `--auto-minor-version-upgrade` | Security patches without action |

## Key Takeaways

- **RDS = managed relational database.** AWS runs it; you use it.
- KanAuth uses **PostgreSQL 18.3 on `db.t3.micro`**, single-AZ, private.
- **One manual security group rule** (port 5432 from EB SG → RDS SG) is
  required the first time.
- Connection string is **stored in SSM as a SecureString** and injected by
  `.ebextensions` at boot.
- EF Core `db.Database.Migrate()` runs migrations on startup.
- **Alternatives:** Aurora, DynamoDB, self-hosted Postgres, SQLite. All
  valid for different workloads.

## Next

Read [06-ssm-parameter-store.md](06-ssm-parameter-store.md) to see where the
database password actually lives.
