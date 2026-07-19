param(
    [string]$ConfigPath = "config/official.json",
    [string]$OutputDirectory = "artifacts/k8s",
    [Parameter(Mandatory = $true)][string]$RuntimeImage,
    [Parameter(Mandatory = $true)][string]$MigrationImage,
    [Parameter(Mandatory = $true)][string]$AwsRegion,
    [Parameter(Mandatory = $true)][string]$CommandsQueueUrl,
    [Parameter(Mandatory = $true)][string]$CommandsDlqUrl,
    [Parameter(Mandatory = $true)][string]$EventsQueueUrl,
    [Parameter(Mandatory = $true)][string]$EventsDlqUrl,
    [ValidateSet("PodIdentity","IRSA")][string]$WorkloadIdentityMode = "PodIdentity",
    [string]$RuntimeIrsaRoleArn = "",
    [string]$MigrationIrsaRoleArn = "",
    [string]$MigrationJobName = "oficina-ordens-servico-migration-local"
)

$ErrorActionPreference = "Stop"

& "$PSScriptRoot/validate-official-config.ps1" -ConfigPath $ConfigPath

function Assert-Tag([string]$Image, [string]$Name) {
    if ($Image -notmatch ':[0-9a-fA-F]{7,40}(-migration)?$') { throw "$Name deve usar tag de commit SHA." }
    if ($Image -match ':(latest|dev|hml|staging|prod)$') { throw "$Name usa tag proibida." }
}

Assert-Tag $RuntimeImage "RuntimeImage"
Assert-Tag $MigrationImage "MigrationImage"
if ($MigrationImage -notmatch '-migration$') { throw "MigrationImage deve terminar com -migration." }
if ($MigrationJobName -notmatch '^oficina-ordens-servico-migration-[a-zA-Z0-9-]+$') { throw "MigrationJobName invalido." }
if ($CommandsQueueUrl -notmatch '^https?://') { throw "CommandsQueueUrl invalida." }
if ($CommandsDlqUrl -notmatch '^https?://') { throw "CommandsDlqUrl invalida." }
if ($EventsQueueUrl -notmatch '^https?://') { throw "EventsQueueUrl invalida." }
if ($EventsDlqUrl -notmatch '^https?://') { throw "EventsDlqUrl invalida." }
if ($WorkloadIdentityMode -eq "IRSA" -and ([string]::IsNullOrWhiteSpace($RuntimeIrsaRoleArn) -or [string]::IsNullOrWhiteSpace($MigrationIrsaRoleArn))) {
    throw "IRSA exige roles runtime e migration."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$templates = Get-ChildItem -LiteralPath "deploy/k8s" -File -Filter "*.yaml"

foreach ($template in $templates) {
    $content = Get-Content -LiteralPath $template.FullName -Raw
    $content = $content.Replace("__RUNTIME_IMAGE__", $RuntimeImage)
    $content = $content.Replace("__MIGRATION_IMAGE__", $MigrationImage)
    $content = $content.Replace("__AWS_REGION__", $AwsRegion)
    $content = $content.Replace("__COMMANDS_QUEUE_URL__", $CommandsQueueUrl)
    $content = $content.Replace("__COMMANDS_DLQ_URL__", $CommandsDlqUrl)
    $content = $content.Replace("__EVENTS_QUEUE_URL__", $EventsQueueUrl)
    $content = $content.Replace("__EVENTS_DLQ_URL__", $EventsDlqUrl)
    $content = $content.Replace("__CADASTRO_BASE_URL__", $config.services.cadastroBaseUrl)
    $content = $content.Replace("__ESTOQUE_BASE_URL__", $config.services.estoqueBaseUrl)
    $content = $content.Replace("__MIGRATION_JOB_NAME__", $MigrationJobName)

    if ($WorkloadIdentityMode -eq "IRSA") {
        if ($template.Name -eq "service-account-runtime.template.yaml") {
            $content = $content.Replace("annotations: {}", "annotations:`n    eks.amazonaws.com/role-arn: `"$RuntimeIrsaRoleArn`"")
        }
        if ($template.Name -eq "service-account-migration.template.yaml") {
            $content = $content.Replace("annotations: {}", "annotations:`n    eks.amazonaws.com/role-arn: `"$MigrationIrsaRoleArn`"")
        }
    }

    $pendingPlaceholders = @(
        "__RUNTIME_IMAGE__",
        "__MIGRATION_IMAGE__",
        "__AWS_REGION__",
        "__COMMANDS_QUEUE_URL__",
        "__COMMANDS_DLQ_URL__",
        "__EVENTS_QUEUE_URL__",
        "__EVENTS_DLQ_URL__",
        "__CADASTRO_BASE_URL__",
        "__ESTOQUE_BASE_URL__",
        "__MIGRATION_JOB_NAME__"
    )
    foreach ($placeholder in $pendingPlaceholders) {
        if ($content.Contains($placeholder)) { throw "Placeholder pendente em $($template.Name): $placeholder" }
    }
    if ($content -match 'ConnectionStrings__DefaultConnection:') { throw "Manifest nao deve conter connection string." }
    if ($template.Name -eq "configmap.template.yaml") {
        foreach ($flag in @(
            'Payments__UseMock: "true"',
            'Payments__MockBehavior: "Approved"',
            'Payments__ExternalApiEnabled: "false"',
            'Payments__ExternalWebhookEnabled: "false"'
        )) {
            if ($content -notmatch [regex]::Escape($flag)) { throw "Flag de pagamento ausente: $flag" }
        }
    }

    $name = $template.Name.Replace(".template", "")
    Set-Content -LiteralPath (Join-Path $OutputDirectory $name) -Value $content -Encoding utf8
}

Write-Host "Manifests renderizados em $OutputDirectory."
