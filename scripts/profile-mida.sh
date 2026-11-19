#!/usr/bin/env bash
# Stands in for mida as MIDA_BENCH_TOOL: runs the installed mida and leaves, per run, under $MIDA_PROFILE_DIR/<n>/, its
# arguments, MIDA_TIMINGS phases, a dotnet-trace of the mida process, its runtime counters, and the bytes the machine
# dirtied and wrote meanwhile. For finding where the time goes, never for times to report: tracing slows the run.
# Read a trace with dotnet-trace report <file> topN, or convert it for speedscope. See docs/notes/benchmarks.md
set -uo pipefail
dir="${MIDA_PROFILE_DIR:?}/$(date +%s%N)"
mkdir -p "$dir"
printf '%s\n' "$PWD" "$@" > "$dir/args.txt"

# Pages dirtied and written by the whole machine, which is this run and nothing else.
vm() { awk '$1 == "nr_dirtied" || $1 == "nr_written" { printf "%s %d MB\n", $1, $2 * 4096 / 1048576 }' /proc/vmstat; }
vm > "$dir/vm-before.txt"
start=$(date +%s.%N)

export MIDA_TIMINGS=1
# MIDA_PROFILE_ENV: variables for mida and every process it starts, such as DOTNET_TieredPGO=0. Split on spaces.
if [ -n "${MIDA_PROFILE_ENV:-}" ]; then
  # shellcheck disable=SC2163,SC2086
  export ${MIDA_PROFILE_ENV}
fi
# MIDA_EXTRA_ARGS: arguments added to every run, such as a -p to try. Split on spaces.
# shellcheck disable=SC2086
mida "$@" ${MIDA_EXTRA_ARGS:-} 2> >(tee "$dir/timings.txt" >&2) &
pid=$!

# Attaching fails until the runtime's diagnostic server listens, so retry briefly. Their output stays out of mida's.
attach() {
  for _ in $(seq 100); do
    "$@" > /dev/null 2>&1 && return
    kill -0 "$pid" 2> /dev/null || return
    sleep 0.05
  done
}
attach dotnet-trace collect -p "$pid" -o "$dir/mida.nettrace" &
tracer=$!
attach dotnet-counters collect -p "$pid" --format csv -o "$dir/counters.csv" --refresh-interval 1 \
  --counters System.Runtime &
counters=$!

wait "$pid"
code=$?
awk -v s="$start" -v e="$(date +%s.%N)" 'BEGIN { printf "wall %.1f s\n", e - s }' > "$dir/vm-after.txt"
vm >> "$dir/vm-after.txt"
# Once mida is gone the tracer only finishes its file, and the counters have nothing left to read. One run waited
# ten minutes on them, so neither gets more than 30 s.
for _ in $(seq 60); do
  kill -0 "$tracer" 2> /dev/null || break
  sleep 0.5
done
# The tools run under the retry loops' subshells, so end the children first.
pkill -P "$tracer,$counters" 2> /dev/null
kill "$tracer" "$counters" 2> /dev/null
wait
exit "$code"
