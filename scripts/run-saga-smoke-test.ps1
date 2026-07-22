param(
    [string]$BaseUrl = "http://localhost:5103"
)

$ErrorActionPreference = "Stop"
Write-Host "Saga smoke local usa endpoints reais quando o ambiente Docker Compose esta ativo."
& "$PSScriptRoot/smoke-test.ps1" -BaseUrl $BaseUrl
Write-Host "Cenarios distribuidos exigem Cadastro, Estoque, SQL Server e LocalStack locais ativos."
