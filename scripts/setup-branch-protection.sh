#!/usr/bin/env bash
# setup-branch-protection.sh — enable branch protection on `main` to match the
# contribution model in CONTRIBUTING.md. Idempotent: the protection API is a full
# replace, so re-running just re-asserts the same rules.
#
# ┌─ PRECONDITIONS (read before running) ───────────────────────────────────────┐
# │ 1. The repo AND its `main` branch must already exist ON THE REMOTE           │
# │    (push `main` first — you can't protect a branch GitHub hasn't seen).      │
# │ 2. The active `gh` account must have ADMIN on the repo.                       │
# │ 3. The `build-test-coverage` check must have run at least once for GitHub    │
# │    to recognise it as a status-check context (open one PR through CI first). │
# │ This script is NOT run automatically — an admin runs it by hand, once.       │
# └─────────────────────────────────────────────────────────────────────────────┘
set -euo pipefail

echo "── Branch protection for 'main' ──────────────────────────────────────────────"
echo "Preconditions: repo + 'main' exist on the remote, and the active gh account has"
echo "admin. Requires the 'build-test-coverage' check to have run once. Ctrl-C to abort."
echo

REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner)"
echo "Applying protection to: $REPO  (branch: main)"

gh api \
  --method PUT \
  -H "Accept: application/vnd.github+json" \
  "/repos/$REPO/branches/main/protection" \
  --input - <<'JSON'
{
  "required_status_checks": {
    "strict": true,
    "contexts": ["build-test-coverage"]
  },
  "enforce_admins": true,
  "required_pull_request_reviews": {
    "dismiss_stale_reviews": true,
    "required_approving_review_count": 1
  },
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false
}
JSON

echo
echo "✅ Protection applied: PR required, 'build-test-coverage' must pass, ≥1 approval,"
echo "   stale approvals dismissed, force-push + deletion blocked, enforced for admins."
