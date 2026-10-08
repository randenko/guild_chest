#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
case "${1:-help}" in
  setup) python3 scripts/setup.py "${@:2}" ;;
  build) dotnet build GuildChest.sln --configuration Debug "${@:2}" ;;
  test)
    dotnet test tests/GuildChest.Tests/GuildChest.Tests.csproj --configuration Debug "${@:2}"
    python3 -m unittest discover -s tests -p 'test_*.py'
    ;;
  package)
    dotnet build src/GuildChest/GuildChest.csproj --configuration Release "${@:2}"
    python3 scripts/package.py
    ;;
  *) echo 'Usage: bash scripts/dev.sh {setup [--update]|build|test|package}'; exit 2 ;;
esac
