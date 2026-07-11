param(
    [string]$Namespace = "oficina",
    [string]$DeploymentName = "oficina-ordens-servico",
    [string]$ExpectedImageTag = ""
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

if ($ExpectedImageTag) {
    $deployment = kubectl get deployment $DeploymentName -n $Namespace -o json | ConvertFrom-Json
    $image = $deployment.spec.template.spec.containers[0].image
    if ($image -notmatch [regex]::Escape($ExpectedImageTag)) { throw "Imagem esperada nao encontrada." }
}

Write-Host "Validacao read-only concluida."
