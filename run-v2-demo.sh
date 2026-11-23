#!/usr/bin/env bash
# Временный запуск демонстрационного режима VibroAnalyzer v2.
# Скрипт собирает актуальную версию и открывает главное окно,
# осциллограф и спектр на демо-тракте.
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

DOTNET="${DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$DOTNET" ]]; then
    echo "Не найден dotnet. Установите .NET SDK 10." >&2
    exit 1
fi

SESSION_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/VibroAnalyzer/v2-demo"

"$DOTNET" build src/VibroAnalyzer.App/VibroAnalyzer.App.csproj -v q --nologo
exec "$DOTNET" src/VibroAnalyzer.App/bin/Debug/net10.0/VibroAnalyzer.App.dll \
    --session-dir "$SESSION_DIR" \
    --demo --start --open=scope,spectrum "$@"
