#!/usr/bin/env bash
# Git Bash / WSL: run the same publish as publish.ps1 via PowerShell.
set -euo pipefail
cd "$(dirname "$0")"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "./publish.ps1" "$@"
