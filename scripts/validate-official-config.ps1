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
Assert-True ($config.ecs.serviceName -eq "oficina-ordens-servico") "ECS service invalido."
Assert-True ($config.ecs.containerName -eq "oficina-ordens-servico") "ECS container invalido."
Assert-True ($config.ecs.migrationContainerName -eq "oficina-ordens-servico-migration") "ECS migration container invalido."
Assert-True ($config.ecs.desiredCount -eq 1) "Desired count deve ser 1."
Assert-True ($config.ecs.launchType -eq "FARGATE") "Launch type deve ser FARGATE."
Assert-True ($config.queues.consumerConcurrency -eq 1) "Consumer concurrency deve ser 1."
Assert-True ($config.queues.maxMessagesPerReceive -eq 1) "Max messages deve ser 1."
Assert-True ($config.secrets.runtimeDatabase -eq "/oficina/ordens/runtime-db") "Secret runtime invalido."
Assert-True ($config.secrets.migrationDatabase -eq "/oficina/ordens/migration-db") "Secret migration invalido."
Assert-True ($config.secrets.runtimeDatabase -ne $config.secrets.migrationDatabase) "Secrets runtime e migration devem ser distintos."
Assert-True ($config.payments.useMock -eq $true) "Pagamento mock deve estar habilitado."
Assert-True ($config.payments.mockBehavior -eq "Approved") "MockBehavior deve ser Approved."
Assert-True ($config.payments.externalApiEnabled -eq $false) "External API deve estar desabilitada."
Assert-True ($config.payments.externalWebhookEnabled -eq $false) "External webhook deve estar desabilitado."
Assert-True ($config.payments.contractStatus -eq "Pending") "Contrato externo deve permanecer Pending."
Assert-True ($null -eq $config.payments.baseUrl) "BaseUrl externa nao deve ser configurada."
Assert-True ($null -eq $config.payments.submitPath) "SubmitPath externo nao deve ser configurado."
Assert-True ($config.payments.webhookPath -eq "/api/webhooks/payments") "Webhook path invalido."
Assert-True ($config.payments.timeoutSeconds -eq 5) "Timeout de pagamentos invalido."
Assert-True ($config.payments.maxRetryAttempts -eq 2) "Retry de pagamentos invalido."
Assert-True ($config.services.cadastroBaseUrlParameter -eq "/oficina/infra/alb/dns-name") "Cadastro deve usar ALB interno publicado pela plataforma."
Assert-True ($config.services.estoqueBaseUrlParameter -eq "/oficina/infra/alb/dns-name") "Estoque deve usar ALB interno publicado pela plataforma."
Assert-True ($config.health.path -eq "/health") "Health path invalido."
Assert-True ($config.health.readinessPath -eq "/ready") "Readiness path invalido."

$paths = @(
    $config.aws.clusterNameParameter,
    $config.aws.ecrRepositoryParameter,
    $config.ecs.targetGroupArnParameter,
    $config.ecs.logGroupNameParameter,
    $config.ecs.taskSecurityGroupParameter,
    $config.ecs.privateSubnet1Parameter,
    $config.ecs.privateSubnet2Parameter,
    $config.secrets.runtimeDatabase,
    $config.secrets.migrationDatabase,
    $config.queues.commandsUrlParameter,
    $config.queues.commandsArnParameter,
    $config.queues.commandsDlqUrlParameter,
    $config.queues.commandsDlqArnParameter,
    $config.queues.eventsUrlParameter,
    $config.queues.eventsArnParameter,
    $config.queues.eventsDlqUrlParameter,
    $config.queues.eventsDlqArnParameter,
    $config.services.cadastroBaseUrlParameter,
    $config.services.estoqueBaseUrlParameter
)
foreach ($path in $paths) {
    Assert-True (-not [string]::IsNullOrWhiteSpace($path) -and $path.StartsWith("/oficina/")) "Parametro fora do prefixo /oficina/: $path"
}

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
