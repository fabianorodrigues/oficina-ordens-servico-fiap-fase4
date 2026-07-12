param([string]$Service)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if ([string]::IsNullOrWhiteSpace($Service)) {
    docker compose -f docker-compose.local.yml --env-file .env.local logs -f
} else {
    docker compose -f docker-compose.local.yml --env-file .env.local logs -f $Service
}
