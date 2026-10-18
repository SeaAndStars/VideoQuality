#!/usr/bin/env bash
# Runs every test project with coverage over VideoQuality.Core, .App and .Cli, merges the runs
# (plus any extra coverlet JSON files, e.g. from a Windows run, so OS-specific branches count) and
# fails unless line AND branch coverage are 100%.
#
#   ./scripts/coverage.sh                         # Linux run, report in artifacts/coverage/
#   ./scripts/coverage.sh extra/windows.json      # merge another platform's coverlet JSON report
#   VQ_COVERAGE_MIN=95 ./scripts/coverage.sh      # a lower bar while backfilling tests
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
out=$root/artifacts/coverage
min=${VQ_COVERAGE_MIN:-100}
rm -rf "$out"
mkdir -p "$out"

# Coverlet's JSON keeps each branch's hits, so runs merge exactly (Cobertura only says "1 of 2
# branches", which cannot be combined). Extra JSON reports (another platform's run, paths rewritten
# with scripts/coverage-paths.py) are merged first.
merged=$out/merged.json
echo '{}' > "$merged"
for extra in "$@"; do
    python3 "$root/scripts/coverage-paths.py" "$extra" "$root" "$merged"
done

projects=()
for project in "$root"/VideoQuality.Tests "$root"/VideoQuality.App.Tests; do
    [[ -f $project/$(basename "$project").csproj ]] && projects+=("$project")
done

for i in "${!projects[@]}"; do
    project=${projects[$i]}
    formats=json
    [[ $i -eq $((${#projects[@]} - 1)) ]] && formats='json%2ccobertura'
    dotnet test "$project" -c Debug \
        /p:CollectCoverage=true "/p:CoverletOutputFormat=$formats" \
        "/p:CoverletOutput=$out/" "/p:MergeWith=$merged" \
        '/p:Include="[VideoQuality.Core]*,[VideoQuality.App]*,[VideoQuality.Cli]*"' \
        '/p:ExcludeByAttribute="GeneratedCodeAttribute,CompilerGeneratedAttribute"' \
        '/p:ExcludeByFile="**/obj/**"'
    mv "$out/coverage.json" "$merged"
done
reports=("$out/coverage.cobertura.xml")

dotnet tool run reportgenerator -verbosity:Warning \
    "-reports:$(IFS=';'; echo "${reports[*]}")" \
    "-targetdir:$out/report" "-reporttypes:Html;JsonSummary;TextSummary;Cobertura"
sed -n '1,25p' "$out/report/Summary.txt"

python3 - "$out/report/Summary.json" "$min" <<'PY'
import json, sys
summary = json.load(open(sys.argv[1]))
minimum = float(sys.argv[2])
s = summary["summary"]
line, branch = s.get("linecoverage") or 0, s.get("branchcoverage") or 0
short = []
for assembly in summary["coverage"]["assemblies"]:
    for cls in assembly["classesinassembly"]:
        lc = cls.get("coverage") or 0
        bc = cls.get("branchcoverage")
        if lc < 100 or (bc is not None and bc < 100):
            short.append((lc, bc if bc is not None else 100, cls["name"]))
for lc, bc, name in sorted(short):
    print(f"  {lc:6.1f}% line  {bc:6.1f}% branch  {name}")
print(f"total: {line}% line, {branch}% branch (required {minimum}%)")
sys.exit(0 if line >= minimum and branch >= minimum else 1)
PY
