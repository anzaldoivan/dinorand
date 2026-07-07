#!/usr/bin/env bash
# Run tests with coverage, emit Cobertura XML + HTML report + plain-text summary.
# Output lands in TestResults/ (raw) and coverage-report/ (rendered).
# Exits with the test runner's exit code so CI can distinguish test failures from
# infrastructure failures, but always attempts to render the report if XML exists.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS_DIR="$REPO_ROOT/TestResults"
REPORT_DIR="$REPO_ROOT/coverage-report"
SETTINGS="$REPO_ROOT/test/DinoRand.FileFormats.Tests/coverage.runsettings"

cd "$REPO_ROOT"

dotnet tool restore

rm -rf "$RESULTS_DIR" "$REPORT_DIR"

# Capture test exit code; don't let a test failure abort the report step.
TESTS_EXIT=0
dotnet test \
  --settings "$SETTINGS" \
  --collect:"XPlat Code Coverage" \
  --results-directory "$RESULTS_DIR" \
  || TESTS_EXIT=$?

dotnet tool run reportgenerator -- \
  "-reports:$RESULTS_DIR/**/coverage.cobertura.xml" \
  "-targetdir:$REPORT_DIR" \
  "-reporttypes:HtmlInline_AzurePipelines;Cobertura;TextSummary"

echo ""
echo "=== Coverage summary ==="
cat "$REPORT_DIR/Summary.txt"
echo ""
echo "HTML report: $REPORT_DIR/index.html"
echo "Cobertura:   $REPORT_DIR/Cobertura.xml"

exit $TESTS_EXIT
