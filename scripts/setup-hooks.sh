#!/usr/bin/env bash
# One-time: point git at the version-controlled hooks in .githooks/.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
git config core.hooksPath .githooks
chmod +x .githooks/* scripts/*.sh 2>/dev/null || true
echo "Hooks enabled (core.hooksPath=.githooks). Pre-commit copyright guard is active."
