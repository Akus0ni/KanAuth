# 01 — Docker & Elastic Container Registry (ECR)

## The Story: The Lunchbox

Imagine you made a peanut-butter sandwich at home and you want your friend
across town to eat **the exact same sandwich** — not a similar one, the exact
same one, with the same bread, the same peanut butter, the same amount of jelly.

You put it in a **sealed lunchbox** so nothing falls out or changes on the
way. That lunchbox is a **Docker image**.

Now you need a place to leave the lunchbox so your friend can pick it up. A
shared **warehouse with shelves and labels**. That warehouse is **ECR**.

- **Docker image** = sealed lunchbox with the app + every file it needs.
- **Docker container** = when your friend opens the lunchbox and eats it
  (i.e., the running copy of the image).
- **ECR (Elastic Container Registry)** = AWS's warehouse to store lunchboxes.

## Why Do We Use Docker At All?

Before Docker, deploying software was painful: *"It works on my machine!"*
You would install .NET 8 on your laptop, write code, and then try to run it
on a server that had .NET 7 — boom, crash. Docker fixes this by packaging
**the app AND its whole environment** (the OS libraries, the runtime, the
config files) into one file.

If the image runs on your laptop, it will run **identically** on AWS,
because the image is the same bits in both places.

### The Dockerfile (Already In This Repo)

Look at `Dockerfile` at the project root. It says roughly:

1. Start from the official `mcr.microsoft.com/dotnet/sdk:8.0` image.
2. Copy `.csproj` files and run `dotnet restore` (downloads NuGet packages).
3. Copy the rest of the source and run `dotnet publish`.
4. Copy the published DLLs into a smaller runtime image (`aspnet:8.0`).
5. Expose port 80 and run `dotnet KanAuth.API.dll`.

When you run `docker build`, Docker follows those steps and produces a
single image that contains **everything KanAuth needs to run** — you could
take that image to Mars and it would still work.

## Why Do We Use ECR (Not Docker Hub)?

Elastic Beanstalk needs to **pull the image** when it starts a new EC2
instance. It could pull from anywhere, but using ECR has three big wins:

| Reason | Why It Matters |
|---|---|
| **Private by default** | Nobody on the internet can peek at your compiled .NET app. |
| **IAM-based auth** | The EC2 instance proves who it is with an IAM role instead of a username/password. |
| **Same AWS region = fast pulls** | ECR in `ap-south-2` → EC2 in `ap-south-2` = no internet hop, no data transfer charges. |

KanAuth stores its image at:

```
116743944666.dkr.ecr.ap-south-2.amazonaws.com/kanauth-api:latest
```

Read that like an address:
- `116743944666` = the AWS account ID (your account).
- `dkr.ecr.ap-south-2.amazonaws.com` = the ECR service in the Mumbai 2 region.
- `kanauth-api` = the repository name (the shelf in the warehouse).
- `latest` = the tag (which version of the lunchbox on that shelf).

## Exact Steps: Building And Pushing The Image

### Step 1 — Create the ECR repository (one time only)

```bash
aws ecr create-repository \
  --repository-name kanauth-api \
  --region ap-south-2
```

**What it does:** Creates an empty "shelf" named `kanauth-api` in the
warehouse. You only do this once per project.

**Why:** You cannot push an image until the shelf exists.

### Step 2 — Build the image for the right CPU

```bash
docker build --platform linux/amd64 -t kanauth-api .
```

**What it does:** Reads `Dockerfile` and produces an image tagged
`kanauth-api:latest` on your local machine.

**Why `--platform linux/amd64`?** AWS EC2 `t3.small` runs on Intel/AMD chips
(`x86_64` = `amd64`). If you are on a Mac with Apple Silicon (M1/M2/M3), your
Mac is `arm64`. Without `--platform`, Docker would build an arm64 image, and
it would **crash on startup** on AWS with a scary libc error. Always force
`linux/amd64` for deployments to EB.

### Step 3 — Log in to ECR

```bash
aws ecr get-login-password --region ap-south-2 | \
  docker login --username AWS --password-stdin \
  116743944666.dkr.ecr.ap-south-2.amazonaws.com
```

**What it does:** Asks AWS for a 12-hour access token, and hands it to
Docker so Docker knows the password for your ECR warehouse.

**Why:** ECR is private. Docker will not let you push to a warehouse it
cannot log in to.

### Step 4 — Tag the local image with the ECR address

```bash
docker tag kanauth-api:latest \
  116743944666.dkr.ecr.ap-south-2.amazonaws.com/kanauth-api:latest
```

**What it does:** Adds a second name to the same image (like adding a
shipping label to a box). The bits are not copied — only a pointer is added.

**Why:** `docker push` needs to know **where** to push. A naked name like
`kanauth-api:latest` does not have an address.

### Step 5 — Push

```bash
docker push 116743944666.dkr.ecr.ap-south-2.amazonaws.com/kanauth-api:latest
```

**What it does:** Uploads the image layers to ECR. Layers that already exist
in ECR are skipped, so subsequent pushes are fast.

**Why:** The EC2 instances on AWS cannot read files from your laptop. They
read from ECR.

### Step 6 — Verify

```bash
aws ecr describe-images \
  --repository-name kanauth-api \
  --region ap-south-2
```

You should see one image with tag `latest` and an image digest (a long
`sha256:...` string). That digest is the image's unique fingerprint.

## Alternatives: Other Places To Store Container Images

| Alternative | Pros | Cons | When To Pick It |
|---|---|---|---|
| **Docker Hub** | Free public tier, famous, huge ecosystem | Private repos cost money; rate-limited pulls can make production flaky | Open-source projects where the image should be public |
| **GitHub Container Registry (ghcr.io)** | Free, integrated with GitHub Actions, private allowed | Not AWS-native — EC2 still needs a token to pull | If your CI already runs in GitHub Actions and you want fewer AWS moving parts |
| **GitLab Container Registry** | Bundled with GitLab CI | Same AWS-integration gap as GHCR | GitLab-centric teams |
| **Self-hosted registry** (`registry:2` image) | Full control, free | You have to run and secure it yourself | Air-gapped environments |
| **Quay.io (Red Hat)** | Vulnerability scanning included | Paid for private repos | Enterprise security requirements |
| **Azure Container Registry / Google Artifact Registry** | Same benefits as ECR, but on their cloud | You are on AWS, so it would mean cross-cloud pulls | Multi-cloud setups |

### Why KanAuth Specifically Chose ECR

1. **We are already on AWS** → zero-latency, zero-cost image pulls from ECR
   to EC2 in the same region.
2. **IAM auth "just works"** → the EC2 instance profile grants
   `AmazonEC2ContainerRegistryReadOnly` and no passwords are needed.
3. **Cost is negligible** → storage is $0.10 per GB per month; the KanAuth
   image is ~200 MB so this is less than $0.03 per month.

## Alternatives: Packaging Without Docker At All

Docker is not the only way to ship an app. For completeness:

| Alternative | Pros | Cons |
|---|---|---|
| **Plain zip** (EB's default platform) | No Docker knowledge needed | Tied to EB's pre-baked .NET version; harder to reproduce locally |
| **Virtual machine images (AMIs)** | Includes the OS too — ultra-reproducible | Slow to build, huge files, hard to version |
| **Serverless (AWS Lambda)** | No servers to manage at all | Cold starts; 15-minute request cap; EF Core migrations get tricky |
| **Nix / Buildpacks** | Reproducible without Dockerfiles | Newer ecosystem, smaller community |

Docker is the standard because it hits the sweet spot: small images, fast
deploys, runs the same everywhere.

## Key Takeaways

- **A Docker image is a sealed, runnable copy of your app + its environment.**
- **ECR is the private warehouse where AWS services pull images from.**
- **Always build for `linux/amd64`** when deploying to EB on `t3.small`.
- The image address has 4 parts: account, region, repo name, tag.
- The EC2 instance uses its IAM role to authenticate — no passwords stored anywhere.

## Next

Read [02-elastic-beanstalk.md](02-elastic-beanstalk.md) to learn about the
auto-pilot that runs the image on a real computer.
