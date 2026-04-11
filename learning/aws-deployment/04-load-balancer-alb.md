# 04 — Application Load Balancer (ALB)

## The Story: The Receptionist At A Doctor's Office

Imagine a doctor's office with one front desk and three doctors in the back.
When patients arrive, they do not walk straight into a random exam room.
They tell the **receptionist** their name, the receptionist looks at who
is free, and sends them to the right doctor.

If a doctor is on a lunch break, the receptionist does not send patients
there. If a doctor is sick and went home, the receptionist stops sending
patients there entirely. If the office suddenly gets busy, the receptionist
can even call more doctors in from another building.

**The ALB is that receptionist.** Every HTTP request from the internet
lands at the ALB first. The ALB decides which healthy EC2 should handle it
and forwards the request there.

## Why Do We Need A Receptionist For One Doctor?

KanAuth only runs one EC2 today. Isn't the ALB wasted effort?

**No, because the ALB gives us five things even with one instance:**

1. **A stable DNS name.** `kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com`
   never changes even if the EC2 is replaced and its private IP changes.
2. **Health checks.** The ALB hits `/health` every few seconds. If the
   container crashes, the ALB returns 503 to users within seconds — no
   broken requests while EB spins up a replacement.
3. **TLS termination (optional).** When you add HTTPS, the ALB handles the
   certificate and the expensive TLS handshake, so the app doesn't have to.
4. **Request logging.** Every request is logged by the ALB with timing,
   response code, source IP, etc.
5. **Future scaling.** The day you add a second EC2, there is zero client
   change — the ALB already exists and starts load-balancing.

## What "Layer 7" Means (And Why It Matters)

AWS offers three load balancer types. The ALB is **Layer 7** — it
understands **HTTP**.

| Load Balancer | Layer | Understands | Use Case |
|---|---|---|---|
| **ALB** (Application) | 7 (HTTP/HTTPS) | URLs, headers, cookies, host names | Web apps, APIs, microservices |
| **NLB** (Network) | 4 (TCP/UDP) | IP addresses, ports | Ultra-low-latency, non-HTTP protocols |
| **CLB** (Classic) | 4 and 7 (legacy) | Old-school, being retired | Legacy apps |

Because the ALB understands HTTP, it can do smart routing like:

- "If the URL starts with `/api/`, send it to the backend fleet."
- "If the URL starts with `/admin/`, send it to the admin fleet."
- "If the `Host:` header is `app.example.com`, send it to fleet A; if it is
  `api.example.com`, send it to fleet B."

KanAuth only uses **one rule** right now ("send everything to the single
target group"), but the capability is there when you need it.

## The ALB's Pieces

An ALB is not one thing. It is a small constellation of objects:

```
       Internet
           │
           ▼
   ┌─────────────────┐
   │   Listener      │    ← port 80 (and 443 if HTTPS)
   │   port 80       │
   └────────┬────────┘
            │ rule: default → forward to TG1
            ▼
   ┌─────────────────┐
   │  Target Group   │    ← list of EC2s + health check config
   │      TG1        │
   └────────┬────────┘
            │ registered targets
            ▼
   ┌─────────────────┐
   │   EC2 instance  │
   │   (one or more) │
   └─────────────────┘
```

- **Listener** — "I accept connections on port 80."
- **Rule** — "If the request matches XYZ, forward to target group TG1."
- **Target Group** — a collection of EC2 instances (or IPs, or Lambdas).
  The target group owns the **health check** configuration.
- **Target** — an individual EC2 registered in the group.

### Health Checks In KanAuth

From `.ebextensions/03-healthcheck.config`:

```yaml
option_settings:
  aws:elasticbeanstalk:environment:process:default:
    HealthCheckPath: /health
    MatcherHTTPCode: "200"
  aws:elasticbeanstalk:healthreporting:system:
    SystemType: enhanced
```

This tells the target group:

- **Path:** `GET /health` — a minimal endpoint defined in
  `src/KanAuth.API/Program.cs`. It checks the database connection.
- **Matcher:** HTTP `200` is success; anything else is failure.
- **Reporting:** `enhanced` enables EB's richer health dashboard.

Defaults that you inherit (EB sets these):
- Interval: 15 seconds
- Timeout: 5 seconds
- Healthy threshold: 3 successes in a row
- Unhealthy threshold: 5 failures in a row

> **Gotcha we hit:** `MatcherHTTPCode: 200` without quotes makes EB silently
> skip the entire `process:default` namespace. Always quote it: `"200"`.

## Request Flow Through The ALB

Trace a login request:

1. Browser → `POST https://kanauth-prod.eba-spwm2ak2.ap-south-2.elasticbeanstalk.com/api/v1/auth/login`
2. DNS resolves the hostname → ALB's public IP.
3. TCP handshake → ALB listener accepts on port 80.
4. ALB picks a rule → default rule → target group TG1.
5. ALB picks a **healthy** target from TG1 (round-robin among healthy
   instances). Unhealthy ones are skipped automatically.
6. ALB opens a connection to the EC2's private IP on port 80.
7. The EC2's nginx proxies the request to the Docker container on port 80.
8. Container returns a JSON response. Path reverses.

## Exact Steps: Where ALB Config Lives

You do **not** run `aws elbv2 create-load-balancer` by hand. EB creates the
ALB as part of `eb create`. But you can inspect / tweak it:

### Find the ALB EB created

```bash
aws elbv2 describe-load-balancers \
  --region ap-south-2 \
  --query "LoadBalancers[?starts_with(LoadBalancerName, 'awseb')].[LoadBalancerName,DNSName,State.Code]"
```

You should see the one matching `DEPLOYMENT.md`:
`awseb--AWSEB-jf4L3n4aPEYI-1033739991.ap-south-2.elb.amazonaws.com`.

### Find the target group & its health

```bash
aws elbv2 describe-target-groups \
  --region ap-south-2 \
  --query "TargetGroups[?starts_with(TargetGroupName, 'awseb')].[TargetGroupName,HealthCheckPath,TargetGroupArn]"

aws elbv2 describe-target-health \
  --target-group-arn <ARN-from-previous-command>
```

The second command shows each registered target and whether it is
`healthy`, `unhealthy`, `initial`, or `draining`.

### Tail ALB access logs

ALB access logs are **off** by default. To enable:

```bash
aws elbv2 modify-load-balancer-attributes \
  --load-balancer-arn <alb-arn> \
  --attributes Key=access_logs.s3.enabled,Value=true \
               Key=access_logs.s3.bucket,Value=my-log-bucket \
               Key=access_logs.s3.prefix,Value=kanauth-prod
```

> ALB logs drop into S3 every 5 minutes as compressed text files. You can
> analyze them later with Athena.

## Adding HTTPS (Production Hardening)

KanAuth currently listens on plain HTTP. For real production you should:

1. Request an ACM (AWS Certificate Manager) certificate for your domain.
2. Add a listener on port 443 using that cert.
3. Change the port-80 listener to redirect to port 443.

This is documented in `plan_aws.md` Step 13. The ALB does all the TLS work
— your container still listens on plain HTTP internally, which is fine
because the EC2 lives in the private VPC.

## Alternatives: Other Load Balancers

| Alternative | Pros | Cons | When |
|---|---|---|---|
| **NLB (Network)** | Ultra-low latency, supports static IP, TCP/UDP | No HTTP-aware routing, no host-header rules, no TLS termination niceties | gRPC over TCP, gaming servers, realtime video |
| **CloudFront (CDN)** | Global edge, built-in caching, DDoS protection via Shield | More complex, extra cost, overkill for API-only apps with no static assets | Global reach, static/JS-heavy apps, WAF+Shield+cache in one |
| **API Gateway (REST/HTTP)** | Per-request IAM, request validation, throttling, usage plans, Lambda integration | Per-request billing; quirks around connection reuse | API-only workloads, Lambda backends, needing API keys |
| **Self-hosted nginx / HAProxy / Caddy** | Full control, free, runs anywhere | You own uptime, patching, scaling | Hybrid/on-prem, cost-obsessed setups |
| **Cloudflare / Fastly / Fly.io** | Massive edge network, bundled CDN+WAF+LB | Another vendor, extra DNS config | Want edge caching without AWS lock-in |

### Why ALB For KanAuth?

- It is **free with EB** — we did not have to provision it; EB did.
- It speaks HTTP, so `/health` checks are trivial.
- It integrates with ACM for painless HTTPS when we get there.
- It is cheaper than API Gateway at any non-trivial request volume.

### What About "No Load Balancer At All"?

Technically EB supports a **single-instance environment** where the EC2 has
a public Elastic IP and no ALB. This is cheaper (saves ~$20/month) but:

- You lose health-check-based traffic removal.
- You lose the stable DNS through EB.
- You lose the painless HTTPS path.
- You cannot scale horizontally later without rearchitecting.

For a learning project, single-instance is fine. For something touching
real users (auth tokens!), the ALB is worth the ~$20/month.

## Key Takeaways

- **ALB = smart receptionist in front of EC2s.**
- It does **health checks, routing, TLS termination, and stable DNS.**
- KanAuth's ALB hits `/health`; enhanced health reporting is on.
- Alternatives (NLB, CloudFront, API Gateway) each specialize — ALB is the
  general-purpose HTTP choice.
- You rarely touch it directly; EB manages it for you.

## Next

Read [05-rds-postgres.md](05-rds-postgres.md) to meet the managed database.
