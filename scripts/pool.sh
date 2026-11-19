#!/usr/bin/env bash
# Runs one command on the Cloud Build private pool through cloudbuild.yaml, streams its log and copies its TestResults
# back. Fails unless the build succeeded. scripts/pool-setup.sh provisions the pool, and .github/actions/pool runs this.
# Usage: scripts/pool.sh PROJECT [REGION] [COMMAND] [TIMEOUT]
set -euo pipefail
cd "$(dirname "$0")/.."

project=${1:?usage: scripts/pool.sh PROJECT [REGION] [COMMAND] [TIMEOUT]}
region=${2:-europe-west4}
run=${3:-dotnet test --project tests/Mida.Tests -f net11.0}
# A mida test scenario at 1024 projects runs seven builds and the tests after most of them; 900 s was set for builds alone.
timeout=${4:-1800s}
where=(--project "$project" --region "$region")

# The sign-in file of google-github-actions/auth can become mida-ci, so code on the pool must never see it. Only
# .gitignore keeps it out of the upload, and gcloud stops reading .gitignore once a .gcloudignore exists.
upload=$(gcloud meta list-files-for-upload)
if grep -q gha-creds <<< "$upload"; then
  echo "A gha-creds file would be uploaded to the pool." >&2
  exit 1
fi

# ^~~^ makes ~~ the separator between substitutions, so a command may contain commas.
id=$(gcloud builds submit "${where[@]}" --async --format 'value(id)' --timeout "$timeout" \
  --worker-pool "projects/$project/locations/$region/workerPools/mida-8" \
  --service-account "projects/$project/serviceAccounts/mida-build@$project.iam.gserviceaccount.com" \
  --gcs-source-staging-dir "gs://$project-mida-builds/source" --gcs-log-dir "gs://$project-mida-builds/logs" \
  --substitutions "^~~^_RUN=$run" .)

# A cancelled job or Ctrl-C cancels the build, which would otherwise run on until its timeout.
trap 'gcloud builds cancel "$id" "${where[@]}" > /dev/null' INT TERM

# Not gcloud builds log --stream: it asks for the build every second, and 14 jobs at once passed the project's 600
# requests a minute. Here the status is read every 30 s, and the log from the bucket, which has no such quota.
log="gs://$project-mida-builds/logs/log-$id.txt"
chunk=$(mktemp)
offset=0
while true; do
  # Read before the log, so the last pass prints what the build wrote before it ended. A failed read waits and retries.
  status=$(gcloud builds describe "$id" "${where[@]}" --format 'value(status)' 2> /dev/null || echo STATUS_UNKNOWN)
  # Fails until the log exists and when nothing is new.
  if gcloud storage cat --range "$offset-" "$log" > "$chunk" 2> /dev/null; then
    cat "$chunk"
    offset=$((offset + $(wc -c < "$chunk")))
  fi
  case $status in
    PENDING | QUEUED | WORKING | STATUS_UNKNOWN) sleep 30 & wait $! ;;
    *) break ;;
  esac
done

# Absent when the build failed before its tests, and gcloud's error then would only confuse.
gcloud storage cp -r "gs://$project-mida-builds/results/$id/TestResults" . 2> /dev/null || true
echo "Build $status"
[ "$status" = SUCCESS ]
