$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

New-Item -ItemType Directory -Force -Path (Join-Path $root "artifacts/newman") | Out-Null

docker compose -f docker-compose.local.yml --env-file .env.local --profile validation run --rm newman
