<#
.SYNOPSIS
    Executa o BDD distribuido de Ordens de Servico.

.DESCRIPTION
    O fluxo exercitado e Ordens -> SQS -> Estoque -> SQS -> Ordens, com as APIs
    reais dos tres microsservicos, SQL Server e filas FIFO no LocalStack.

    A execucao nao depende da AWS nem do ECR: Cadastro e Estoque sao obtidos por
    checkout em commit SHA fixo e construidos localmente. Referencia movel, como
    branch ou tag, tornaria a execucao irreproduzivel e por isso e recusada.
#>
[CmdletBinding()]
param(
    [string]$ComposeFile = 'docker-compose.bdd.yml',
    [int]$StepTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repoRoot

$artifactsDir = Join-Path $repoRoot 'artifacts/bdd'
$checkoutRoot = Join-Path $repoRoot '.bdd-checkouts'
$envFile = Join-Path $repoRoot '.env.bdd'

function Write-Info([string]$Message) { Write-Host "[bdd] $Message" }

function Get-RequiredEnv([string]$Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Variavel $Name obrigatoria para o BDD distribuido."
    }
    return $value.Trim()
}

function Assert-CommitSha([string]$Name, [string]$Value) {
    if ($Value -notmatch '^[0-9a-f]{40}$') {
        throw "$Name deve ser um commit SHA completo de 40 caracteres. Branch ou tag movel torna a execucao irreproduzivel. Valor recebido tem $($Value.Length) caractere(s)."
    }
}

function New-LocalPassword {
    # Senha efemera, valida apenas dentro do compose desta execucao.
    $bytes = [byte[]]::new(24)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }
    return 'Bdd!' + ([Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', '') + '9a'
}

function Invoke-Compose([string[]]$Arguments) {
    & docker compose -f $ComposeFile --env-file $envFile @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($Arguments -join ' ') falhou."
    }
}

function Get-Checkout {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$CommitSha,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $token = [Environment]::GetEnvironmentVariable('CROSS_REPOSITORY_TOKEN')
    $server = if ([string]::IsNullOrWhiteSpace($env:GITHUB_SERVER_URL)) { 'https://github.com' } else { $env:GITHUB_SERVER_URL }
    # $host e variavel automatica do PowerShell e nao pode ser reatribuida.
    $gitHost = ([Uri]$server).Host

    if ([string]::IsNullOrWhiteSpace($token)) {
        $remote = "$server/$Repository.git"
    }
    else {
        # O token e somente leitura, com escopo contents:read e restrito aos
        # repositorios necessarios. Nunca aparece em log.
        Write-Host "::add-mask::$token"
        $remote = "https://x-access-token:$token@$gitHost/$Repository.git"
    }

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    & git init --quiet $Destination
    if ($LASTEXITCODE -ne 0) { throw "git init falhou para $Repository." }
    & git -C $Destination remote add origin $remote
    if ($LASTEXITCODE -ne 0) { throw "git remote add falhou para $Repository." }

    # Preflight de acesso antes de qualquer build: sem isso a falha apareceria
    # apenas quando o docker tentasse usar o contexto.
    & git -C $Destination fetch --quiet --depth 1 origin $CommitSha
    if ($LASTEXITCODE -ne 0) {
        throw "Nao foi possivel obter o commit $CommitSha de $Repository. Verifique o acesso de leitura e o SHA informado."
    }
    & git -C $Destination checkout --quiet FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw "git checkout falhou para $Repository." }

    # A confirmacao do HEAD e o que impede um checkout resolvido para outro commit.
    $head = (& git -C $Destination rev-parse HEAD).Trim()
    if ($head -cne $CommitSha) {
        throw "HEAD de $Repository resolveu para $head, diferente do SHA informado $CommitSha."
    }

    Write-Info "$Repository em $head"
    return $head
}

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------

$estoqueRepository = Get-RequiredEnv 'ESTOQUE_REPOSITORY'
$estoqueRef = Get-RequiredEnv 'ESTOQUE_REF'
$cadastroRepository = Get-RequiredEnv 'CADASTRO_REPOSITORY'
$cadastroRef = Get-RequiredEnv 'CADASTRO_REF'

Assert-CommitSha 'ESTOQUE_REF' $estoqueRef
Assert-CommitSha 'CADASTRO_REF' $cadastroRef

& docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'docker compose indisponivel.' }

New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
New-Item -ItemType Directory -Path $checkoutRoot -Force | Out-Null

$estoqueContext = Join-Path $checkoutRoot 'estoque'
$cadastroContext = Join-Path $checkoutRoot 'cadastro'

$estoqueHead = Get-Checkout -Repository $estoqueRepository -CommitSha $estoqueRef -Destination $estoqueContext
$cadastroHead = Get-Checkout -Repository $cadastroRepository -CommitSha $cadastroRef -Destination $cadastroContext

# ---------------------------------------------------------------------------
# Ambiente
# ---------------------------------------------------------------------------

$ports = [ordered]@{
    CADASTRO_HTTP_PORT   = 15101
    ESTOQUE_HTTP_PORT    = 15102
    ORDENS_HTTP_PORT     = 15103
    SQLSERVER_PORT       = 14433
    LOCALSTACK_HTTP_PORT = 14566
}

$saPassword = New-LocalPassword
$envLines = @(
    "MSSQL_SA_PASSWORD=$saPassword"
    "CADASTRO_DB_PASSWORD=$(New-LocalPassword)"
    "ESTOQUE_DB_PASSWORD=$(New-LocalPassword)"
    "ORDENS_DB_PASSWORD=$(New-LocalPassword)"
    "AWS_REGION=us-east-1"
    "AWS_ACCESS_KEY_ID=test"
    "AWS_SECRET_ACCESS_KEY=test"
    "CADASTRO_CONTEXT=$cadastroContext"
    "ESTOQUE_CONTEXT=$estoqueContext"
)
foreach ($port in $ports.GetEnumerator()) { $envLines += "$($port.Key)=$($port.Value)" }

[System.IO.File]::WriteAllText($envFile, (($envLines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "::add-mask::$saPassword"

$exitCode = 0
try {
    Write-Info 'Subindo o ambiente distribuido.'
    # --wait respeita os health checks: nenhum cenario comeca antes de os tres
    # servicos responderem /ready.
    Invoke-Compose @('up', '--build', '--detach', '--wait', '--wait-timeout', '600')

    $ordensConnectionString = "Server=127.0.0.1,$($ports.SQLSERVER_PORT);Database=OficinaOrdensServicoDb;User Id=sa;Password=$saPassword;TrustServerCertificate=True;Encrypt=True"

    $env:BDD_CADASTRO_URL = "http://127.0.0.1:$($ports.CADASTRO_HTTP_PORT)"
    $env:BDD_ESTOQUE_URL = "http://127.0.0.1:$($ports.ESTOQUE_HTTP_PORT)"
    $env:BDD_ORDENS_URL = "http://127.0.0.1:$($ports.ORDENS_HTTP_PORT)"
    $env:BDD_ORDENS_CONNECTION_STRING = $ordensConnectionString
    $env:BDD_SQS_ENDPOINT = "http://127.0.0.1:$($ports.LOCALSTACK_HTTP_PORT)"
    $env:BDD_AWS_REGION = 'us-east-1'
    $env:BDD_AWS_ACCESS_KEY_ID = 'test'
    $env:BDD_AWS_SECRET_ACCESS_KEY = 'test'
    $env:BDD_STEP_TIMEOUT_SECONDS = "$StepTimeoutSeconds"

    Write-Info 'Executando os cenarios.'
    & dotnet test tests/Oficina.Ordens.Bdd/Oficina.Ordens.Bdd.csproj `
        --configuration Release `
        --logger "trx;LogFileName=bdd-results.trx" `
        --results-directory $artifactsDir
    if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE }
}
catch {
    Write-Error $_
    $exitCode = 1
}
finally {
    # Logs de todos os servicos viram artifact mesmo quando o BDD passa: em
    # falha eles sao a unica fonte de diagnostico do fluxo assincrono.
    try {
        & docker compose -f $ComposeFile --env-file $envFile logs --no-color `
            *> (Join-Path $artifactsDir 'compose-logs.txt')
    }
    catch { Write-Info 'Nao foi possivel capturar os logs do compose.' }

    try {
        $images = & docker compose -f $ComposeFile --env-file $envFile images --format json
        [System.IO.File]::WriteAllText((Join-Path $artifactsDir 'compose-images.json'), ($images | Out-String))
    }
    catch { Write-Info 'Nao foi possivel capturar os digests das imagens.' }

    $ordensHead = (& git rev-parse HEAD).Trim()
    $relatorio = @(
        '# Execucao do BDD distribuido'
        ''
        "- Ordens: commit local $ordensHead"
        "- Cadastro: $cadastroRepository em $cadastroHead"
        "- Estoque: $estoqueRepository em $estoqueHead"
        "- Pagamento: provedor da solucao com retorno Approved, o mesmo do ambiente publicado"
        "- Timeout por etapa: $StepTimeoutSeconds s"
        ''
        'Digests das imagens em compose-images.json. Logs em compose-logs.txt.'
    )
    [System.IO.File]::WriteAllText((Join-Path $artifactsDir 'README.md'), (($relatorio -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

    Write-Info 'Derrubando o ambiente e removendo volumes.'
    & docker compose -f $ComposeFile --env-file $envFile down -v --remove-orphans | Out-Null

    Remove-Item -LiteralPath $envFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $checkoutRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($exitCode -ne 0) {
    throw "BDD distribuido falhou com exit code $exitCode."
}

Write-Info 'BDD distribuido concluido com sucesso.'
