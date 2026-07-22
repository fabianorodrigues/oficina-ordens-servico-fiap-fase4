param(
    [string]$Region = $env:AWS_REGION,
    [int]$TimeoutSeconds = 30,
    [int]$AsyncTimeoutSeconds = 240
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Region)) {
    throw "AWS_REGION must be configured."
}

function Invoke-AwsText([string[]]$Arguments) {
    $output = & aws @Arguments --region $Region --output text 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "aws $($Arguments -join ' ') failed: $output"
    }
    return ($output | Out-String).Trim()
}

function Invoke-AwsJson([string[]]$Arguments) {
    $output = & aws @Arguments --region $Region --output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "aws $($Arguments -join ' ') failed: $output"
    }
    if ([string]::IsNullOrWhiteSpace($output)) { return $null }
    return $output | ConvertFrom-Json
}

function Get-SsmValue([string]$Name) {
    Invoke-AwsText -Arguments @("ssm", "get-parameter", "--name", $Name, "--query", "Parameter.Value")
}

function ConvertTo-Base64Url([byte[]]$Bytes) {
    [Convert]::ToBase64String($Bytes).TrimEnd("=").Replace("+", "-").Replace("/", "_")
}

function New-Jwt([string]$SigningKey, [string]$Cpf) {
    $now = [DateTimeOffset]::UtcNow
    $header = @{ alg = "HS256"; typ = "JWT" } | ConvertTo-Json -Compress
    $payload = @{
        iss = "oficina"
        aud = "oficina-api"
        sub = [guid]::NewGuid().ToString()
        cpf = $Cpf
        role = "Admin"
        name = "Bootstrap Admin"
        iat = $now.ToUnixTimeSeconds()
        exp = $now.AddMinutes(30).ToUnixTimeSeconds()
        jti = [guid]::NewGuid().ToString("N")
    } | ConvertTo-Json -Compress

    $encodedHeader = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($header))
    $encodedPayload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($payload))
    $unsigned = "$encodedHeader.$encodedPayload"
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($SigningKey))
    try {
        $signature = ConvertTo-Base64Url ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($unsigned)))
    }
    finally {
        $hmac.Dispose()
    }

    return "$unsigned.$signature"
}

function New-Cpf([int64]$Seed) {
    $base = ("{0:D9}" -f ($Seed % 1000000000))
    if ($base -match "^(\d)\1{8}$") { $base = "123456789" }
    $digits = @($base.ToCharArray() | ForEach-Object { [int]::Parse($_) })

    $sum = 0
    for ($i = 0; $i -lt 9; $i++) { $sum += $digits[$i] * (10 - $i) }
    $d1 = ($sum * 10) % 11
    if ($d1 -eq 10) { $d1 = 0 }
    $digits += $d1

    $sum = 0
    for ($i = 0; $i -lt 10; $i++) { $sum += $digits[$i] * (11 - $i) }
    $d2 = ($sum * 10) % 11
    if ($d2 -eq 10) { $d2 = 0 }
    $digits += $d2

    return ($digits -join "")
}

function Get-PropertyValue($Object, [string[]]$Names) {
    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
        if ($null -ne $property -and $null -ne $property.Value -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return $property.Value
        }
    }
    return $null
}

function Invoke-Api {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("GET", "POST", "PUT", "PATCH")][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [object]$Body,
        [string]$Token,
        [int[]]$ExpectedStatus = @(200)
    )

    $uri = "$($script:BaseUrl.TrimEnd([char]"/"))/$($Path.TrimStart([char]"/"))"
    $headers = @{ "x-correlation-id" = "aws-e2e-$script:RunId" }
    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $headers["Authorization"] = "Bearer $Token"
    }

    $requestArgs = @{
        Method = $Method
        Uri = $uri
        Headers = $headers
        TimeoutSec = $TimeoutSeconds
        SkipHttpErrorCheck = $true
    }
    if ($PSBoundParameters.ContainsKey("Body")) {
        $requestArgs["ContentType"] = "application/json"
        $requestArgs["Body"] = ($Body | ConvertTo-Json -Depth 12 -Compress)
    }

    $response = Invoke-WebRequest @requestArgs
    if ($ExpectedStatus -notcontains [int]$response.StatusCode) {
        $content = ($response.Content | Out-String).Trim()
        if ($content.Length -gt 800) { $content = $content.Substring(0, 800) }
        throw "$Method $Path returned HTTP $($response.StatusCode). Body: $content"
    }

    if ([string]::IsNullOrWhiteSpace($response.Content)) {
        return [pscustomobject]@{ statusCode = [int]$response.StatusCode }
    }

    try {
        return $response.Content | ConvertFrom-Json
    }
    catch {
        return [pscustomobject]@{ statusCode = [int]$response.StatusCode; content = $response.Content }
    }
}

Invoke-AwsJson -Arguments @("sts", "get-caller-identity") | Out-Null
$script:BaseUrl = Get-SsmValue "/oficina/infra/api/url"
$apiId = Get-SsmValue "/oficina/infra/api/id"
Invoke-AwsJson -Arguments @("apigatewayv2", "get-api", "--api-id", $apiId) | Out-Null

foreach ($parameter in @(
    "/oficina/infra/sqs/estoque-comandos/url",
    "/oficina/infra/sqs/estoque-comandos-dlq/url",
    "/oficina/infra/sqs/ordens-eventos/url",
    "/oficina/infra/sqs/ordens-eventos-dlq/url"
)) {
    $queueUrl = Get-SsmValue $parameter
    Invoke-AwsJson -Arguments @("sqs", "get-queue-attributes", "--queue-url", $queueUrl, "--attribute-names", "FifoQueue", "RedrivePolicy", "VisibilityTimeout") | Out-Null
}

foreach ($target in @("cadastro", "estoque", "ordens")) {
    Invoke-Api -Method GET -Path "/health/$target" -ExpectedStatus @(200) | Out-Null
}

$secretString = Invoke-AwsText -Arguments @("secretsmanager", "get-secret-value", "--secret-id", "/oficina/auth/jwt", "--query", "SecretString")
$jwtSecret = $secretString | ConvertFrom-Json
$signingKey = [string]$jwtSecret.SigningKey
if ([string]::IsNullOrWhiteSpace($signingKey)) {
    throw "/oficina/auth/jwt does not contain SigningKey."
}
Write-Host "::add-mask::$signingKey"

$script:RunId = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
$seed = [int64]$script:RunId
$adminCpf = New-Cpf ($seed + 101)
$clienteCpf = New-Cpf ($seed + 202)
$adminPassword = "E2e!" + [guid]::NewGuid().ToString("N")
Write-Host "::add-mask::$adminPassword"

$bootstrapToken = New-Jwt -SigningKey $signingKey -Cpf $adminCpf
Write-Host "::add-mask::$bootstrapToken"

Invoke-Api -Method POST -Path "/api/admin/funcionarios" -Token $bootstrapToken -ExpectedStatus @(201) -Body @{
    nome = "Admin E2E"
    cpf = $adminCpf
    senha = $adminPassword
    perfil = "Admin"
} | Out-Null

$auth = Invoke-Api -Method POST -Path "/api/auth/cpf" -ExpectedStatus @(200) -Body @{
    cpf = $adminCpf
    password = $adminPassword
}
$token = [string](Get-PropertyValue $auth @("accessToken", "token", "jwt"))
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "Auth response did not include an access token."
}
Write-Host "::add-mask::$token"

$suffix = $script:RunId.Substring([Math]::Max(0, $script:RunId.Length - 4))
$placa = "E2E$("{0:D4}" -f ([int]$suffix % 10000))"
$renavam = "{0:D11}" -f (($seed + 303) % 100000000000)

$cliente = Invoke-Api -Method POST -Path "/api/clientes" -Token $token -ExpectedStatus @(201) -Body @{
    cpfCnpj = $clienteCpf
    nome = "Cliente E2E"
    email = "cliente-$script:RunId@example.com"
    telefone = "11999990000"
}
$clienteId = [string](Get-PropertyValue $cliente @("id"))
if ([string]::IsNullOrWhiteSpace($clienteId)) { throw "Cliente creation response did not include id." }

Invoke-Api -Method POST -Path "/api/veiculos" -Token $token -ExpectedStatus @(201) -Body @{
    clienteId = $clienteId
    placa = $placa
    renavam = $renavam
    modelo = @{
        descricao = "E2E Hatch"
        marca = "Oficina"
        ano = 2024
    }
} | Out-Null

$peca = Invoke-Api -Method POST -Path "/api/pecas" -Token $token -ExpectedStatus @(201) -Body @{
    precoUnitario = 49.90
    descricao = "Peca E2E $script:RunId"
}
$pecaId = [string](Get-PropertyValue $peca @("id"))
if ([string]::IsNullOrWhiteSpace($pecaId)) { throw "Peca creation response did not include id." }

Invoke-Api -Method POST -Path "/api/estoque/pecas/$pecaId/ajustar" -Token $token -ExpectedStatus @(204) -Body @{
    quantidade = 5
} | Out-Null

$servico = Invoke-Api -Method POST -Path "/api/servicos" -Token $token -ExpectedStatus @(201) -Body @{
    maoDeObra = 150
    pecas = @(@{ id = $pecaId; quantidade = 1 })
    insumos = @()
}
$servicoId = [string](Get-PropertyValue $servico @("id"))
if ([string]::IsNullOrWhiteSpace($servicoId)) { throw "Servico creation response did not include id." }

$ordem = Invoke-Api -Method POST -Path "/api/ordens-servico" -Token $token -ExpectedStatus @(201) -Body @{
    tipoManutencao = "Preventiva"
    cliente = @{
        nome = "Cliente E2E"
        documento = $clienteCpf
        email = "cliente-$script:RunId@example.com"
        telefone = "11999990000"
    }
    veiculo = @{
        placa = $placa
        renavam = $renavam
        modelo = @{
            descricao = "E2E Hatch"
            marca = "Oficina"
            ano = 2024
        }
    }
    itens = @{
        servicos = @(@{ servicoId = $servicoId })
        pecas = @()
        insumos = @()
    }
}
$ordemId = [string](Get-PropertyValue $ordem @("id"))
if ([string]::IsNullOrWhiteSpace($ordemId)) { throw "Ordem creation response did not include id." }

$ordemDetalhe = Invoke-Api -Method GET -Path "/api/ordens-servico/$ordemId" -Token $token -ExpectedStatus @(200)
if ($null -eq $ordemDetalhe.orcamento) {
    throw "Ordem detail response did not include orcamento."
}
$orcamentoId = [string](Get-PropertyValue $ordemDetalhe.orcamento @("id", "orcamentoId"))
if ([string]::IsNullOrWhiteSpace($orcamentoId)) {
    throw "Ordem detail response did not include orcamento.id."
}

Invoke-Api -Method POST -Path "/api/orcamentos/$orcamentoId/aprovar" -Token $token -ExpectedStatus @(204) | Out-Null

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($AsyncTimeoutSeconds)
$lastStatus = ""
do {
    Start-Sleep -Seconds 5
    $statusResponse = Invoke-Api -Method GET -Path "/api/ordens-servico/$ordemId/status" -Token $token -ExpectedStatus @(200)
    $lastStatus = [string](Get-PropertyValue $statusResponse @("status"))
    if ($lastStatus -eq "EmExecucao") { break }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

if ($lastStatus -ne "EmExecucao") {
    throw "Ordem did not reach EmExecucao after payment mock approval and stock reservation. Last status: $lastStatus"
}

Invoke-Api -Method GET -Path "/api/estoque/pecas/$pecaId" -Token $token -ExpectedStatus @(200) | Out-Null

@"
## AWS E2E Validate

- API URL resolved from SSM
- Health endpoints validated
- Bootstrap token generated without printing SigningKey
- Auth `/api/auth/cpf` validated
- Cadastro, Estoque and Ordens main flow validated
- Payment mock approved and order reached EmExecucao
- SQS metadata validated

Sensitive values were not printed.
"@ | Add-Content -Path $env:GITHUB_STEP_SUMMARY

Write-Host "AWS E2E validation completed."
