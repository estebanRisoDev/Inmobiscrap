#!/bin/bash
set -e

echo "🔍 Checking Playwright browsers..."

PLAYWRIGHT_CACHE="/root/.cache/ms-playwright"
if [ ! -d "$PLAYWRIGHT_CACHE" ] || [ -z "$(ls -A $PLAYWRIGHT_CACHE 2>/dev/null)" ]; then
    echo "⚠️  Playwright browsers not found. Installing..."
    dotnet build "Inmobiscrap.csproj" -c Debug --no-restore 2>/dev/null || true
    pwsh /app/bin/Debug/net9.0/playwright.ps1 install chromium --with-deps
    echo "✅ Playwright browsers installed"
else
    echo "✅ Playwright browsers already present at $PLAYWRIGHT_CACHE"
    ls "$PLAYWRIGHT_CACHE"
fi

echo "🚀 Starting application..."
exec dotnet run --urls "http://0.0.0.0:8080"