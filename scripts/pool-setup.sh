#!/usr/bin/env bash
# Provisions what scripts/pool.sh runs on, all in one region: the private pool, the account builds run as, a bucket
# for build sources and logs, and the bucket the GCS tests use. Safe to rerun.
# Usage: scripts/pool-setup.sh PROJECT [REGION]
set -euo pipefail

project=${1:?usage: scripts/pool-setup.sh PROJECT [REGION]}
# The cheapest European region for c3 private pools on the pricing page, 2026-11-12.
region=${2:-europe-west4}
build=mida-build@$project.iam.gserviceaccount.com

gcloud services enable cloudbuild.googleapis.com iam.googleapis.com --project "$project"

# 8 processors, the machine mida is tuned for. A 22-processor pool, mida, ran until 2026-11-19: docs/notes/benchmarks.md.
gcloud builds worker-pools describe mida-8 --project "$project" --region "$region" > /dev/null 2>&1 ||
  gcloud builds worker-pools create mida-8 --project "$project" --region "$region" \
    --worker-machine-type c3-highcpu-8 --worker-disk-size 1000

# Builds run as this account, which can reach the two buckets below and nothing else.
gcloud iam service-accounts describe "$build" --project "$project" > /dev/null 2>&1 ||
  gcloud iam service-accounts create mida-build --project "$project" --display-name "MIDA builds"

for bucket in "$project-mida-builds" "$project-mida-test"; do
  gcloud storage buckets describe "gs://$bucket" > /dev/null 2>&1 ||
    gcloud storage buckets create "gs://$bucket" --project "$project" --location "$region" \
      --uniform-bucket-level-access --public-access-prevention
done

# Cloud Build reads the bucket itself before it writes logs there. Not storage.admin, which could change who reads it.
for role in roles/storage.objectAdmin roles/storage.legacyBucketReader; do
  gcloud storage buckets add-iam-policy-binding "gs://$project-mida-builds" \
    --member "serviceAccount:$build" --role "$role" > /dev/null
done

# The tests only create and read, so a build cannot delete what another one wrote.
for role in roles/storage.objectCreator roles/storage.objectViewer; do
  gcloud storage buckets add-iam-policy-binding "gs://$project-mida-test" \
    --member "serviceAccount:$build" --role "$role" > /dev/null
done

# Every test writes under a fresh prefix and never reads it again. No soft delete, so expired objects stop billing.
gcloud storage buckets update "gs://$project-mida-test" --clear-soft-delete \
  --lifecycle-file /dev/stdin <<< '{"rule": [{"action": {"type": "Delete"}, "condition": {"age": 1}}]}'
