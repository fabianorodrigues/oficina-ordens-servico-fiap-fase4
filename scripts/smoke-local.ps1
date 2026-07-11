$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $root ".env.local"
Get-Content $envPath | Where-Object { $_ -and -not $_.StartsWith("#") } | ForEach-Object {
    $parts = $_.Split("=", 2)
    if ($parts.Length -eq 2) { Set-Item -Path "env:$($parts[0])" -Value $parts[1] }
}

$cadastro = "http://127.0.0.1:$env:CADASTRO_HTTP_PORT"
$estoque = "http://127.0.0.1:$env:ESTOQUE_HTTP_PORT"
$ordens = "http://127.0.0.1:$env:ORDENS_HTTP_PORT"
$correlationId = [guid]::NewGuid().ToString()
$runId = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString()
$suffix = $runId.Substring($runId.Length - 7)
$documento = ("1" + $suffix + "001").Substring(0, 11)
$placa = ("SM" + $suffix).Substring(0, 7).ToUpperInvariant()
$renavam = ("2" + $suffix + "001").Substring(0, 11)
$email = "cliente.smoke+$suffix@example.com"
$headers = @{
    "X-Dev-Role" = "Funcionario"
    "X-Dev-Cpf" = "12345678901"
    "X-Dev-FuncionarioId" = "11111111-1111-1111-1111-111111111111"
    "X-Correlation-Id" = $correlationId
}

function PostJson($url, $body) {
    Invoke-RestMethod -Method Post -Uri $url -Headers $headers -ContentType "application/json" -Body ($body | ConvertTo-Json -Depth 20)
}

function GetJson($url) {
    Invoke-RestMethod -Method Get -Uri $url -Headers $headers
}

function WaitStatus($ordemId, [string] $expected) {
    for ($i = 0; $i -lt 45; $i++) {
        $status = GetJson "$ordens/api/ordens-servico/$ordemId/status"
        if ($status.status -eq $expected) {
            Write-Host "Status OK: $expected"
            return
        }
        Start-Sleep -Seconds 2
    }
    throw "Status esperado nao atingido: $expected"
}

function GetAvailableQuantity($materialId) {
    $response = PostJson "$estoque/api/internal/estoque/disponibilidade" @{
        items = @(@{
            tipoMaterial = 1
            materialId = $materialId
            requestedQuantity = 1
        })
    }
    [int]$response.items[0].availableQuantity
}

function WaitQuantity($materialId, [int] $expected) {
    for ($i = 0; $i -lt 45; $i++) {
        $current = GetAvailableQuantity $materialId
        if ($current -eq $expected) {
            Write-Host "Saldo OK: $current"
            return
        }
        Start-Sleep -Seconds 2
    }
    throw "Saldo esperado nao atingido: $expected"
}

Write-Host "CorrelationId: $correlationId"

$cliente = PostJson "$cadastro/api/clientes" @{
    cpfCnpj = $documento
    nome = "Cliente Smoke"
    email = $email
    telefone = "11999990000"
}
$clienteId = $cliente.id
Write-Host "Cliente criado: $clienteId"

$veiculo = PostJson "$cadastro/api/veiculos" @{
    clienteId = $clienteId
    placa = $placa
    renavam = $renavam
    modelo = @{ descricao = "Civic"; marca = "Honda"; ano = 2022 }
}
$veiculoId = $veiculo.id
Write-Host "Veiculo criado: $veiculoId"

$peca = PostJson "$estoque/api/pecas" @{
    precoUnitario = 50
    descricao = "Filtro de oleo smoke $suffix"
}
$pecaId = $peca.id
Write-Host "Peca criada: $pecaId"

PostJson "$estoque/api/estoque/pecas/$pecaId/ajustar" @{ quantidade = 10 } | Out-Null
Write-Host "Saldo adicionado para peca."

$servico = PostJson "$cadastro/api/servicos" @{
    maoDeObra = 100
    pecas = @(@{ id = $pecaId; quantidade = 2 })
    insumos = @()
}
$servicoId = $servico.id
Write-Host "Servico criado: $servicoId"

$ordem = PostJson "$ordens/api/ordens-servico" @{
    tipoManutencao = "Corretiva"
    cliente = @{
        nome = "Cliente Smoke"
        documento = $documento
        email = $email
        telefone = "11999990000"
    }
    veiculo = @{
        placa = $placa
        renavam = $renavam
        modelo = @{ descricao = "Civic"; marca = "Honda"; ano = 2022 }
    }
    itens = @{ servicos = @(); pecas = @(); insumos = @() }
}
$ordemId = $ordem.id
Write-Host "Ordem criada: $ordemId"

$diagnostico = PostJson "$ordens/api/ordens-servico/$ordemId/diagnostico" @{
    descricao = "Diagnostico smoke"
    servicoIds = @($servicoId)
}
$orcamentoId = $diagnostico.orcamentoId
Write-Host "Orcamento gerado: $orcamentoId"

$detalhe = GetJson "$ordens/api/ordens-servico/$ordemId"
if (-not $detalhe.orcamento -or $detalhe.orcamento.valorTotal -ne 200) {
    throw "Orcamento inesperado. Valor recebido: $($detalhe.orcamento.valorTotal)"
}

if ($detalhe.veiculoId -ne $veiculoId) {
    throw "Snapshot/veiculo inesperado no detalhe da ordem."
}

Invoke-RestMethod -Method Post -Uri "$ordens/api/orcamentos/$orcamentoId/aprovar" -Headers $headers | Out-Null
Invoke-RestMethod -Method Post -Uri "$ordens/api/orcamentos/$orcamentoId/aprovar" -Headers $headers | Out-Null
WaitStatus $ordemId "EmExecucao"
WaitQuantity $pecaId 8

Write-Host "Smoke OK: ordem=$ordemId orcamento=$orcamentoId total=$($detalhe.orcamento.valorTotal) aprovado_e_reservado=true"
