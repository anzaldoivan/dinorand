#!/usr/bin/env bash
# Merge DinoRand's verified governance requirements into existing rulesets.
# Safe default: update only required checks already observed successful on GitHub.
set -euo pipefail

PHASE="pre-merge"
REMOVE_OBSOLETE=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --phase) PHASE="${2:-}"; shift 2 ;;
    --remove-obsolete-release-branch-ruleset) REMOVE_OBSOLETE=true; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
case "$PHASE" in pre-merge|post-merge) ;; *) echo "--phase must be pre-merge or post-merge" >&2; exit 2 ;; esac
if $REMOVE_OBSOLETE && [ "$PHASE" != "post-merge" ]; then
  echo "obsolete release-branch removal requires --phase post-merge" >&2
  exit 2
fi

REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner)"
RULESETS="$(gh api "repos/$REPO/rulesets")"

ruleset_id() {
  jq -er --arg name "$1" '.[] | select(.name == $name) | .id' <<<"$RULESETS"
}

round_trip() {
  local id="$1" filter="$2" current payload
  current="$(gh api "repos/$REPO/rulesets/$id")"
  payload="$(jq -e "$filter | {name,target,enforcement,bypass_actors,conditions,rules}" <<<"$current")"
  gh api --method PUT "repos/$REPO/rulesets/$id" --input - <<<"$payload" >/dev/null
}

# Exact job names observed successful on public runs 29975061415 and 29975239199.
MAIN_ID="$(ruleset_id main-protection)"
round_trip "$MAIN_ID" '
  (.rules[] | select(.type == "required_status_checks") |
    .parameters.required_status_checks) |=
  (. + [
    {"context":"branch-name"},
    {"context":"apworld"},
    {"context":"apworld-ap-integration"}
  ] | unique_by([.context, (.integration_id // 0)]))'
echo "updated main-protection without removing existing rules, bypass actors, or checks"

if [ "$PHASE" = "post-merge" ]; then
  TAG_ID="$(ruleset_id release-tag-protection)"
  round_trip "$TAG_ID" '
    if any(.rules[]; .type == "creation") then .
    else .rules += [{"type":"creation"}] end'
  echo "updated release-tag-protection for administrator-only tag creation"

  if $REMOVE_OBSOLETE; then
    BRANCH_ID="$(ruleset_id release-branch-control)"
    gh api --method DELETE "repos/$REPO/rulesets/$BRANCH_ID" >/dev/null
    echo "removed obsolete release-branch-control ruleset"
  fi
fi
