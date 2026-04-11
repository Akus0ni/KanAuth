# 10 — CloudFormation & S3 (The Behind-The-Scenes)

## The Story: The Contractor's Master Blueprint

Imagine you ask a general contractor to build you a house. You say "I want
3 bedrooms, 2 bathrooms, a garage". The contractor doesn't hand-build
every wall. Instead, they draw up a **master blueprint** that lists every
board, every nail, every pipe, every electrical outlet. The construction
crew follows the blueprint. If you later say "add a deck," the contractor
revises the blueprint and the crew adds exactly the deck — nothing more,
nothing less.

When you later say "tear it all down," the contractor reads the blueprint
backwards and removes every board, nail, and pipe **that was part of this
blueprint** — nothing else in the neighborhood is touched.

**CloudFormation is that master blueprint for AWS.** And the crew is AWS
itself. You describe a set of resources as a single "stack," CF creates
them, manages their drift, and lets you delete them all with one command.

## The Two Roles CloudFormation Plays In KanAuth

### Role 1: The Stack EB Creates For You

When you ran `eb create kanauth-prod`, EB did not directly call
`ec2:RunInstances` and `elbv2:CreateLoadBalancer`. Instead, it generated a
**CloudFormation template** describing every resource — EC2, ALB, target
group, security groups, IAM role references, CloudWatch alarms, and so on
— and submitted it to CloudFormation.

The stack is named:

```
awseb-e-yxfecmr94u-stack
```

(Format: `awseb-e-<env-id>-stack`.)

**Why this matters:**

- `eb terminate kanauth-prod` is really `aws cloudformation delete-stack`.
  **Every resource in the blueprint is removed**, including the EC2, ALB,
  target group, and SGs. This is safe and atomic.
- `eb status` is partly reading CloudFormation stack events.
- `eb events -f` is mostly tailing CloudFormation stack events.
- If the stack gets stuck in a bad state (`UPDATE_ROLLBACK_FAILED`),
  you have to fix it through the CloudFormation console — not through `eb`.

### Role 2: You Could Write Your Own Stack If You Wanted

You could throw away EB and write a CloudFormation template yourself that
declares the same EC2/ALB/RDS/security groups. Tools like the
**AWS CDK**, **Terraform**, or **Pulumi** generate CloudFormation (or the
underlying API calls) from higher-level code.

KanAuth does not do this because EB already does the hard part and the
team wants to focus on the app, not the infra language.

## Seeing The Stack

```bash
aws cloudformation describe-stacks \
  --stack-name awseb-e-yxfecmr94u-stack \
  --region ap-south-2
```

Output: creation time, current status, parameters, outputs, tags.

```bash
aws cloudformation describe-stack-resources \
  --stack-name awseb-e-yxfecmr94u-stack \
  --region ap-south-2
```

Output: **every resource in the blueprint**. You'll see things like:

- `AWS::AutoScaling::AutoScalingGroup` — the ASG EB created
- `AWS::EC2::SecurityGroup` — the EB EC2 SG
- `AWS::ElasticLoadBalancingV2::LoadBalancer` — the ALB
- `AWS::ElasticLoadBalancingV2::TargetGroup` — the target group
- `AWS::ElasticLoadBalancingV2::Listener` — the port-80 listener
- `AWS::ElasticLoadBalancingV2::ListenerRule` — the default rule
- `AWS::CloudWatch::Alarm` — health alarms
- `AWS::AutoScaling::LaunchConfiguration` or `LaunchTemplate`

This list is why `eb create` is so powerful — it describes ~25 resources
that would otherwise take a week of AWS console clicking to replicate.

## Drift Detection

"Drift" = when someone changed a resource **outside** CloudFormation (e.g.,
manually tweaked a security group rule in the console). CF can detect this:

```bash
aws cloudformation detect-stack-drift \
  --stack-name awseb-e-yxfecmr94u-stack \
  --region ap-south-2
```

Useful to catch "somebody added an inbound rule in the console that our
Infrastructure-as-Code does not know about." For KanAuth, the one rule we
**do** add manually (the RDS → EB SG rule) is counted as drift if CF is
the owner. In our case CF does not own the RDS SG, so no drift is reported.

## S3: The EB Artifact Bucket

During `eb deploy`:

```
your laptop ─(zip)─► EB S3 bucket ─(pull)─► EC2 instance
```

The bucket EB uses is:

```
elasticbeanstalk-ap-south-2-116743944666
```

Format: `elasticbeanstalk-<region>-<account-id>`. Auto-created on the
first `eb` command in a region. Every `eb deploy` uploads the zip there
under a timestamped key.

### What's In It

- Your deployment zips (`Dockerrun.aws.json` + `.ebextensions/`).
- EB platform internal config files.
- Rolling-deploy state files.

### You Usually Don't Touch It

```bash
# List what's there (mostly for curiosity)
aws s3 ls s3://elasticbeanstalk-ap-south-2-116743944666/ --recursive
```

Never delete the bucket. EB re-uses it across environments.

## S3 In General (Beyond EB)

S3 is AWS's **object storage** — think "infinitely big folder in the
cloud". Objects are up to 5 TB, the service is 11-nines durable (tiny
chance of loss per year), and it is cheap (~$0.023 per GB-month).

**KanAuth uses S3 only indirectly**, for the deployment zip. But in
production apps, S3 is often used for:

- Uploaded user files (profile pictures, documents)
- Daily DB backups (written from a CRON job)
- Static frontend assets behind CloudFront
- Data lake / analytics storage
- Cross-region replication for DR

## Exact Steps: Stack Inspection And Lifecycle

### See stack events during a deploy

```bash
aws cloudformation describe-stack-events \
  --stack-name awseb-e-yxfecmr94u-stack \
  --region ap-south-2 \
  --max-items 20
```

Or simply `eb events -f`, which is a wrapper around this.

### See the stack template as JSON

```bash
aws cloudformation get-template \
  --stack-name awseb-e-yxfecmr94u-stack \
  --region ap-south-2 \
  --query TemplateBody > eb-template.json
```

Useful for studying exactly what EB created. You will learn a lot from
reading this.

### Delete the stack (destroys everything)

```bash
eb terminate kanauth-prod
```

This is equivalent to:

```bash
aws cloudformation delete-stack \
  --stack-name awseb-e-yxfecmr94u-stack \
  --region ap-south-2
```

> **Warning:** This deletes the EC2, ALB, SGs, target groups, alarms, and
> the auto-scaling group. It does **not** delete RDS (RDS is not in the
> stack), ECR, SSM parameters, or IAM roles.

## Alternatives: Other Infrastructure-As-Code Tools

| Alternative | What It Is | Pros | Cons |
|---|---|---|---|
| **Terraform** | HashiCorp's IaC DSL (HCL) | Cloud-agnostic, huge ecosystem, clear plan/apply flow | Another tool to learn; state file management can bite you |
| **AWS CDK** | Write CloudFormation in TypeScript / Python / C# / Java | Real programming language, reusable constructs, type safety | Compiles to CloudFormation so you inherit CF's quirks |
| **Pulumi** | Like CDK but cloud-agnostic | Polyglot, cloud-agnostic, TypeScript-friendly | Requires a Pulumi account for state (or self-host) |
| **Serverless Framework** | YAML DSL for Lambda apps | Amazing for serverless | Less flexible for non-Lambda resources |
| **SAM (Serverless Application Model)** | AWS's YAML extension of CF for serverless | First-class CF, good for Lambda | Limited to serverless shapes |
| **Ansible / Chef / Puppet** | Config management, not IaC | Great for in-VM config | Less ideal for cloud resources |
| **Click-Ops (the console)** | Click buttons in the AWS Console | Zero learning curve | No history, no reproducibility, no review |

### When To Use Which

- **Small project, just starting out:** EB (no IaC file to write).
- **Medium project, want reproducibility:** Terraform or CDK.
- **Huge multi-team org:** Terraform with a shared module library.
- **Serverless-heavy:** SAM or Serverless Framework.
- **Multi-cloud:** Pulumi or Terraform.

KanAuth does not need Terraform today. If the project grew to include
multiple environments (dev, staging, prod), a CloudFront distribution, a
Cognito user pool, and a scheduled Lambda for cleanup — then Terraform
or CDK would start to pay for itself.

## Key Takeaways

- **CloudFormation is the blueprint** that lists every AWS resource in a
  stack. EB generates one for you.
- The stack is named `awseb-e-<env-id>-stack` and contains ~25 resources.
- **`eb terminate` deletes the stack**, not RDS / ECR / SSM / IAM.
- **S3 is the artifact store** EB uses to upload deployment zips.
- **Alternatives to CF include Terraform, CDK, Pulumi** — each with
  trade-offs for language, multi-cloud, and ecosystem.

## Next

Read [11-full-deploy-walkthrough.md](11-full-deploy-walkthrough.md) to
stitch every step from zero to deployed into one runbook.
