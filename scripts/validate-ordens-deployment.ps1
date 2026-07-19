param(
    [string]$Namespace = "oficina",
    [string]$DeploymentName = "oficina-ordens-servico",
    [string]$ExpectedImageTag = "",
    [string]$MigrationJobName = ""
)

$ErrorActionPreference = "Stop"

Write-Host "Validador read-only de deploy de Ordens."
Write-Host "Este script nao le secrets e nao altera recursos."

$forbidden = @("apply", "delete", "create", "update", "put-secret-value", "get-secret-value")
foreach ($word in $forbidden) {
    if ($MyInvocation.Line -match $word) {
        throw "Operacao mutavel proibida detectada."
    }
}

kubectl get deployment $DeploymentName -n $Namespace -o json | Out-Null
kubectl get service oficina-ordens-servico -n $Namespace -o json | Out-Null
kubectl get serviceaccount ordens-runtime -n $Namespace -o json | Out-Null
kubectl get secretproviderclass ordens-runtime-db -n $Namespace -o json | Out-Null
kubectl get configmap oficina-ordens-servico-config -n $Namespace -o json | Out-Null

if ($MigrationJobName) {
    kubectl get job $MigrationJobName -n $Namespace -o json | Out-Null
}

$configMap = kubectl get configmap oficina-ordens-servico-config -n $Namespace -o json | ConvertFrom-Json
$expectedPaymentFlags = @{
    'Payments__UseMock' = 'true'
    'Payments__MockBehavior' = 'Approved'
    'Payments__ExternalApiEnabled' = 'false'
    'Payments__ExternalWebhookEnabled' = 'false'
}
foreach ($key in $expectedPaymentFlags.Keys) {
    if ($configMap.data.$key -ne $expectedPaymentFlags[$key]) {
        throw "Flag de pagamento invalida no ConfigMap: $key"
    }
}

if ($ExpectedImageTag) {
    $deployment = kubectl get deployment $DeploymentName -n $Namespace -o json | ConvertFrom-Json
    $image = $deployment.spec.template.spec.containers[0].image
    if ($image -notmatch [regex]::Escape($ExpectedImageTag)) { throw "Imagem esperada nao encontrada." }
    $container = $deployment.spec.template.spec.containers[0]
    if ($null -eq $container.livenessProbe -or $null -eq $container.readinessProbe) {
        throw "Deployment precisa de livenessProbe e readinessProbe."
    }
    if ($null -eq $container.resources.requests -or $null -eq $container.resources.limits) {
        throw "Deployment precisa de requests e limits."
    }
    if ($container.securityContext.allowPrivilegeEscalation -ne $false) {
        throw "Deployment deve bloquear privilege escalation."
    }
    if ($deployment.spec.template.spec.securityContext.runAsNonRoot -ne $true) {
        throw "Pod deve rodar como non-root."
    }
}

Write-Host "Validacao read-only concluida."
