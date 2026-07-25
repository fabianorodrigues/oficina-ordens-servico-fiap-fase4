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
Assert-True ($null -eq $config.PSObject.Properties['ecs']) "Bloco ecs removido: use kubernetes."
Assert-True ($config.kubernetes.deploymentName -eq "oficina-ordens-servico") "Deployment invalido."
Assert-True ($config.kubernetes.serviceName -eq "oficina-ordens-servico") "Service invalido."
Assert-True ($config.kubernetes.containerName -eq "oficina-ordens-servico") "Container invalido."
Assert-True ($config.kubernetes.migrationJobPrefix -eq "oficina-ordens-servico-migration") "Prefixo do Migration Job invalido."
Assert-True ($config.kubernetes.replicas -eq 1) "Replicas deve ser 1."
Assert-True ($config.kubernetes.nodePort -ge 30000 -and $config.kubernetes.nodePort -le 32767) "NodePort fora da faixa 30000-32767."
foreach ($manifestKey in @('configMap', 'deployment', 'service', 'migrationJob', 'secretApp', 'secretMigration')) {
    $manifestPath = $config.kubernetes.manifests.$manifestKey
    Assert-True ((-not [string]::IsNullOrWhiteSpace($manifestPath)) -and (Test-Path -LiteralPath $manifestPath -PathType Leaf)) "Manifesto ausente: $manifestKey"
}
# Um Secret unico servindo Deployment e Job daria ao runtime a credencial de
# migration; os dois templates precisam ser arquivos distintos.
Assert-True ($config.kubernetes.manifests.secretApp -ne $config.kubernetes.manifests.secretMigration) "secretApp e secretMigration devem ser manifests distintos."
Assert-True ($config.deploy.s3Prefix -eq "k8s-deploy/ordens/") "deploy.s3Prefix invalido."
Assert-True ($config.deploy.presignedUrlTtlSeconds -gt 0 -and $config.deploy.presignedUrlTtlSeconds -le 300) "TTL da URL pre-assinada deve ficar entre 1 e 300 segundos."
Assert-True ($config.coverage.minimumLinePercentage -ge 80) "Cobertura minima deve ser ao menos 80."
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
    $config.aws.namespaceParameter,
    $config.aws.instanceIdParameter,
    $config.aws.ecrRepositoryParameter,
    $config.kubernetes.targetGroupArnParameter,
    $config.kubernetes.nodePortParameter,
    $config.deploy.parameterPathPrefix,
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
    '/dev/',
    '-dev',
    '-hml',
    '-prod'
)

foreach ($pattern in $forbiddenPatterns) {
    Assert-True (-not ($raw -match $pattern)) "Config contem padrao proibido: $pattern"
}

Write-Host "official.json valido."
