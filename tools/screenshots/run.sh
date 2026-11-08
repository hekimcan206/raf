#!/bin/sh
# Runs the capture inside the pinned Playwright image against a running `docker compose up` stack.
# Usage (repo root): sh tools/screenshots/run.sh
set -eu
# Git Bash on Windows would otherwise rewrite the container paths below.
export MSYS_NO_PATHCONV=1
docker run --rm --add-host=host.docker.internal:host-gateway \
  -e RAF_WEB=http://host.docker.internal:58281 \
  -v "$(pwd)/tools/screenshots:/work" -v "$(pwd)/docs/images:/out" -w /work \
  mcr.microsoft.com/playwright:v1.64.1-noble \
  sh -c "npm install --no-audit --no-fund --silent && node capture.mjs"
