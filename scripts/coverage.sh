#!/usr/bin/env bash
# Runs every test project with coverage over VideoQuality.Core, .App and .Cli, merges the reports
# (plus any extra Cobertura files, e.g. from a Windows run, so OS-specific branches count) and fails
# unless line AND branch coverage are 100%.
#
#   ./scripts/coverage.sh                         # Linux run, report in artifacts/coverage/
#   ./scripts/coverage.sh extra/windows.xml       # merge another platform's Cobertura report
#   VQ_COVERAGE_MIN=95 ./scripts/coverage.sh      # a lower bar while backfilling tests
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
out=$root/artifacts/coverage
min=${VQ_COVERAGE_MIN:-100}
rm -rf "$out"
mkdir -p "$out"

reports=()
for project in "$root"/VideoQuality.Tests "$root"/VideoQuality.App.Tests; do
    [[ -f $project/$(basename "$project").csproj ]] || continue
    name=$(basename "$project")
    dotnet test "$project" -c Debug \
        /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura \
        "/p:CoverletOutput=$out/$name.cobertura.xml" \
        '/p:Include="[VideoQuality.Core]*,[VideoQuality.App]*,[VideoQuality.Cli]*"' \
        '/p:ExcludeByAttribute="GeneratedCodeAttribute,CompilerGeneratedAttribute"' \
        '/p:ExcludeByFile="**/obj/**"'
    reports+=("$out/$name.cobertura.xml")
done
reports+=("$@")

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
