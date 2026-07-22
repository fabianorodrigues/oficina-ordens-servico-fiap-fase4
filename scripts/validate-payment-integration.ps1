param(
    [string]$ConfigPath = "config/official.json"
)

$ErrorActionPreference = "Stop"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$raw = Get-Content -LiteralPath $ConfigPath -Raw
$config = $raw | ConvertFrom-Json

Assert-True ($config.payments.useMock -eq $true) "Payments.useMock deve ser true."
Assert-True ($config.payments.mockBehavior -eq "Approved") "Payments.mockBehavior deve ser Approved."
Assert-True ($config.payments.externalApiEnabled -eq $false) "Payments.externalApiEnabled deve ser false."
Assert-True ($config.payments.externalWebhookEnabled -eq $false) "Payments.externalWebhookEnabled deve ser false."
Assert-True ($config.payments.contractStatus -eq "Pending") "Payments.contractStatus deve ser Pending."
Assert-True ($config.payments.webhookPath -eq "/api/webhooks/payments") "Webhook path invalido."
Assert-True ($config.payments.timeoutSeconds -ge 1 -and $config.payments.timeoutSeconds -le 30) "Timeout invalido."
Assert-True ($config.payments.maxRetryAttempts -ge 0 -and $config.payments.maxRetryAttempts -le 3) "Retries devem ser limitados."
Assert-True ($null -eq $config.payments.baseUrl) "Base URL real nao deve estar configurada."
Assert-True ($null -eq $config.payments.submitPath) "Submit path externo nao deve estar configurado."

$forbiddenPatterns = @(
    'MERCADO_PAGO_ACCESS_TOKEN',
    'MERCADO_PAGO_CLIENT_ID',
    'MERCADO_PAGO_CLIENT_SECRET',
    'MercadoPago.Client',
    'MercadoPago.Config',
    'aws_dynamodb_table',
    'oficina-payment-audit',
    'terraform/payments',
    'payments-infra-deploy',
    'payment-api-config-sync',
    'PAYMENTS_API_CREDENTIAL',
    '/oficina/payments/credential',
    'AccessToken=',
    'ClientSecret=',
    'WebhookSecret=',
    ('terraform ' + 'destroy')
)

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$scriptPath = (Resolve-Path -LiteralPath $PSCommandPath).Path
$excludedDirectoryNames = @('bin', 'obj', 'TestResults', '.git')
$trimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$files = Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Force | Where-Object {
    if ($_.FullName -eq $scriptPath) {
        $false
    }
    else {
        $relativePath = $_.FullName.Substring($repoRoot.Length).TrimStart($trimChars)
        $pathParts = $relativePath -split '[\\/]'
        -not ($pathParts | Where-Object { $excludedDirectoryNames -contains $_ })
    }
}

foreach ($pattern in $forbiddenPatterns) {
    $matches = @($files | Select-String -Pattern $pattern)
    if ($matches.Count -gt 0) {
        $formattedMatches = ($matches | ForEach-Object { "$($_.Path):$($_.LineNumber):$($_.Line.Trim())" }) -join "`n"
        throw "Padrao proibido encontrado: $pattern`n$formattedMatches"
    }
}

Write-Host "Integracao de pagamentos validada: mock aprovado, externo desabilitado, contrato pendente."
