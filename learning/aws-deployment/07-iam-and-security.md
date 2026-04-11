# 07 — IAM (Identity & Access Management)

## The Story: The Keycard System At A Big Office

Imagine a tall office building. There are many floors, many rooms, and many
people. The **security guard downstairs** does not personally remember what
every employee is allowed to do. Instead:

- Each employee has a **keycard** with their name on it.
- Every **door** has a **reader** that checks "does this card have permission
  to open this door?" against a **policy list**.
- When someone is hired, they get a card. When they leave, the card is
  revoked. When they need access to a new room, a role is assigned to them.

**IAM (Identity & Access Management) is AWS's keycard system.** Every AWS
API call is "someone trying to open a door". IAM decides whether to let
them in.

- **Identities** — who is making the request (users, roles, services).
- **Policies** — the rules that say what an identity can do.
- **Permissions** — the union of all policies that apply.

## The Core IAM Concepts

| Term | What It Is | Pizza Shop Analogy |
|---|---|---|
| **User** | A real human with a username and optionally passwords/keys | An employee |
| **Group** | A bundle of users, all getting the same permissions | A department |
| **Role** | A set of permissions that an **identity** can temporarily assume — no permanent credentials | A shared "manager on duty" vest |
| **Policy** | A JSON document listing allowed/denied actions on resources | The rules pinned on the break room wall |
| **Instance profile** | A wrapper around a role that can be attached to an EC2 instance | A vest the robot cook automatically wears |
| **Trust policy** | Rules about **who is allowed to assume** a role | "Only the Wednesday-shift manager can wear this vest" |

### User vs Role — The Confusing Bit

**User:** long-lived credentials (a password or an access key). You log in
as yourself.

**Role:** no long-lived credentials. An identity (a user, an EC2 instance,
a Lambda function) **assumes** the role and gets temporary credentials for
a few hours. When they are done, the credentials expire automatically.

Roles are strictly better than users for services because:
- No keys to leak.
- No keys to rotate.
- Credentials auto-expire.
- You can see who assumed the role in CloudTrail.

## The KanAuth Roles

From `DEPLOYMENT.md`, two roles matter:

### Role 1: `kanauth-eb-instance-profile` (EC2 instance profile)

This is the role **the EC2 instance wears**. It needs permissions to:

| What It Does | Managed Policy |
|---|---|
| Pull the Docker image from ECR | `AmazonEC2ContainerRegistryReadOnly` |
| Read secrets from SSM Parameter Store | `AmazonSSMReadOnlyAccess` |
| Be a well-behaved EB web tier instance (health reporting, CloudWatch logs, S3 artifacts) | `AWSElasticBeanstalkWebTier` |

#### Trust Policy (Who Can Assume This Role)

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": { "Service": "ec2.amazonaws.com" },
      "Action": "sts:AssumeRole"
    }
  ]
}
```

**Plain English:** "The EC2 service is allowed to assume this role." That
is how the EC2 instance gets the credentials without anyone logging in.

When an EC2 launches with this instance profile, the instance metadata
endpoint (`http://169.254.169.254/latest/meta-data/iam/security-credentials/...`)
starts returning short-lived credentials. The AWS SDK inside the container
(or the `aws` CLI in `.ebextensions`) picks them up automatically.

### Role 2: `aws-elasticbeanstalk-service-role`

This is the role **EB itself wears** when managing your environment. It
needs permissions to create/delete EC2s, ALBs, target groups, security
groups, CloudWatch alarms, etc.

You only create this once per AWS account. The `AWSElasticBeanstalkManagedUpdatesCustomerRolePolicy` and `AWSElasticBeanstalkEnhancedHealth` managed policies cover it.

## How The Instance Profile Works (The Magic Cloud Part)

```
1. EC2 launches with instance profile "kanauth-eb-instance-profile"
2. AWS's hypervisor injects a 15-min credential set into the instance metadata
   at http://169.254.169.254/latest/meta-data/iam/security-credentials/kanauth-eb-instance-profile
3. The AWS SDK/CLI detects it is on EC2, calls the metadata endpoint, gets creds
4. AWS refreshes the creds automatically every ~10 minutes
5. Your code never sees or stores a password/access key
```

**This is why `aws ssm get-parameter` works inside `.ebextensions` without
any `AWS_ACCESS_KEY_ID`.** The CLI walks to the metadata endpoint, grabs
the role's temporary creds, and uses them. You never configured a thing.

## Exact Steps: Creating The Instance Profile

> You only do this once per AWS account. After that, every EB environment
> in the account can reuse it.

### Step 1 — Create the role with a trust policy

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

**What it does:** Creates an empty role that the EC2 service can assume.

### Step 2 — Attach managed policies

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

**What it does:** Gives the role the three permissions the EC2 needs.

**Managed policy?** AWS-maintained, well-known policy documents you can
attach. Saves you writing JSON. Alternatives: inline policies (JSON you
write yourself) and customer-managed policies (reusable JSON you write).

### Step 3 — Create the instance profile

```bash
aws iam create-instance-profile \
  --instance-profile-name kanauth-eb-instance-profile

aws iam add-role-to-instance-profile \
  --instance-profile-name kanauth-eb-instance-profile \
  --role-name kanauth-eb-instance-profile
```

**Why two objects?** An **instance profile** is a separate resource from
a **role**, even though they usually share a name. EC2 can only attach
instance profiles to instances, not roles directly. This is a historical
quirk of IAM.

### Step 4 — Use it when creating the EB environment

```bash
eb create kanauth-prod --instance_profile kanauth-eb-instance-profile ...
```

Now every EC2 launched by EB wears this instance profile automatically.

## Production Hardening: Narrow Down The Policies

`AmazonSSMReadOnlyAccess` lets the instance read **any** SSM parameter in
the account. That is too broad. Replace it with a custom policy scoped to
`/kanauth/prod/*`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ReadKanAuthParameters",
      "Effect": "Allow",
      "Action": ["ssm:GetParameter", "ssm:GetParameters"],
      "Resource": "arn:aws:ssm:ap-south-2:116743944666:parameter/kanauth/prod/*"
    },
    {
      "Sid": "DecryptKanAuthSecrets",
      "Effect": "Allow",
      "Action": "kms:Decrypt",
      "Resource": "arn:aws:kms:ap-south-2:116743944666:key/alias/aws/ssm"
    }
  ]
}
```

**Why it's better:**
- Only `kanauth/prod/*` parameters, not every parameter.
- Only the `Decrypt` action on KMS, not `Encrypt` or admin.
- `Resource` ARNs are scoped to region and account.

This is the principle of **least privilege**: give only what is needed.

## IAM For CI/CD: GitHub Actions OIDC

If you use GitHub Actions to deploy (see `plan_aws.md` Step 14), you should
**not** create a long-lived IAM user with access keys stored in GitHub
Secrets. Instead, use **OIDC federation**:

1. Configure GitHub as an identity provider in IAM.
2. Create a role with a trust policy that only trusts
   `repo:your-org/kanauth:ref:refs/heads/main`.
3. The workflow exchanges a short-lived OIDC token from GitHub for
   temporary AWS creds via `sts:AssumeRoleWithWebIdentity`.

Result: no keys in GitHub Secrets, credentials scoped to a specific branch,
auto-expiring after each workflow run.

## Common Mistakes

| Mistake | Why It's Bad | Fix |
|---|---|---|
| Giving the instance profile `AdministratorAccess` | Any container compromise → full AWS account takeover | Use least-privilege policies |
| Baking IAM access keys into the Docker image | Keys live forever in image layers | Use the instance profile |
| Sharing one IAM user across developers | Audit log says "dev@example.com" for every action | One user per human |
| Not enabling MFA on the root account | Root = full control. A password leak = game over | MFA the root; never use it daily |
| Creating a role per environment with a wildcard trust (`Principal: "*"`) | Anyone in AWS can assume your role | Scope the `Principal` tightly |

## Alternatives: Not Using IAM Roles

You could theoretically hardcode access keys somewhere:

| Alternative | Why It's Awful |
|---|---|
| **Access keys in env vars** | Plaintext leakage, no rotation, no audit by caller |
| **Access keys in a config file in the image** | Persists forever in image layers; any `docker history` command shows them |
| **IAM user per app** | Still long-lived keys; just slightly less bad |

**There is no good reason not to use instance profiles on EC2.** They are
the canonical way.

## Key Takeaways

- **IAM = keycard system.** Every API call is checked against policies.
- **Roles > users for services.** No static keys, auto-expiring creds,
  better audit.
- KanAuth uses `kanauth-eb-instance-profile` attached to the EC2. It gets
  read access to ECR, SSM, and EB managed features.
- **The instance metadata endpoint (`169.254.169.254`)** is the cloud magic
  that delivers temporary creds to the running container.
- **Least privilege:** replace broad managed policies with narrow custom
  ones for production.
- **GitHub Actions should use OIDC** instead of stored access keys.

## Next

Read [08-vpc-subnets-security-groups.md](08-vpc-subnets-security-groups.md)
to understand the private network everything lives in.
