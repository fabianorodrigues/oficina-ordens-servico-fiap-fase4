param([switch]$Force)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env.local"
$example = Join-Path $root ".env.local.example"

if ((Test-Path $envFile) -and -not $Force) {
    Write-Host ".env.local ja existe. Use -Force para recriar."
    exit 0
}

Copy-Item -LiteralPath $example -Destination $envFile -Force
Write-Host ".env.local criado a partir de .env.local.example. Ajuste as senhas antes de usar fora da maquina local."
