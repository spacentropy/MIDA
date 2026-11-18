#!/usr/bin/env bash
# Lets this repo's GitHub Actions workflows submit builds to the pool without a key: Workload Identity Federation trades
# a workflow's OIDC token for the mida-ci account. Then stores what the workflows need as repo secrets, so the project
# never appears in the repo or in its public logs. Run after scripts/pool-setup.sh. Safe to rerun, and the first run may
# need it: a new account takes a minute to reach project IAM, which until then says it "does not exist".
# Usage: scripts/pool-github.sh PROJECT
set -euo pipefail

project=${1:?usage: scripts/pool-github.sh PROJECT}
number=$(gcloud projects describe "$project" --format 'value(projectNumber)')
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner)
# The numeric id, which a repo deleted and recreated under the same name does not keep.
repo_id=$(gh api "repos/$repo" --jq .id)
ci=mida-ci@$project.iam.gserviceaccount.com
build=mida-build@$project.iam.gserviceaccount.com
# The binding below trusts every provider in this pool, so the pool holds MIDA's provider and nothing else. A generic
# name like github could be a pool someone else made, with a looser provider that would skip the main-only condition.
pool_id=mida-github
pool=projects/$number/locations/global/workloadIdentityPools/$pool_id

gcloud services enable iamcredentials.googleapis.com sts.googleapis.com --project "$project"

gcloud iam workload-identity-pools describe "$pool_id" --project "$project" --location global > /dev/null 2>&1 ||
  gcloud iam workload-identity-pools create "$pool_id" --project "$project" --location global --display-name "MIDA GitHub"
# Only main's workflows: a pushed branch or a dispatch from one runs code nobody merged. And only these two events:
# pull_request_target, workflow_run and issue_comment also carry main's ref, while a pull request sets them off.
provider=(github --project "$project" --location global --workload-identity-pool "$pool_id"
  --attribute-mapping 'google.subject=assertion.sub,attribute.repository_id=assertion.repository_id'
  --attribute-condition "assertion.repository_id == '$repo_id' && assertion.ref == 'refs/heads/main' && (assertion.event_name == 'push' || assertion.event_name == 'workflow_dispatch')")
if gcloud iam workload-identity-pools providers describe github --project "$project" --location global \
  --workload-identity-pool "$pool_id" > /dev/null 2>&1; then
  gcloud iam workload-identity-pools providers update-oidc "${provider[@]}"
else
  gcloud iam workload-identity-pools providers create-oidc "${provider[@]}" --issuer-uri https://token.actions.githubusercontent.com
fi

gcloud iam service-accounts describe "$ci" --project "$project" > /dev/null 2>&1 ||
  gcloud iam service-accounts create mida-ci --project "$project" --display-name "MIDA GitHub Actions"
gcloud iam service-accounts add-iam-policy-binding "$ci" --project "$project" --role roles/iam.workloadIdentityUser \
  --member "principalSet://iam.googleapis.com/$pool/attribute.repository_id/$repo_id" > /dev/null

# mida-ci submits builds on the pool that run as mida-build, and stages sources, reads logs and fetches results in the
# builds bucket. Nothing else.
for role in roles/cloudbuild.builds.editor roles/cloudbuild.workerPoolUser roles/serviceusage.serviceUsageConsumer; do
  gcloud projects add-iam-policy-binding "$project" --member "serviceAccount:$ci" --role "$role" --condition None > /dev/null
done
gcloud iam service-accounts add-iam-policy-binding "$build" --project "$project" \
  --member "serviceAccount:$ci" --role roles/iam.serviceAccountUser > /dev/null
for role in roles/storage.objectAdmin roles/storage.legacyBucketReader; do
  gcloud storage buckets add-iam-policy-binding "gs://$project-mida-builds" \
    --member "serviceAccount:$ci" --role "$role" > /dev/null
done

gh secret set GCP_PROJECT --repo "$repo" --body "$project"
gh secret set GCP_WORKLOAD_IDENTITY_PROVIDER --repo "$repo" --body "$pool/providers/github"
