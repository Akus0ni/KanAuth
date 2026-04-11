# 08 — VPC, Subnets, and Security Groups (The Private Network)

## The Story: The Gated Community

Imagine a gated community. It has:

- A **big fence around the whole neighborhood** — no one can wander in
  from the street.
- **Several streets** inside, each in a different part of the neighborhood
  (north side, south side, east side).
- **One main gate** where guests enter from outside.
- **Each house has its own door with its own guest list.** Even if you are
  inside the community, you can only enter a house if you are on its list.

Translate:

- **Gated community = VPC** (Virtual Private Cloud)
- **Streets = Subnets**
- **Main gate = Internet Gateway**
- **House guest list = Security Group**

## Why Do We Need A Private Network At All?

In 2005, people deployed apps on servers that lived **directly on the public
internet**. Every port was reachable from every IP in the world by default.
The only thing keeping attackers out was careful `iptables` rules.

In 2025, the standard is **private by default**: your compute lives in a
private network, and you **explicitly open** the one or two ports you need.
This is called the **principle of default deny**.

A VPC gives KanAuth three things:

1. **The RDS has no public IP.** Even if someone knows the DB endpoint
   hostname, they cannot connect to it from the internet.
2. **The EC2 only accepts traffic from the ALB.** A direct request to the
   EC2's private IP from anywhere else is dropped.
3. **Traffic inside the VPC is free.** EC2 → RDS does not cost data
   transfer fees; cross-AZ does but it is inside AWS.

## The VPC Layers, Visualized

```
┌───────────────────────────────────────────────────────────────────────┐
│  VPC  vpc-0b9635f52b41d1e29   (10.0.0.0/16 or similar)                │
│                                                                       │
│  ┌─────────────────────┐        ┌─────────────────────┐               │
│  │  Public Subnet A    │        │  Public Subnet B    │               │
│  │  (10.0.1.0/24)      │        │  (10.0.2.0/24)      │               │
│  │                     │        │                     │               │
│  │    ┌──────────┐     │        │    ┌──────────┐     │               │
│  │    │   ALB    │◄────┼────────┼────│   ALB    │     │               │
│  │    └──────────┘     │        │    └──────────┘     │               │
│  │                     │        │                     │               │
│  │    ┌──────────┐     │        │                     │               │
│  │    │   EC2    │     │        │                     │               │
│  │    │ t3.small │     │        │                     │               │
│  │    └──────────┘     │        │                     │               │
│  └──────────┬──────────┘        └─────────────────────┘               │
│             │                                                         │
│             ▼                                                         │
│  ┌─────────────────────┐        ┌─────────────────────┐               │
│  │  Private Subnet A   │        │  Private Subnet B   │               │
│  │  (10.0.11.0/24)     │        │  (10.0.12.0/24)     │               │
│  │                     │        │                     │               │
│  │    ┌──────────┐     │        │                     │               │
│  │    │   RDS    │     │        │  (spare capacity    │               │
│  │    │Postgres  │     │        │   for Multi-AZ      │               │
│  │    └──────────┘     │        │   failover)         │               │
│  └─────────────────────┘        └─────────────────────┘               │
│                                                                       │
│             ▲                                                         │
│             │ Internet Gateway                                        │
│             │                                                         │
└─────────────┼─────────────────────────────────────────────────────────┘
              │
           Internet
```

**Key insight:** The ALB is in the public subnet (it has a public IP). The
RDS is in a private subnet (no public IP). The EC2 is technically in a
public subnet too (so it can pull from ECR and SSM without a NAT), but its
security group locks down inbound traffic.

## The Four Network Objects To Know

### 1. VPC (The Outer Fence)

A VPC is defined by a **CIDR block** — a range of private IP addresses.
KanAuth's VPC is `vpc-0b9635f52b41d1e29`. The CIDR is something like
`10.0.0.0/16`, meaning addresses `10.0.0.0` through `10.0.255.255` (65,536 IPs).

These addresses are **only valid inside the VPC**. They never appear on the
internet.

### 2. Subnets (The Streets)

A subnet is a slice of the VPC CIDR, anchored to **one availability zone**.

- **Public subnet:** has a route to the internet gateway. Resources here
  can reach the internet and (if they have a public IP) be reached from it.
- **Private subnet:** no route to the internet gateway. Resources here
  cannot talk to the internet directly.

KanAuth's RDS is in a private subnet (`subnet-065e2aa91caa5de8d`).

> **Wait — how does an EC2 in a public subnet avoid exposing the app?**
> By not having its security group allow inbound traffic from anywhere
> except the ALB. "Public subnet" is about routing; "security group" is
> about filtering. Both together make the EC2 safe.

### 3. Security Groups (The House Guest Lists)

A security group is a **stateful firewall** attached to an EC2, RDS, ALB,
or Lambda ENI. It has two rule lists:

- **Inbound rules:** what incoming traffic is allowed.
- **Outbound rules:** what outgoing traffic is allowed.

"Stateful" means: if you allow an outbound request, the response is
automatically allowed back, even without an explicit inbound rule.

### KanAuth's Security Groups

From `DEPLOYMENT.md`:

**EB EC2 SG** (`sg-0fbd0935c6d8bc906`):

| Direction | Protocol | Port | Source | Why |
|---|---|---|---|---|
| Inbound | TCP | 80 | ALB SG | Only ALB can reach app |
| Outbound | ALL | ALL | 0.0.0.0/0 | EC2 needs to reach ECR, SSM, RDS |

**RDS SG** (`sg-0a0ddaa51da38a02e`):

| Direction | Protocol | Port | Source | Why |
|---|---|---|---|---|
| Inbound | TCP | 5432 | EB EC2 SG | Only the app can hit the DB |
| Outbound | ALL | ALL | 0.0.0.0/0 | Default; unused in practice |

**ALB SG** (auto-created by EB):

| Direction | Protocol | Port | Source | Why |
|---|---|---|---|---|
| Inbound | TCP | 80 | 0.0.0.0/0 | Public HTTP |
| Inbound | TCP | 443 | 0.0.0.0/0 | Public HTTPS (when you add a cert) |
| Outbound | ALL | ALL | 0.0.0.0/0 | ALB sends traffic to EC2 |

### 4. The Internet Gateway (The Main Gate)

The Internet Gateway (IGW) is a logical object attached to a VPC that lets
traffic flow to and from the public internet. Only **one per VPC**. You
rarely touch it; EB's default VPC already has one.

## Security Groups vs NACLs (Network ACLs)

Two layers of network filtering exist in a VPC:

| | Security Group | NACL (Network ACL) |
|---|---|---|
| **Scope** | Per-resource (attached to EC2, RDS, ALB) | Per-subnet |
| **Stateful?** | Yes | **No** — response must be explicitly allowed |
| **Order** | Only allow rules, all evaluated | Numbered rules, evaluated in order |
| **Default** | Deny all inbound, allow all outbound | Allow all inbound and outbound |
| **When to use** | 99% of cases | Subnet-wide blacklists, compliance requirements |

**Rule of thumb:** use security groups for app-level rules. Leave NACLs at
their defaults unless you have a specific compliance reason.

## The Critical Rule You Must Add Manually

When `eb create` finishes, **RDS still does not trust the EB SG**. You add
it yourself:

```bash
aws ec2 authorize-security-group-ingress \
  --region ap-south-2 \
  --group-id sg-0a0ddaa51da38a02e \
  --protocol tcp --port 5432 \
  --source-group sg-0fbd0935c6d8bc906
```

**Translation:** "On the RDS SG, allow inbound TCP 5432 from any resource
that has the EB EC2 SG attached."

### Why Reference A SG, Not An IP?

If you wrote `--cidr 10.0.1.42/32` (the EC2's private IP), the rule would
break the first time EB replaces the instance with a new one (new IP).
Referencing the **SG** means the rule tracks "whatever EC2s are currently
in the EB group", which is stable across replacements.

This pattern — "SG as a dynamic label" — is one of the best design decisions
in AWS networking.

## Exact Steps: Inspecting The Network

### See the VPC and its subnets

```bash
aws ec2 describe-vpcs \
  --vpc-ids vpc-0b9635f52b41d1e29 \
  --region ap-south-2

aws ec2 describe-subnets \
  --filters "Name=vpc-id,Values=vpc-0b9635f52b41d1e29" \
  --region ap-south-2
```

### See the security group rules

```bash
aws ec2 describe-security-groups \
  --group-ids sg-0a0ddaa51da38a02e sg-0fbd0935c6d8bc906 \
  --region ap-south-2
```

Output shows the current inbound/outbound rules. If connectivity is
broken, start here.

### Add / remove a rule at runtime

```bash
# Add inbound rule
aws ec2 authorize-security-group-ingress --group-id <sg> --protocol tcp --port 5432 --source-group <other-sg>

# Remove inbound rule
aws ec2 revoke-security-group-ingress --group-id <sg> --protocol tcp --port 5432 --source-group <other-sg>
```

Rules take effect **immediately** — no restart needed.

## Alternatives: Other Networking Models

| Alternative | Pros | Cons | When |
|---|---|---|---|
| **AWS default VPC** | Pre-created in every region | Broad default, less isolation | Quick experiments |
| **Custom VPC with public + private subnets + NAT gateway** | Real isolation for private workloads | Costs ~$35/month per NAT | Production apps with strict egress control |
| **PrivateLink endpoints** | Reach AWS services from private subnets without NAT | Extra $7/month per endpoint | Fully private apps; ECR / SSM / S3 from private subnets |
| **Transit Gateway** | Connect multiple VPCs and on-prem | Complex, expensive | Enterprises with many VPCs |
| **AWS Cloud WAN** | Global network orchestration | Very new, expensive | Large multi-region enterprises |
| **Flat public exposure (no VPC features)** | Simplest | Insecure | Hobby projects you don't care about |

### KanAuth's Choice

KanAuth uses the **default VPC** that EB creates (or the account's default
VPC). Subnets are `public` so EC2 can reach ECR/SSM without a NAT. The RDS
is in private subnets.

For production hardening you'd:
1. Create a **custom VPC** with clear public/private split.
2. Put the **EC2 in private subnets** so it has no direct internet exposure.
3. Add a **NAT Gateway** in a public subnet so private EC2 can still reach
   ECR and SSM. Or use **PrivateLink** endpoints for cheaper per-service access.

## Key Takeaways

- **VPC = private network box.** Subnets = slices of it per AZ. Security
  Groups = per-resource firewalls.
- **Default deny:** nothing gets through unless a rule explicitly allows it.
- KanAuth uses **three SGs**: ALB SG (public), EB EC2 SG (only ALB in),
  RDS SG (only EB SG in on 5432).
- **One manual rule** connects EB EC2 SG → RDS SG on port 5432.
- **Reference SGs, not IPs**, so rules survive instance replacement.
- **Alternatives:** custom VPCs with NAT, PrivateLink, Transit Gateway —
  all more advanced; KanAuth does not need them yet.

## Next

Read [09-ebextensions.md](09-ebextensions.md) to walk through the files
that glue all of this together.
