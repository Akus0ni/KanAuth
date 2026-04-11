# KanAuth — AWS Elastic Beanstalk Deployment Plan

This plan deploys KanAuth as a **single-container Docker** application on AWS Elastic Beanstalk backed by **Amazon RDS (PostgreSQL)**. Secrets are stored in **AWS Systems Manager Parameter Store**.

---

## Architecture Overview

```
Internet → ALB (Elastic Beanstalk managed)
              → EC2 instance (Docker container: KanAuth.API)
                   → RDS PostgreSQL (private subnet)
                   → SSM Parameter Store (secrets at startup)
```

---

## Prerequisites

- AWS account with sufficient IAM permissions (or Administrator access for initial setup)
- AWS CLI v2 installed and configured (`aws configure`)
- EB CLI installed (`pip install awsebcli`)
- Docker Desktop installed and running
- .NET 8 SDK installed

---

## Step 1 — Create an ECR Repository

Push your Docker image to Amazon Elastic Container Registry (ECR) so Elastic Beanstalk can pull it.

```bash
# Replace <region> and <account-id> throughout this plan
aws ecr create-repository --repository-name kanauth-api --region <region>

# Authenticate Docker to ECR
aws ecr get-login-password --region <region> | \
  docker login --username AWS --password-stdin <account-id>.dkr.ecr.<region>.amazonaws.com
```

---

## Step 2 — Build and Push the Docker Image

```bash
# Build the image from the project root
docker build -t kanauth-api .

# Tag for ECR
docker tag kanauth-api:latest \
  <account-id>.dkr.ecr.<region>.amazonaws.com/kanauth-api:latest

# Push
docker push <account-id>.dkr.ecr.<region>.amazonaws.com/kanauth-api:latest
```

---

## Step 3 — Provision RDS PostgreSQL

Create a managed PostgreSQL instance in a private subnet.

```bash
# Create a DB subnet group spanning at least two AZs (use your VPC subnet IDs)
aws rds create-db-subnet-group \
  --db-subnet-group-name kanauth-db-subnet \
  --db-subnet-group-description "KanAuth DB subnet group" \
  --subnet-ids subnet-xxxxxxxx subnet-yyyyyyyy

# Create the RDS instance (single-AZ is fine for non-prod; use Multi-AZ for production)
aws rds create-db-instance \
  --db-instance-identifier kanauth-db \
  --db-instance-class db.t3.micro \
  --engine postgres \
  --engine-version "16" \
  --master-username kanauth \
  --master-user-password "<STRONG_PASSWORD>" \
  --db-name kanauth \
  --db-subnet-group-name kanauth-db-subnet \
  --no-publicly-accessible \
  --allocated-storage 20 \
  --storage-type gp3 \
  --backup-retention-period 7
```

> After the instance becomes `available`, note the **endpoint hostname** from the AWS Console or:
> ```bash
> aws rds describe-db-instances --db-instance-identifier kanauth-db \
>   --query "DBInstances[0].Endpoint.Address" --output text
> ```

---

## Step 4 — Store Secrets in AWS Systems Manager Parameter Store

Store sensitive values as `SecureString` parameters so they are never hard-coded.

```bash
# JWT secret (minimum 32 characters)
aws ssm put-parameter \
  --name "/kanauth/prod/Jwt__Secret" \
  --value "<YOUR_STRONG_JWT_SECRET_MIN_32_CHARS>" \
  --type SecureString

# DB connection string
aws ssm put-parameter \
  --name "/kanauth/prod/ConnectionStrings__DefaultConnection" \
  --value "Host=<RDS_ENDPOINT>;Port=5432;Database=kanauth;Username=kanauth;Password=<STRONG_PASSWORD>" \
  --type SecureString
```

---

## Step 5 — Create an IAM Instance Profile

The EC2 instance running inside Elastic Beanstalk needs permissions to pull from ECR and read SSM parameters.

1. In the **IAM Console**, create a new role with the trusted entity **EC2**.
2. Attach these managed policies:
   - `AWSElasticBeanstalkWebTier`
   - `AmazonEC2ContainerRegistryReadOnly`
   - `AmazonSSMReadOnlyAccess`
3. Name the role `kanauth-eb-instance-profile` and create an **Instance Profile** with the same name.

> If using the CLI:
> ```bash
> aws iam create-role --role-name kanauth-eb-ec2-role \
>   --assume-role-policy-document '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"ec2.amazonaws.com"},"Action":"sts:AssumeRole"}]}'
>
> aws iam attach-role-policy --role-name kanauth-eb-ec2-role \
>   --policy-arn arn:aws:iam::aws:policy/AWSElasticBeanstalkWebTier
> aws iam attach-role-policy --role-name kanauth-eb-ec2-role \
>   --policy-arn arn:aws:iam::aws:policy/AmazonEC2ContainerRegistryReadOnly
> aws iam attach-role-policy --role-name kanauth-eb-ec2-role \
>   --policy-arn arn:aws:iam::aws:policy/AmazonSSMReadOnlyAccess
>
> aws iam create-instance-profile --instance-profile-name kanauth-eb-instance-profile
> aws iam add-role-to-instance-profile \
>   --instance-profile-name kanauth-eb-instance-profile \
>   --role-name kanauth-eb-ec2-role
> ```

---

## Step 6 — Create the Elastic Beanstalk `Dockerrun.aws.json`

Create this file in the project root. It tells Elastic Beanstalk which image to run and how to map ports.

```json
{
  "AWSEBDockerrunVersion": "1",
  "Image": {
    "Name": "<account-id>.dkr.ecr.<region>.amazonaws.com/kanauth-api:latest",
    "Update": "true"
  },
  "Ports": [
    {
      "ContainerPort": "80"
    }
  ],
  "Logging": "/app/logs"
}
```

> Save as `Dockerrun.aws.json` in the project root.

---

## Step 7 — Add `.ebextensions` for Environment Configuration

Create the directory `.ebextensions/` in the project root. These files configure the EB environment declaratively.

### `.ebextensions/01-env.config`

This file injects environment variables (non-secret values). Secrets are pulled from SSM at deploy time using the `option_settings` + a startup hook (see next file).

```yaml
option_settings:
  aws:elasticbeanstalk:application:environment:
    ASPNETCORE_ENVIRONMENT: Production
    Database__Provider: postgresql
    Database__AutoMigrate: "true"
    Jwt__Issuer: KanAuth
    Jwt__Audience: KanAuth.Clients
```

### `.ebextensions/02-ssm-secrets.config`

This file uses a container command to fetch secrets from SSM and write them to the EB environment before the application starts.

```yaml
container_commands:
  01_fetch_ssm_secrets:
    command: |
      JWT_SECRET=$(aws ssm get-parameter \
        --name "/kanauth/prod/Jwt__Secret" \
        --with-decryption \
        --query Parameter.Value \
        --output text \
        --region $(curl -s http://169.254.169.254/latest/meta-data/placement/region))

      DB_CONN=$(aws ssm get-parameter \
        --name "/kanauth/prod/ConnectionStrings__DefaultConnection" \
        --with-decryption \
        --query Parameter.Value \
        --output text \
        --region $(curl -s http://169.254.169.254/latest/meta-data/placement/region))

      # Write to EB environment file
      cat >> /opt/elasticbeanstalk/deployment/env << EOF
      Jwt__Secret=$JWT_SECRET
      ConnectionStrings__DefaultConnection=$DB_CONN
      EOF
```

---

## Step 8 — Configure the Health Check Endpoint

Elastic Beanstalk's ALB health check must point to `/health`.

Create `.ebextensions/03-healthcheck.config`:

```yaml
option_settings:
  aws:elasticbeanstalk:environment:process:default:
    HealthCheckPath: /health
    MatcherHTTPCode: 200
  aws:elasticbeanstalk:healthreporting:system:
    SystemType: enhanced
```

---

## Step 9 — Initialize the Elastic Beanstalk Application

From the project root:

```bash
eb init kanauth --platform "Docker" --region <region>
```

When prompted:
- **Application name**: `kanauth`
- **Platform**: `Docker`
- **Platform branch**: `Docker running on 64bit Amazon Linux 2023`
- **SSH**: optionally add a key pair for EC2 access

This creates `.elasticbeanstalk/config.yml`.

---

## Step 10 — Create the Environment and Deploy

```bash
eb create kanauth-prod \
  --instance-types t3.small \
  --elb-type application \
  --instance_profile kanauth-eb-instance-profile \
  --envvars ASPNETCORE_ENVIRONMENT=Production
```

Monitor the creation:

```bash
eb events -f
```

Once the environment is `Green`, deploy updates with:

```bash
eb deploy kanauth-prod
```

---

## Step 11 — Configure the RDS Security Group

The RDS instance must allow inbound PostgreSQL traffic (port 5432) **only** from the EB EC2 security group.

1. In the **VPC Console → Security Groups**, find the security group automatically created for the EB environment (named like `awseb-…`).
2. Go to the RDS instance's security group and add an **inbound rule**:
   - Type: `PostgreSQL`
   - Port: `5432`
   - Source: the EB EC2 security group ID

> This keeps the database unreachable from the public internet.

---

## Step 12 — Verify the Deployment

```bash
# Open the environment URL in the browser
eb open

# Check logs if anything is wrong
eb logs

# Confirm the health endpoint responds
curl https://<eb-environment-url>/health

# Confirm Swagger is reachable (disable in production if desired)
curl https://<eb-environment-url>/swagger
```

---

## Step 13 — (Optional) Configure a Custom Domain and HTTPS

1. In **Route 53**, create a hosted zone for your domain.
2. In **ACM (AWS Certificate Manager)**, request a public certificate for your domain and validate via DNS.
3. In the EB console → **Configuration → Load Balancer**, add an HTTPS listener on port 443 using the ACM certificate.
4. Add a CNAME or Alias record in Route 53 pointing to the EB environment URL.
5. In `.ebextensions/01-env.config`, update `Jwt__Issuer` and `Jwt__Audience` to reflect the custom domain.

---

## Step 14 — (Optional) Set Up CI/CD with GitHub Actions

Create `.github/workflows/deploy.yml`:

```yaml
name: Deploy to Elastic Beanstalk

on:
  push:
    branches: [main]

env:
  AWS_REGION: <region>
  ECR_REPOSITORY: kanauth-api
  EB_APP_NAME: kanauth
  EB_ENV_NAME: kanauth-prod

jobs:
  deploy:
    runs-on: ubuntu-latest
    permissions:
      id-token: write
      contents: read

    steps:
      - uses: actions/checkout@v4

      - name: Configure AWS credentials (OIDC)
        uses: aws-actions/configure-aws-credentials@v4
        with:
          role-to-assume: arn:aws:iam::<account-id>:role/github-actions-kanauth
          aws-region: ${{ env.AWS_REGION }}

      - name: Login to ECR
        id: login-ecr
        uses: aws-actions/amazon-ecr-login@v2

      - name: Build, tag, and push image
        run: |
          IMAGE_URI=${{ steps.login-ecr.outputs.registry }}/$ECR_REPOSITORY:${{ github.sha }}
          docker build -t $IMAGE_URI .
          docker push $IMAGE_URI
          # Update Dockerrun.aws.json with the new image tag
          sed -i "s|:latest|:${{ github.sha }}|g" Dockerrun.aws.json

      - name: Deploy to Elastic Beanstalk
        uses: einaregilsson/beanstalk-deploy@v22
        with:
          aws_access_key: ${{ env.AWS_ACCESS_KEY_ID }}
          aws_secret_key: ${{ env.AWS_SECRET_ACCESS_KEY }}
          application_name: ${{ env.EB_APP_NAME }}
          environment_name: ${{ env.EB_ENV_NAME }}
          region: ${{ env.AWS_REGION }}
          deployment_package: Dockerrun.aws.json
```

> Use **OIDC** (the `role-to-assume` approach) instead of long-lived access keys. Create an IAM role for GitHub Actions with a trust policy scoped to your repository.

---

## Step 15 — Production Hardening Checklist

| Item | Action |
|---|---|
| Disable Swagger in production | Set `ASPNETCORE_ENVIRONMENT=Production`; Swagger is already env-gated in `Program.cs` |
| Rotate JWT secret | Update the SSM parameter and redeploy |
| Enable RDS automated backups | `--backup-retention-period 7` (already set in Step 3) |
| Enable RDS encryption at rest | Add `--storage-encrypted` to the `create-db-instance` command |
| Enable HTTPS only | Add HTTP → HTTPS redirect rule on the ALB listener |
| Set up CloudWatch alarms | Alert on 5xx error rate, health check failures, CPU > 80% |
| Enable AWS WAF | Attach a WAF WebACL to the ALB for rate limiting and OWASP rule group |
| Tag all resources | `--tags Key=Project,Value=kanauth Key=Env,Value=prod` |
| Use Multi-AZ RDS for production | Add `--multi-az` to `create-db-instance` |

---

## Cost Estimate (ap/us regions, ~2026 pricing)

| Resource | Tier | Approx. monthly cost |
|---|---|---|
| EB EC2 (t3.small, 1 instance) | On-demand | ~$15 |
| RDS PostgreSQL (db.t3.micro, 20 GB gp3) | Single-AZ | ~$15 |
| ALB | Per hour + LCU | ~$20 |
| ECR storage | Per GB | < $1 |
| SSM Parameter Store (SecureString) | Per 10,000 API calls | < $1 |
| **Total** | | **~$50–55/month** |

Switch to **Reserved Instances** (1-year term) for EC2 and RDS to reduce costs by ~40%.
