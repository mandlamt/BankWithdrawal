#!/usr/bin/env bash
# push.sh - Push the local BankWithdrawal repo to GitHub
#
# Prerequisite: Create an EMPTY repo at https://github.com/mandlamt/BankWithdrawal
#   - Go to https://github.com/new
#   - Owner: mandlamt
#   - Name:   BankWithdrawal
#   - Public or Private (your choice)
#   - DO NOT initialize with README, .gitignore, or license (we have them)
#   - Click "Create repository"
#
# Then run this script from the repo root.

set -euo pipefail

REMOTE_URL="https://github.com/mandlamt/BankWithdrawal.git"
BRANCH="main"

echo "Pushing local branch '$BRANCH' to $REMOTE_URL ..."
echo "(You will be prompted for GitHub credentials, or your credential helper will be used.)"
echo

# If a remote named 'origin' is already set, leave it; otherwise add it.
if ! git remote get-url origin >/dev/null 2>&1; then
  git remote add origin "$REMOTE_URL"
fi

# Push the branch and set upstream.
git push -u origin "$BRANCH"

echo
echo "Done. Your repo is now live at: https://github.com/mandlamt/BankWithdrawal"
