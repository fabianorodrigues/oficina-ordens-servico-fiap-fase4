param(
    [string]$ConfigPath = "config/official.json"
)

$ErrorActionPreference = "Stop"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$raw = Get-Content -LiteralPath $ConfigPath -Raw
$config = $raw | ConvertFrom-Json

Assert-True ($config.application.name -eq "oficina-ordens-servico") "Aplicacao oficial invalida."
Assert-True ($config.application.environment -eq "Production") "Ambiente oficial deve ser Production."
Assert-True ($config.kubernetes.namespace -eq "oficina") "Namespace invalido."
Assert-True ($config.kubernetes.deploymentName -eq "oficina-ordens-servico") "Deployment invalido."
Assert-True ($config.kubernetes.serviceName -eq "oficina-ordens-servico") "Service invalido."
Assert-True ($config.kubernetes.replicas -eq 1) "Replicas deve ser 1."
Assert-True ($config.kubernetes.deploymentStrategy -eq "Recreate") "Strategy deve ser Recreate."
Assert-True ($config.queues.consumerConcurrency -eq 1) "Consumer concurrency deve ser 1."
Assert-True ($config.queues.maxMessagesPerReceive -eq 1) "Max messages deve ser 1."
Assert-True ($config.secrets.runtimeDatabase -eq "/oficina/ordens/runtime-db") "Secret runtime invalido."
Assert-True ($config.secrets.migrationDatabase -eq "/oficina/ordens/migration-db") "Secret migration invalido."
Assert-True ($config.secrets.runtimeDatabase -ne $config.secrets.migrationDatabase) "Secrets runtime e migration devem ser distintos."
Assert-True ($config.payments.useMock -eq $true) "Pagamento mock deve estar habilitado."
Assert-True ($config.payments.provider -eq "Mock") "Provider deve ser Mock."
Assert-True ($config.services.cadastroBaseUrl -match '^http://oficina-cadastro') "Cadastro deve usar DNS interno."
Assert-True ($config.services.estoqueBaseUrl -match '^http://oficina-estoque') "Estoque deve usar DNS interno."
Assert-True ($config.health.path -eq "/health") "Health path invalido."
Assert-True ($config.health.readinessPath -eq "/ready") "Readiness path invalido."

$forbiddenPatterns = @(
    'Password\s*=',
    'ConnectionString\s*=',
    'SecretString',
    'AccessToken',
    'ClientSecret',
    'WebhookSecret',
    '\b\d{12}\b',
    'amazonaws\.com/.+\.fifo',
    'dkr\.ecr\.',
    'Fase3',
    'fase-3',
    '/dev/',
    '-dev',
    '-hml',
    '-prod'
)

foreach ($pattern in $forbiddenPatterns) {
    Assert-True (-not ($raw -match $pattern)) "Config contem padrao proibido: $pattern"
}

Write-Host "official.json valido."
