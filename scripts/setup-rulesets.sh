#!/usr/bin/env bash
# setup-rulesets.sh — configure GitHub repository rulesets for governance.
# Supersedes the classic setup-branch-protection.sh (rulesets are the modern mechanism and
# support branch-name patterns the classic API can't). Idempotent: each ruleset is looked up
# by name and deleted before being re-created, so re-running re-asserts the same config.
#
# ┌─ PRECONDITIONS ─────────────────────────────────────────────────────────────┐
# │ 1. Repo + `main` exist on the remote; active `gh` account has ADMIN.         │
# │ 2. The `build-test-coverage` and `scan` checks have each run at least once    │
# │    (push/PR through CI) so GitHub recognises them as status-check contexts.   │
# │ An admin runs this by hand, once (and after changing the rules here).         │
# └─────────────────────────────────────────────────────────────────────────────┘
#
# Governance applied (all rulesets: enforcement=active, repo ADMINs may bypass):
#   main-protection        — main: PR required (1 approval, stale reviews dismissed), required
#                            checks build-test-coverage + scan (strict/up-to-date), no force-push,
#                            no deletion. ⇒ blocks direct pushes to main.
#   release-branch-control — only bypassers (admins) may create `release/v*` branches (⇒ only
#                            admins cut releases), and those branches can't be force-pushed/deleted.
#   release-tag-protection — published `v*` tags can't be deleted or moved (releases are immutable;
#                            creating a new tag is still allowed so release.yml can publish).
#
# Branch NAMING (feature/*, release/v*) is NOT a ruleset here: the metadata-restriction rules
# (branch_name_pattern etc.) are only available to Organization repos, and this is a User repo.
# It's enforced instead by the required CI check `branch-name` (.github/workflows/branch-naming.yml),
# which fails any PR whose head branch isn't `feature/*` or `release/v*` — added to the required
# checks below only AFTER that workflow is merged to main (see the commented context).
set -euo pipefail

REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner)"
echo "── Repository rulesets for $REPO ──────────────────────────────────────────────"

# Repo-admin role (id 5) may bypass every ruleset, "always" (avoids self-lockout on hotfixes).
ADMIN_BYPASS='[{"actor_id":5,"actor_type":"RepositoryRole","bypass_mode":"always"}]'

apply_ruleset() {
  local name="$1" json="$2"
  # Delete an existing ruleset of the same name (full-replace semantics on re-run).
  local existing
  existing="$(gh api "repos/$REPO/rulesets" --jq ".[] | select(.name==\"$name\") | .id" 2>/dev/null || true)"
  for id in $existing; do
    gh api --method DELETE "repos/$REPO/rulesets/$id" >/dev/null && echo "  (replaced existing '$name' #$id)"
  done
  gh api --method POST "repos/$REPO/rulesets" --input - <<<"$json" >/dev/null
  echo "  ✅ $name"
}

apply_ruleset "main-protection" "$(cat <<JSON
{
  "name": "main-protection",
  "target": "branch",
  "enforcement": "active",
  "bypass_actors": $ADMIN_BYPASS,
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "rules": [
    { "type": "pull_request", "parameters": {
        "required_approving_review_count": 1,
        "dismiss_stale_reviews_on_push": true,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false } },
    { "type": "required_status_checks", "parameters": {
        "strict_required_status_checks_policy": true,
        "required_status_checks": [
          { "context": "build-test-coverage" },
          { "context": "scan" } ] } },
    { "type": "non_fast_forward" },
    { "type": "deletion" }
  ]
}
JSON
)"

# NOTE: branch NAMING (feature/*, release/v*) would be a `branch_name_pattern` ruleset, but that
# rule type is Organization-only and this is a User repo — see the header. It's enforced by the
# `branch-name` required CI check (.github/workflows/branch-naming.yml) instead.

apply_ruleset "release-branch-control" "$(cat <<JSON
{
  "name": "release-branch-control",
  "target": "branch",
  "enforcement": "active",
  "bypass_actors": $ADMIN_BYPASS,
  "conditions": { "ref_name": { "include": ["refs/heads/release/**"], "exclude": [] } },
  "rules": [
    { "type": "creation" },
    { "type": "non_fast_forward" },
    { "type": "deletion" }
  ]
}
JSON
)"

apply_ruleset "release-tag-protection" "$(cat <<JSON
{
  "name": "release-tag-protection",
  "target": "tag",
  "enforcement": "active",
  "bypass_actors": $ADMIN_BYPASS,
  "conditions": { "ref_name": { "include": ["refs/tags/v*"], "exclude": [] } },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" }
  ]
}
JSON
)"

echo
echo "Done. Review at: https://github.com/$REPO/settings/rules"
