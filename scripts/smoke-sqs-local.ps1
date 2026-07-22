$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $root ".env.local"
Get-Content $envPath | Where-Object { $_ -and -not $_.StartsWith("#") } | ForEach-Object {
    $parts = $_.Split("=", 2)
    if ($parts.Length -eq 2) { Set-Item -Path "env:$($parts[0])" -Value $parts[1] }
}

$estoque = "http://127.0.0.1:$env:ESTOQUE_HTTP_PORT"
$correlationId = [guid]::NewGuid().ToString()
$headers = @{
    "X-Dev-Role" = "Funcionario"
    "X-Dev-Cpf" = "12345678901"
    "X-Dev-FuncionarioId" = "11111111-1111-1111-1111-111111111111"
    "X-Correlation-Id" = $correlationId
}

function PostJson($url, $body) {
    Invoke-RestMethod -Method Post -Uri $url -Headers $headers -ContentType "application/json" -Body ($body | ConvertTo-Json -Depth 30)
}

function AwsLocal([string[]] $awsArgs) {
    $composeArgs = @(
        "compose", "-f", "docker-compose.local.yml", "--env-file", ".env.local",
        "run", "--rm", "--entrypoint", "aws", "localstack-init",
        "--endpoint-url", "http://localstack:4566", "--region", $env:AWS_REGION
    ) + $awsArgs
    & docker @composeArgs
    if ($LASTEXITCODE -ne 0) { throw "aws local falhou: $($awsArgs -join ' ')" }
}

function GetQueueUrl($name) {
    (AwsLocal @("sqs", "get-queue-url", "--queue-name", $name, "--query", "QueueUrl", "--output", "text")).Trim()
}

function GetQueueVisibleCount($name) {
    $queueUrl = GetQueueUrl $name
    $count = (AwsLocal @(
        "sqs", "get-queue-attributes",
        "--queue-url", $queueUrl,
        "--attribute-names", "ApproximateNumberOfMessages",
        "--query", "Attributes.ApproximateNumberOfMessages",
        "--output", "text"
    )).Trim()
    [int]$count
}

function InvokeSqlScalar($database, $query) {
    $result = & docker compose -f docker-compose.local.yml --env-file .env.local exec -T sqlserver `
        /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $env:MSSQL_SA_PASSWORD `
        -d $database -h -1 -W -Q $query
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd falhou em $database" }
    ($result | Where-Object { $_ -and $_.Trim() -and $_ -notmatch "rows affected" } | Select-Object -First 1).Trim()
}

function SendEnvelope($queueName, $envelope, [string] $deduplicationId) {
    $queueUrl = GetQueueUrl $queueName
    $body = $envelope | ConvertTo-Json -Depth 30 -Compress
    $tempFile = Join-Path ([System.IO.Path]::GetTempPath()) "oficina-sqs-$deduplicationId.json"
    [System.IO.File]::WriteAllText($tempFile, $body, [System.Text.UTF8Encoding]::new($false))
    try {
        $volume = "$tempFile`:/tmp/message-body.json:ro"
        & docker compose -f docker-compose.local.yml --env-file .env.local run --rm `
            -v $volume `
            --entrypoint aws localstack-init `
            --endpoint-url http://localstack:4566 --region $env:AWS_REGION `
            sqs send-message `
            --queue-url $queueUrl `
            --message-body file:///tmp/message-body.json `
            --message-group-id $envelope.ordemServicoId `
            --message-deduplication-id $deduplicationId | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "aws local falhou: sqs send-message $queueName" }
    }
    finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
}

function NewEnvelope($messageType, $ordemServicoId, $payload, $messageId = [guid]::NewGuid().ToString()) {
    @{
        messageId = $messageId
        messageType = $messageType
        schemaVersion = 1
        occurredAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        correlationId = $correlationId
        causationId = $null
        ordemServicoId = $ordemServicoId
        payload = $payload
    }
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

function WaitQuantity($materialId, [int] $expected, [string] $label) {
    for ($i = 0; $i -lt 30; $i++) {
        $current = GetAvailableQuantity $materialId
        if ($current -eq $expected) {
            Write-Host "$label OK: saldo=$current"
            return
        }
        Start-Sleep -Seconds 2
    }
    throw "$label falhou. Saldo esperado=$expected"
}

function WaitDlqIncrease($queueName, [int] $initialCount, [string] $label) {
    for ($i = 0; $i -lt 45; $i++) {
        $current = GetQueueVisibleCount $queueName
        if ($current -gt $initialCount) {
            Write-Host "$label OK: dlq=$current"
            return
        }
        Start-Sleep -Seconds 2
    }
    throw "$label falhou. DLQ nao recebeu nova mensagem"
}

function WaitOrdensInboxStatus($messageId, [int] $expectedStatus, [string] $label) {
    for ($i = 0; $i -lt 30; $i++) {
        $status = InvokeSqlScalar "OficinaOrdensServicoDb" "SELECT TOP 1 Status FROM InboxMessages WHERE MessageId = '$messageId'"
        if ($status -and [int]$status -eq $expectedStatus) {
            Write-Host "$label OK: status=$status"
            return
        }
        Start-Sleep -Seconds 2
    }
    throw "$label falhou. Status esperado=$expectedStatus"
}

Write-Host "CorrelationId: $correlationId"

$suffix = ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString())
$peca = PostJson "$estoque/api/pecas" @{
    precoUnitario = 25
    descricao = "Peca SQS smoke $suffix"
}
$pecaId = $peca.id
PostJson "$estoque/api/estoque/pecas/$pecaId/ajustar" @{ quantidade = 10 } | Out-Null
WaitQuantity $pecaId 10 "Saldo inicial"

$ordemServicoId = [guid]::NewGuid().ToString()
$chave = "sqs-smoke-$suffix"
$reserveMessageId = [guid]::NewGuid().ToString()
$reserve = NewEnvelope "ReservarEstoque" $ordemServicoId @{
    chaveOperacao = $chave
    itens = @(@{ tipoMaterial = 1; materialId = $pecaId; quantidade = 2 })
} $reserveMessageId

SendEnvelope "oficina-estoque-comandos.fifo" $reserve $reserveMessageId
WaitQuantity $pecaId 8 "Reserva"

SendEnvelope "oficina-estoque-comandos.fifo" $reserve ([guid]::NewGuid().ToString())
WaitQuantity $pecaId 8 "Reserva duplicada"

$insuficiente = NewEnvelope "ReservarEstoque" ([guid]::NewGuid().ToString()) @{
    chaveOperacao = "sqs-insuficiente-$suffix"
    itens = @(@{ tipoMaterial = 1; materialId = $pecaId; quantidade = 999 })
}
SendEnvelope "oficina-estoque-comandos.fifo" $insuficiente $insuficiente.messageId
WaitQuantity $pecaId 8 "Saldo insuficiente nao debitou"

$release = NewEnvelope "LiberarReservaEstoque" $ordemServicoId @{
    reservaId = "00000000-0000-0000-0000-000000000000"
    chaveOperacao = $chave
}
SendEnvelope "oficina-estoque-comandos.fifo" $release $release.messageId
WaitQuantity $pecaId 10 "Liberacao"

$outOfOrderMessageId = [guid]::NewGuid().ToString()
$outOfOrderEvent = NewEnvelope "ReservaEstoqueLiberada" ([guid]::NewGuid().ToString()) @{
    reservaId = ([guid]::NewGuid().ToString())
    jaLiberada = $false
} $outOfOrderMessageId
SendEnvelope "oficina-ordens-eventos.fifo" $outOfOrderEvent $outOfOrderMessageId
WaitOrdensInboxStatus $outOfOrderMessageId 4 "Mensagem fora de ordem"

$initialDlqCount = GetQueueVisibleCount "oficina-estoque-comandos-dlq.fifo"
$queueUrl = GetQueueUrl "oficina-estoque-comandos.fifo"
$invalidId = [guid]::NewGuid().ToString()
$invalidFile = Join-Path ([System.IO.Path]::GetTempPath()) "oficina-sqs-invalid-$invalidId.json"
[System.IO.File]::WriteAllText($invalidFile, "{invalid-json", [System.Text.UTF8Encoding]::new($false))
try {
    $volume = "$invalidFile`:/tmp/message-body.json:ro"
    & docker compose -f docker-compose.local.yml --env-file .env.local run --rm `
        -v $volume `
        --entrypoint aws localstack-init `
        --endpoint-url http://localstack:4566 --region $env:AWS_REGION `
        sqs send-message `
        --queue-url $queueUrl `
        --message-body file:///tmp/message-body.json `
        --message-group-id ([guid]::NewGuid().ToString()) `
        --message-deduplication-id $invalidId | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "aws local falhou: sqs send-message invalido" }
}
finally {
    Remove-Item -LiteralPath $invalidFile -Force -ErrorAction SilentlyContinue
}
Write-Host "Envelope invalido publicado para redrive nativo/DLQ."
WaitDlqIncrease "oficina-estoque-comandos-dlq.fifo" $initialDlqCount "Envelope invalido em DLQ"

Write-Host "Smoke SQS OK: material=$pecaId ordem=$ordemServicoId"
