#!/usr/bin/env bash
# Runs one command on the Cloud Build private pool through cloudbuild.yaml. scripts/pool-setup.sh provisions the pool.
# Usage: scripts/pool.sh PROJECT [REGION] [COMMAND]
set -euo pipefail
cd "$(dirname "$0")/.."

project=${1:?usage: scripts/pool.sh PROJECT [REGION] [COMMAND]}
region=${2:-europe-west4}
run=${3:-dotnet test --project tests/Mida.Tests -f net11.0}

# ^~~^ makes ~~ the separator between substitutions, so a command may contain commas.
time gcloud builds submit --project "$project" --region "$region" \
  --worker-pool "projects/$project/locations/$region/workerPools/mida" \
  --service-account "projects/$project/serviceAccounts/mida-build@$project.iam.gserviceaccount.com" \
  --gcs-source-staging-dir "gs://$project-mida-builds/source" --gcs-log-dir "gs://$project-mida-builds/logs" \
  --substitutions "^~~^_RUN=$run" .
