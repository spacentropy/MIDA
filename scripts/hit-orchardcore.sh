#!/usr/bin/env bash
# Why OrchardCore.Tests fails after a hit: a whole mida test in clone c fills the cache, clone d hits everything, and
# this prints what d's test output names of c, then runs d's tests again once c is gone, as on a CI runner.
# Usage, from the repo root: scripts/pool.sh PROJECT REGION '[DECLARE=1] bash scripts/hit-orchardcore.sh' 7200s
set -euo pipefail

mkdir -p TestResults
results=$PWD/TestResults
dotnet pack src/Mida -c Release -p:Version=0.0.0-hit -o out
dotnet tool install -g mida --version 0.0.0-hit --add-source out --framework net11.0

work=$(mktemp -d)
git clone -q --depth 1 --branch v3.0.1 https://github.com/OrchardCMS/OrchardCore "$work/source"
for name in c d; do
  git clone -q "$work/source" "$work/$name"
  git -C "$work/$name" remote set-url origin https://github.com/OrchardCMS/OrchardCore
done

# DECLARE=1 has the web app name what its build writes into its own directory, so mida stores it.
if [ -n "${DECLARE:-}" ]; then
  for name in c d; do
    sed -i 's#</Project>#<PropertyGroup><MidaSourceOutputs>Localization;wwwroot</MidaSourceOutputs></PropertyGroup></Project>#' \
      "$work/$name/src/OrchardCore.Cms.Web/OrchardCore.Cms.Web.csproj"
  done
fi

tests=(--include '*/OrchardCore.Tests.csproj')
summary() { sed 's/\x1b\[[0-9;]*m//g' "$1" | grep -E 'hits, |test projects run|OrchardCore.Tests +Total: |not stored|not used|Graph ' | sed "s#$work/##g" | sort -u || true; }

(cd "$work/c" && mida test OrchardCore.slnx --cache "$work/cache" --key-details "$work/keys-c" "${tests[@]}" > "$results/hit-c.txt" 2>&1) || true
echo "== c"; summary "$results/hit-c.txt"
# The projects MSBuild built since the marker: its incremental clean writes a file list, which a hit does not restore.
built() { find "$work/d" -name '*.FileListAbsolute.txt' -newer "$work/marker" | sed "s#$work/d/##" | sort; }
touch "$work/marker"
(cd "$work/d" && mida test OrchardCore.slnx --cache "$work/cache" --key-details "$work/keys-d" --all "${tests[@]}" > "$results/hit-d.txt" 2>&1) || true
echo "== d, every project a hit"; summary "$results/hit-d.txt"; built
echo "== keys that differ between c and d"
diff -r "$work/keys-c" "$work/keys-d" | cut -c1-300 | head -40 || true
echo "== the web app's static web asset endpoints, built in c and in d, one value a line"
web=src/OrchardCore.Cms.Web/bin/Debug/net10.0/OrchardCore.Cms.Web.staticwebassets.endpoints.json
for name in c d; do tr ',{}[]' '\n' <"$work/$name/$web" | grep -v '^$' > "$work/endpoints-$name.txt"; done
wc -l "$work/endpoints-c.txt" "$work/endpoints-d.txt"
diff "$work/endpoints-c.txt" "$work/endpoints-d.txt" | grep -v 'GMT"$' | cut -c1-250 | head -40 || true

bin=test/OrchardCore.Tests/bin/Debug/net10.0
echo "== MvcTestingAppManifest.json in c, then d"
cat "$work/c/$bin/MvcTestingAppManifest.json"; echo
cat "$work/d/$bin/MvcTestingAppManifest.json"; echo
echo "== files in d's test bin that name c's checkout"
grep -rlF "$work/c" "$work/d/$bin" | sed "s#$work/d/$bin/##" | grep -v '^App_Data/' | sort | tee "$results/hit-names-c.txt" | head -20
echo "$(wc -l < "$results/hit-names-c.txt") files"
echo "== is the manifest among them"
grep -x 'MvcTestingAppManifest.json' "$results/hit-names-c.txt" || echo "no"
echo "== how one assembly names it"
first=$(grep -m1 '\.dll$' "$results/hit-names-c.txt" || true)
[ -n "$first" ] && { grep -aoE "[[:print:]]*$work/c[[:print:]]*" "$work/d/$bin/$first" | head -5 || true; }
echo "== what the build wrote into the source tree of c, and d lacks: untracked files outside bin/ and obj/"
for name in c d; do
  git -C "$work/$name" status --porcelain --untracked-files=all --ignored | grep -vE '/(bin|obj)/' | sort > "$results/hit-tree-$name.txt" || true
done
diff "$results/hit-tree-c.txt" "$results/hit-tree-d.txt" | head -40 || true
echo "== wwwroot of the web app and of the test bin, in c and d"
for name in c d; do
  ls -d "$work/$name/src/OrchardCore.Cms.Web/wwwroot" "$work/$name/$bin/wwwroot" 2>&1 || true
done

rm -rf "$work/c"
touch "$work/marker"
(cd "$work/d" && mida test OrchardCore.slnx --cache "$work/cache" --all "${tests[@]}" > "$results/hit-d-alone.txt" 2>&1) || true
echo "== d again, with c gone"; summary "$results/hit-d-alone.txt"; built
sed 's/\x1b\[[0-9;]*m//g' "$results/hit-d-alone.txt" | grep -A2 '\[FAIL\]$' | head -12 || true
