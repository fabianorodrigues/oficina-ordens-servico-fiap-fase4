param(
    [string]$Solution = "Oficina.OrdensServico.sln"
)

$ErrorActionPreference = "Stop"

$env:Payments__UseMock = "true"
$env:Payments__MockBehavior = "Approved"
$env:Payments__ExternalApiEnabled = "false"
$env:Payments__ExternalWebhookEnabled = "false"
$env:Payments__ContractStatus = "Pending"

dotnet test $Solution -c Release --no-build --filter "FullyQualifiedName~PagamentoMockTests|FullyQualifiedName~PaymentIntegrationTests"

Write-Host "Teste local do pagamento mock concluido sem chamada externa."
