param(
    [string]$ConfigPath = "config/official.json",
    [string]$ManifestDirectory = "k8s"
)

$ErrorActionPreference = "Stop"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-ConfigMapValue {
    param(
        [Parameter(Mandatory = $true)][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Key
    )

    foreach ($line in $Lines) {
        if ($line -match "^\s+$([regex]::Escape($Key))\s*:\s*(.*)$") {
            return $Matches[1].Trim().Trim('"')
        }
    }

    return $null
}

$dockerfilePath = "Dockerfile"
Assert-True (Test-Path -LiteralPath $dockerfilePath -PathType Leaf) "Dockerfile ausente."
$dockerfileRaw = Get-Content -LiteralPath $dockerfilePath -Raw
Assert-True ($dockerfileRaw -match '(?m)^COPY\s+Directory\.Packages\.props\s+\./\s*$') "Dockerfile deve copiar Directory.Packages.props antes do dotnet restore."

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

# ---------------------------------------------------------------------------
# Contrato de observabilidade dos ConfigMaps.
#
# 1. OTEL_EXPORTER_OTLP_ENDPOINT aponta para o gateway real do chart. O exporter
#    deve ser fail-open quando o Collector ainda nao estiver disponivel.
# 2. service.version nao pode ter duas origens: fica somente em
#    OTEL_SERVICE_VERSION.
# 3. Nenhuma credencial da New Relic pode entrar no Pod.
#    OTEL_EXPORTER_OTLP_HEADERS entra na lista porque e por ele que a license key
#    chegaria ao exporter da aplicacao.
# ---------------------------------------------------------------------------

$telemetryFound = $false
$expectedOtlpEndpoint = 'http://nr-k8s-otel-collector-gateway.newrelic.svc.cluster.local:4317'

if (Test-Path -LiteralPath $ManifestDirectory) {
    foreach ($manifest in Get-ChildItem -LiteralPath $ManifestDirectory -Filter '*.yaml' -File) {
        $lines = Get-Content -LiteralPath $manifest.FullName
        $name = $manifest.Name

        foreach ($key in @(
                'NEW_RELIC_LICENSE_KEY',
                'NEW_RELIC_USER_API_KEY',
                'NEW_RELIC_API_KEY',
                'OTEL_EXPORTER_OTLP_HEADERS',
                'OTEL_EXPORTER_OTLP_PROTOCOL',
                'OTEL_SERVICE_NAME',
                'OTEL_METRIC_EXPORT_INTERVAL')) {
            Assert-True (-not ($lines | Select-String -Pattern "^\s+$([regex]::Escape($key))\s*:" -Quiet)) `
                "$name declara $key. O ConfigMap deve manter OTLP fail-open e sem credenciais."
        }
        foreach ($pattern in @('NRAK-[A-Za-z0-9]{10,}', 'NRAA-[A-Za-z0-9]{10,}')) {
            Assert-True (-not ($lines | Select-String -Pattern $pattern -Quiet)) `
                "$name contem valor com formato de chave da New Relic ($pattern)."
        }

        $endpoint = Get-ConfigMapValue -Lines $lines -Key 'OTEL_EXPORTER_OTLP_ENDPOINT'
        if ($null -eq $endpoint) { continue }

        $telemetryFound = $true
        Assert-True ($endpoint -eq $expectedOtlpEndpoint) "$name aponta OTLP para '$endpoint'; esperado '$expectedOtlpEndpoint'."
        Assert-True (-not [string]::IsNullOrWhiteSpace((Get-ConfigMapValue -Lines $lines -Key 'OTEL_SERVICE_VERSION'))) "$name nao define OTEL_SERVICE_VERSION."

        $attributes = Get-ConfigMapValue -Lines $lines -Key 'OTEL_RESOURCE_ATTRIBUTES'
        if ($null -ne $attributes) {
            Assert-True ($attributes -notmatch 'service\.version\s*=') "$name repete service.version em OTEL_RESOURCE_ATTRIBUTES. A unica origem e OTEL_SERVICE_VERSION."
            foreach ($required in @('deployment.environment', 'service.namespace', 'k8s.cluster.name')) {
                Assert-True ($attributes -match "$([regex]::Escape($required))\s*=") "$name nao declara $required em OTEL_RESOURCE_ATTRIBUTES."
            }
        }
    }
}

Assert-True $telemetryFound "ConfigMap da API deve declarar OTEL_EXPORTER_OTLP_ENDPOINT."

Write-Host "official.json e contrato de observabilidade validos."
