$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
docker compose -f docker-compose.local.yml --env-file .env.local down -v --remove-orphans
docker compose -f docker-compose.local.yml --env-file .env.local up -d --build
