<#
.SYNOPSIS
    Publica o microsservico no cluster K3s single-node por Systems Manager.

.DESCRIPTION
    O transporte e dividido em dois comandos SSM por decisao de seguranca:

      Stage  - recebe apenas o nome de um parametro SecureString e o SHA-256 do
               pacote. Baixa, confere o hash, extrai e revalida. Nao aplica
               nenhum recurso Kubernetes.
      Deploy - revalida os hashes locais, faz o pull das duas imagens, cria
               ConfigMap e Secrets, executa o Migration Job, aplica Deployment e
               Service, valida o rollout e a capacidade do node.

    Entre um e outro o objeto S3 e o SecureString sao removidos, de modo que a
    credencial temporaria deixa de existir antes de qualquer alteracao no
    cluster. Se Stage e Deploy fossem um unico comando, a URL sobreviveria
    durante todo o rollout.

    Nenhum valor secreto passa pelo runner, pelo S3 ou por parametro do Run
    Command: as connection strings sao lidas do Secrets Manager dentro da EC2.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeImage,
    [Parameter(Mandatory = $true)][string]$MigrationImage,
    [Parameter(Mandatory = $true)][string]$AwsRegion,
    [Parameter(Mandatory = $true)][string]$CommitSha,
    [Parameter(Mandatory = $true)][string]$RunId,
    [string]$RunAttempt = '1',
    [string]$StateBucket = '',
    [ValidateSet('s3', 'ssm')][string]$Transport = 's3',
    [string]$ConfigPath = 'config/official.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Utilitarios
# ---------------------------------------------------------------------------

function Invoke-Aws {
    param([Parameter(Mandatory = $true)][string[]]$Arguments, [switch]$AllowFailure)

    $output = & aws @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "AWS CLI falhou: aws $($Arguments -join ' ')`n$($output | Out-String)"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = ($output | Out-String).Trim() }
}

function Get-SsmValue {
    param([Parameter(Mandatory = $true)][string]$Name)

    $result = Invoke-Aws -Arguments @('ssm', 'get-parameter', '--name', $Name, '--region', $AwsRegion, '--query', 'Parameter.Value', '--output', 'text')
    $value = $result.Output.Trim()
    if ([string]::IsNullOrWhiteSpace($value) -or $value -eq 'None') {
        throw "Parametro SSM ausente ou vazio: $Name"
    }
    return $value
}

function Test-ActionAllowed {
    param(
        [Parameter(Mandatory = $true)][string]$PrincipalArn,
        [Parameter(Mandatory = $true)][string]$Action,
        [Parameter(Mandatory = $true)][string]$ResourceArn
    )

    # aws:RequestedRegion e obrigatorio: sem essa chave de contexto a simulacao
    # devolve implicitDeny para politicas condicionadas por regiao, e o
    # resultado seria um falso negativo.
    $result = Invoke-Aws -AllowFailure -Arguments @(
        'iam', 'simulate-principal-policy',
        '--policy-source-arn', $PrincipalArn,
        '--action-names', $Action,
        '--resource-arns', $ResourceArn,
        '--context-entries', "ContextKeyName=aws:RequestedRegion,ContextKeyType=string,ContextKeyValues=$AwsRegion",
        '--query', 'EvaluationResults[0].EvalDecision',
        '--output', 'text'
    )
    if ($result.ExitCode -ne 0) {
        Write-Host "Simulacao indisponivel para ${Action}: tratando como nao permitido."
        return $false
    }
    $decision = $result.Output.Trim()
    Write-Host "  $Action -> $decision"
    return ($decision -eq 'allowed')
}

function Invoke-RunCommand {
    param(
        [Parameter(Mandatory = $true)][string]$InstanceId,
        [Parameter(Mandatory = $true)][string]$Script,
        [Parameter(Mandatory = $true)][string]$Comment,
        [int]$ExecutionTimeoutSeconds = 3600,
        [int]$PollTimeoutSeconds = 2400
    )

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $payload = [ordered]@{
            commands         = @($Script)
            executionTimeout = @("$ExecutionTimeoutSeconds")
        }
        $payloadPath = Join-Path $tempDir 'parameters.json'
        $payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $payloadPath -Encoding utf8

        $send = Invoke-Aws -Arguments @(
            'ssm', 'send-command',
            '--instance-ids', $InstanceId,
            '--document-name', 'AWS-RunShellScript',
            '--comment', $Comment,
            '--region', $AwsRegion,
            '--parameters', "file://$payloadPath",
            '--query', 'Command.CommandId',
            '--output', 'text'
        )
        $commandId = $send.Output.Trim()

        $deadline = (Get-Date).AddSeconds($PollTimeoutSeconds)
        $status = 'Pending'
        while ($true) {
            Start-Sleep -Seconds 8
            $poll = Invoke-Aws -AllowFailure -Arguments @(
                'ssm', 'get-command-invocation',
                '--command-id', $commandId,
                '--instance-id', $InstanceId,
                '--region', $AwsRegion,
                '--query', 'Status',
                '--output', 'text'
            )
            if ($poll.ExitCode -eq 0) { $status = $poll.Output.Trim() }
            if (@('Success', 'Failed', 'Cancelled', 'TimedOut') -contains $status) { break }
            if ((Get-Date) -gt $deadline) { $status = 'TimedOut'; break }
        }

        $stdout = (Invoke-Aws -AllowFailure -Arguments @('ssm', 'get-command-invocation', '--command-id', $commandId, '--instance-id', $InstanceId, '--region', $AwsRegion, '--query', 'StandardOutputContent', '--output', 'text')).Output
        $stderr = (Invoke-Aws -AllowFailure -Arguments @('ssm', 'get-command-invocation', '--command-id', $commandId, '--instance-id', $InstanceId, '--region', $AwsRegion, '--query', 'StandardErrorContent', '--output', 'text')).Output

        return [pscustomobject]@{ CommandId = $commandId; Status = $status; StandardOutput = $stdout; StandardError = $stderr }
    }
    finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Expand-Tokens {
    param([Parameter(Mandatory = $true)][string]$Text, [Parameter(Mandatory = $true)][hashtable]$Tokens)

    foreach ($key in $Tokens.Keys) {
        $Text = $Text.Replace("@@$key@@", [string]$Tokens[$key])
    }
    if ($Text -match '@@[A-Z_]+@@') {
        throw 'Token nao substituido no comando remoto.'
    }
    return $Text
}

# ---------------------------------------------------------------------------
# Contrato e metadados
# ---------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $ConfigPath)) { throw "Configuracao oficial nao encontrada: $ConfigPath" }
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json

$serviceName = [string]$config.kubernetes.deploymentName
$shortSha = $CommitSha.Substring(0, [Math]::Min(12, $CommitSha.Length)).ToLowerInvariant()
$deployRunId = "$RunId-$RunAttempt".ToLowerInvariant()
if ($deployRunId -notmatch '^[a-z0-9]([-a-z0-9]*[a-z0-9])?$') {
    throw "RunId/RunAttempt geraram um identificador Kubernetes invalido: $deployRunId"
}
$runHashInput = [System.Text.Encoding]::UTF8.GetBytes($deployRunId)
$runHash = ([BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($runHashInput))).Replace('-', '').Substring(0, 12).ToLowerInvariant()
$migrationJobId = "$($shortSha.Substring(0, [Math]::Min(8, $shortSha.Length)))-$runHash"

foreach ($image in @($RuntimeImage, $MigrationImage)) {
    if ($image -match ':latest$' -or $image -notmatch ':') {
        throw "Imagem deve usar tag imutavel derivada do commit: $image"
    }
}

Write-Host "Servico: $serviceName"
Write-Host "Commit: $shortSha"
Write-Host "Run: $deployRunId"

Invoke-Aws -Arguments @('sts', 'get-caller-identity', '--output', 'text') | Out-Null
$identity = (Invoke-Aws -Arguments @('sts', 'get-caller-identity', '--output', 'json')).Output | ConvertFrom-Json
$accountId = [string]$identity.Account
$principalArn = [string]$identity.Arn

$namespace = Get-SsmValue $config.aws.namespaceParameter
$instanceId = Get-SsmValue $config.aws.instanceIdParameter
$targetGroupArn = Get-SsmValue $config.kubernetes.targetGroupArnParameter
$nodePort = Get-SsmValue $config.kubernetes.nodePortParameter

if ([int]$nodePort -ne [int]$config.kubernetes.nodePort) {
    throw "NodePort publicado pela plataforma ($nodePort) diverge de config/official.json ($($config.kubernetes.nodePort))."
}

$ping = (Invoke-Aws -Arguments @('ssm', 'describe-instance-information', '--filters', "Key=InstanceIds,Values=$instanceId", '--region', $AwsRegion, '--query', 'InstanceInformationList[0].PingStatus', '--output', 'text')).Output.Trim()
if ($ping -ne 'Online') { throw "O node $instanceId nao esta Online no Systems Manager (status: $ping)." }

# ---------------------------------------------------------------------------
# Renderizacao dos manifests
# ---------------------------------------------------------------------------

$tokens = @{
    '__NAMESPACE__'      = $namespace
    '__AWS_REGION__'     = $AwsRegion
    '__IMAGE__'          = $RuntimeImage
    '__MIGRATION_IMAGE__' = $MigrationImage
    '__SHORT_SHA__'      = $shortSha
    '__DEPLOY_RUN_ID__'  = $deployRunId
    '__MIGRATION_JOB_ID__' = $migrationJobId
    '__NODE_PORT__'      = $nodePort
}

if ($null -ne $config.PSObject.Properties['queues']) {
    $tokens['__COMMANDS_QUEUE_URL__'] = Get-SsmValue $config.queues.commandsUrlParameter
    $tokens['__COMMANDS_DLQ_URL__'] = Get-SsmValue $config.queues.commandsDlqUrlParameter
    $tokens['__EVENTS_QUEUE_URL__'] = Get-SsmValue $config.queues.eventsUrlParameter
    $tokens['__EVENTS_DLQ_URL__'] = Get-SsmValue $config.queues.eventsDlqUrlParameter
}
if ($null -ne $config.PSObject.Properties['services']) {
    $tokens['__ALB_DNS__'] = Get-SsmValue $config.services.cadastroBaseUrlParameter
}

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) "oficina-deploy-$deployRunId"
if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$packageDir = Join-Path $workRoot 'package'
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

# Os dois unicos placeholders que sobrevivem ao pacote. Sao resolvidos dentro da
# EC2, a partir do Secrets Manager: nenhum valor secreto entra no tar.gz.
$allowedPlaceholders = @('__APP_CONNECTION_STRING__', '__MIGRATION_CONNECTION_STRING__')

$manifestFiles = [ordered]@{
    'configmap.yaml'                = $config.kubernetes.manifests.configMap
    'deployment.yaml'               = $config.kubernetes.manifests.deployment
    'service.yaml'                  = $config.kubernetes.manifests.service
    'migration-job.yaml'            = $config.kubernetes.manifests.migrationJob
    'secret-app-template.yaml'      = $config.kubernetes.manifests.secretApp
    'secret-migration-template.yaml' = $config.kubernetes.manifests.secretMigration
}

foreach ($entry in $manifestFiles.GetEnumerator()) {
    $source = [string]$entry.Value
    if (-not (Test-Path -LiteralPath $source)) { throw "Manifesto ausente: $source" }

    $content = (Get-Content -LiteralPath $source -Raw) -replace "`r`n", "`n"
    foreach ($token in $tokens.Keys) {
        $content = $content.Replace($token, [string]$tokens[$token])
    }

    $remaining = [regex]::Matches($content, '__[A-Z0-9_]+__') | ForEach-Object { $_.Value } | Sort-Object -Unique
    foreach ($placeholder in $remaining) {
        if ($allowedPlaceholders -notcontains $placeholder) {
            throw "Placeholder nao resolvido em $($entry.Key): $placeholder"
        }
    }

    $target = Join-Path $packageDir $entry.Key
    [System.IO.File]::WriteAllText($target, $content, (New-Object System.Text.UTF8Encoding($false)))
}

# O pacote nao pode conter valor sensivel: ele sai do runner e passa pelo S3.
$forbidden = @('Password\s*=', 'Server\s*=\s*tcp:', 'AKIA[0-9A-Z]{16}', 'ASIA[0-9A-Z]{16}', 'aws_secret_access_key')
foreach ($file in Get-ChildItem -LiteralPath $packageDir -File) {
    $raw = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($pattern in $forbidden) {
        if ([regex]::IsMatch($raw, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            throw "Conteudo sensivel detectado em $($file.Name): $pattern"
        }
    }
}

# SHA256SUMS permite revalidar cada manifesto no comando de Deploy, e nao apenas
# o pacote inteiro no Stage.
$sumsLines = foreach ($file in Get-ChildItem -LiteralPath $packageDir -File | Sort-Object Name) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
[System.IO.File]::WriteAllText((Join-Path $packageDir 'SHA256SUMS'), (($sumsLines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

$packagePath = Join-Path $workRoot 'manifests.tar.gz'
& tar -czf $packagePath -C $packageDir .
if ($LASTEXITCODE -ne 0) { throw 'Falha ao empacotar os manifests.' }
$packageSha = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$packageBytes = (Get-Item -LiteralPath $packagePath).Length
Write-Host "Pacote: $packageBytes bytes, SHA-256 $packageSha"

$stageDir = "/opt/oficina/stage/$serviceName-$deployRunId"

# ---------------------------------------------------------------------------
# Preflight do transporte
# ---------------------------------------------------------------------------

$parameterName = "$($config.deploy.parameterPathPrefix)$deployRunId/manifest-url"
$objectKey = "$($config.deploy.s3Prefix)$deployRunId/manifests.tar.gz"
$useS3 = ($Transport -eq 's3')

if ($useS3) {
    if ([string]::IsNullOrWhiteSpace($StateBucket)) {
        Write-Host 'Bucket nao informado: usando o fallback por arquivo via SSM.'
        $useS3 = $false
    }
}

if ($useS3) {
    Write-Host 'Preflight de permissoes do transporte por S3:'
    $objectArn = "arn:aws:s3:::$StateBucket/$($config.deploy.s3Prefix)*"
    $parameterArn = "arn:aws:ssm:${AwsRegion}:${accountId}:parameter$($config.deploy.parameterPathPrefix)*"

    $checks = @(
        @{ Action = 's3:PutObject'; Resource = $objectArn },
        # O executor precisa de GetObject porque e ele quem assina a URL: a
        # assinatura so vale para uma acao que o signatario poderia executar.
        @{ Action = 's3:GetObject'; Resource = $objectArn },
        @{ Action = 's3:DeleteObject'; Resource = $objectArn },
        @{ Action = 's3:DeleteObjectVersion'; Resource = $objectArn },
        @{ Action = 'ssm:PutParameter'; Resource = $parameterArn },
        # ssm:DeleteParameter e obrigatorio. Sem ele o SecureString nao e criado:
        # sobrescrever o parametro nao apaga o valor, apenas acrescenta versao.
        @{ Action = 'ssm:DeleteParameter'; Resource = $parameterArn }
    )

    foreach ($check in $checks) {
        if (-not (Test-ActionAllowed -PrincipalArn $principalArn -Action $check.Action -ResourceArn $check.Resource)) {
            Write-Host "Permissao $($check.Action) indisponivel: nenhum objeto e nenhum parametro sera criado. Usando o fallback por arquivo via SSM."
            $useS3 = $false
            break
        }
    }
}

# ---------------------------------------------------------------------------
# Stage
# ---------------------------------------------------------------------------

$stageScriptS3 = @'
set -euo pipefail
umask 077

PARAM_NAME='@@PARAM_NAME@@'
PACKAGE_SHA='@@PACKAGE_SHA@@'
STAGE_DIR='@@STAGE_DIR@@'
REGION='@@REGION@@'

TMP="$(mktemp -d)"
cleanup() {
    status=$?
    rm -rf "$TMP"
    exit "$status"
}
trap cleanup EXIT

rm -rf "$STAGE_DIR"
install -d -m 0700 "$STAGE_DIR"

# A URL pre-assinada vale para qualquer portador ate expirar. Por isso ela vem
# de um SecureString, e nao do corpo do Run Command, e sai da memoria logo apos
# o download.
URL="$(aws ssm get-parameter --name "$PARAM_NAME" --with-decryption --region "$REGION" --query Parameter.Value --output text)"
curl -sS --fail --max-time 120 -o "$TMP/manifests.tar.gz" "$URL"
unset URL

printf '%s  %s\n' "$PACKAGE_SHA" "$TMP/manifests.tar.gz" | sha256sum -c - >/dev/null
tar -xzf "$TMP/manifests.tar.gz" -C "$STAGE_DIR"

cd "$STAGE_DIR"
sha256sum -c SHA256SUMS >/dev/null

unexpected="$(grep -rhoE '__[A-Z0-9_]+__' "$STAGE_DIR" | sort -u | grep -vE '^__(APP|MIGRATION)_CONNECTION_STRING__$' || true)"
if [ -n "$unexpected" ]; then
    echo "Placeholder inesperado no pacote: $unexpected" >&2
    exit 1
fi

if grep -rniE 'password[[:space:]]*=|server[[:space:]]*=[[:space:]]*tcp:|AKIA[0-9A-Z]{16}' "$STAGE_DIR"; then
    echo 'Conteudo sensivel detectado no pacote.' >&2
    exit 1
fi

echo STAGE_OK
'@

$stageScriptFile = @'
set -euo pipefail
umask 077

STAGE_DIR='@@STAGE_DIR@@'
FILE_NAME='@@FILE_NAME@@'
FILE_SHA='@@FILE_SHA@@'
FILE_B64='@@FILE_B64@@'
FIRST='@@FIRST@@'

if [ "$FIRST" = "true" ]; then
    rm -rf "$STAGE_DIR"
    install -d -m 0700 "$STAGE_DIR"
fi
[ -d "$STAGE_DIR" ] || { echo "Diretorio de stage ausente." >&2; exit 1; }

printf '%s' "$FILE_B64" | base64 -d > "$STAGE_DIR/$FILE_NAME"
printf '%s  %s\n' "$FILE_SHA" "$STAGE_DIR/$FILE_NAME" | sha256sum -c - >/dev/null
chmod 0600 "$STAGE_DIR/$FILE_NAME"
echo "STAGE_FILE_OK $FILE_NAME"
'@

if ($useS3) {
    Write-Host 'Transporte por S3 com URL pre-assinada.'
    $s3Uri = "s3://$StateBucket/$objectKey"
    Invoke-Aws -Arguments @('s3', 'cp', $packagePath, $s3Uri, '--region', $AwsRegion, '--only-show-errors') | Out-Null

    $parameterCreated = $false
    try {
        $presign = Invoke-Aws -Arguments @('s3', 'presign', $s3Uri, '--region', $AwsRegion, '--expires-in', "$($config.deploy.presignedUrlTtlSeconds)")
        $presignedUrl = $presign.Output.Trim()
        Write-Host "::add-mask::$presignedUrl"

        $paramPayloadDir = Join-Path $workRoot 'param'
        New-Item -ItemType Directory -Path $paramPayloadDir -Force | Out-Null
        $paramPayloadPath = Join-Path $paramPayloadDir 'put-parameter.json'
        [ordered]@{
            Name      = $parameterName
            Value     = $presignedUrl
            Type      = 'SecureString'
            KeyId     = 'alias/aws/ssm'
            Overwrite = $false
        } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $paramPayloadPath -Encoding utf8

        Invoke-Aws -Arguments @('ssm', 'put-parameter', '--cli-input-json', "file://$paramPayloadPath", '--region', $AwsRegion) | Out-Null
        $parameterCreated = $true
        $presignedUrl = $null
        Remove-Item -LiteralPath $paramPayloadPath -Force -ErrorAction SilentlyContinue

        $stageScript = Expand-Tokens -Text $stageScriptS3 -Tokens @{
            'PARAM_NAME'  = $parameterName
            'PACKAGE_SHA' = $packageSha
            'STAGE_DIR'   = $stageDir
            'REGION'      = $AwsRegion
        }

        $stage = Invoke-RunCommand -InstanceId $instanceId -Script $stageScript -Comment "$serviceName stage" -ExecutionTimeoutSeconds 600 -PollTimeoutSeconds 900
        Write-Host $stage.StandardOutput
        if ($stage.Status -ne 'Success') {
            Write-Host $stage.StandardError
            throw "Stage falhou com status $($stage.Status)."
        }
    }
    finally {
        # A credencial temporaria deixa de existir antes de qualquer alteracao no
        # cluster, tanto no caminho de sucesso quanto no de falha.
        Invoke-Aws -AllowFailure -Arguments @('s3', 'rm', $s3Uri, '--region', $AwsRegion, '--only-show-errors') | Out-Null
        if ($parameterCreated) {
            Invoke-Aws -AllowFailure -Arguments @('ssm', 'delete-parameter', '--name', $parameterName, '--region', $AwsRegion) | Out-Null
            $check = Invoke-Aws -AllowFailure -Arguments @('ssm', 'get-parameter', '--name', $parameterName, '--region', $AwsRegion)
            if ($check.ExitCode -eq 0) { throw "O SecureString $parameterName ainda existe apos a exclusao." }
            Write-Host 'Objeto S3 e SecureString removidos.'
        }
    }
}
else {
    Write-Host 'Fallback: um manifesto por Run Command, com tamanho e hash validados individualmente.'
    # Pacote Base64 unico esta proibido: o modo de falha e truncamento silencioso.
    $first = $true
    foreach ($file in Get-ChildItem -LiteralPath $packageDir -File | Sort-Object Name) {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        if ($bytes.Length -gt [int]$config.deploy.maxManifestBytes) {
            throw "Manifesto $($file.Name) tem $($bytes.Length) bytes e excede o limite de $($config.deploy.maxManifestBytes)."
        }
        $script = Expand-Tokens -Text $stageScriptFile -Tokens @{
            'STAGE_DIR' = $stageDir
            'FILE_NAME' = $file.Name
            'FILE_SHA'  = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            'FILE_B64'  = [Convert]::ToBase64String($bytes)
            'FIRST'     = $first.ToString().ToLowerInvariant()
        }
        $result = Invoke-RunCommand -InstanceId $instanceId -Script $script -Comment "$serviceName stage $($file.Name)" -ExecutionTimeoutSeconds 300 -PollTimeoutSeconds 600
        Write-Host $result.StandardOutput
        if ($result.Status -ne 'Success') {
            Write-Host $result.StandardError
            throw "Envio do manifesto $($file.Name) falhou com status $($result.Status)."
        }
        $first = $false
    }
    # Os manifests so sao aplicados depois que todos foram recebidos e validados.
}

# ---------------------------------------------------------------------------
# Deploy
# ---------------------------------------------------------------------------

$deployScript = @'
set -euo pipefail
umask 077
export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
export PATH="$PATH:/usr/local/bin"

STAGE_DIR='@@STAGE_DIR@@'
NS='@@NAMESPACE@@'
SVC='@@SERVICE@@'
SHORT_SHA='@@SHORT_SHA@@'
DEPLOY_RUN_ID='@@DEPLOY_RUN_ID@@'
MIGRATION_JOB_ID='@@MIGRATION_JOB_ID@@'
REGION='@@REGION@@'
RUNTIME_IMAGE='@@RUNTIME_IMAGE@@'
MIGRATION_IMAGE='@@MIGRATION_IMAGE@@'
APP_SECRET_ID='@@APP_SECRET_ID@@'
MIGRATION_SECRET_ID='@@MIGRATION_SECRET_ID@@'
MIGRATION_TIMEOUT='@@MIGRATION_TIMEOUT@@'
ROLLOUT_TIMEOUT='@@ROLLOUT_TIMEOUT@@'
JOB="$SVC-migration-$MIGRATION_JOB_ID"

cleanup() {
    status=$?
    # A credencial de migration nao permanece no cluster depois do Job.
    k3s kubectl -n "$NS" delete secret "$SVC-database-migration" --ignore-not-found >/dev/null 2>&1 || true
    rm -rf "$STAGE_DIR"
    exit "$status"
}
trap cleanup EXIT

cd "$STAGE_DIR"
sha256sum -c SHA256SUMS >/dev/null
echo "Hashes locais reconferidos."

# As duas imagens sao obtidas antes de qualquer apply: sem isso um problema de
# registry viraria timeout de migration, com o Job em ImagePullBackOff dentro da
# janela de espera. O containerd do K3s nao tem credential helper de ECR.
TOKEN="$(aws ecr get-login-password --region "$REGION")"
k3s ctr --namespace k8s.io images pull --user "AWS:$TOKEN" "$RUNTIME_IMAGE" >/dev/null
k3s ctr --namespace k8s.io images pull --user "AWS:$TOKEN" "$MIGRATION_IMAGE" >/dev/null
unset TOKEN
echo "Imagens de runtime e de migration disponiveis no node."

k3s kubectl apply -f "$STAGE_DIR/configmap.yaml" >/dev/null
echo "ConfigMaps aplicados."

render_secret() {
    template="$1"
    secret_id="$2"
    placeholder="$3"
    out="$4"

    cs="$(aws secretsmanager get-secret-value --secret-id "$secret_id" --region "$REGION" --query SecretString --output text | jq -r '.ConnectionString')"
    if [ -z "$cs" ] || [ "$cs" = "null" ]; then
        echo "Connection string ausente no secret informado." >&2
        exit 1
    fi
    b64="$(printf '%s' "$cs" | base64 -w0)"
    unset cs
    sed "s|$placeholder|$b64|" "$template" > "$out"
    unset b64
    if grep -q "$placeholder" "$out"; then
        echo "Placeholder do Secret nao substituido." >&2
        exit 1
    fi
    k3s kubectl apply -f "$out" >/dev/null
    if command -v shred >/dev/null 2>&1; then
        shred -u "$out" 2>/dev/null || rm -f "$out"
    else
        rm -f "$out"
    fi
}

# Os Secrets vem antes do Job porque o Job precisa da credencial de migration
# para rodar.
render_secret "$STAGE_DIR/secret-app-template.yaml" "$APP_SECRET_ID" '__APP_CONNECTION_STRING__' "$STAGE_DIR/secret-app.yaml"
render_secret "$STAGE_DIR/secret-migration-template.yaml" "$MIGRATION_SECRET_ID" '__MIGRATION_CONNECTION_STRING__' "$STAGE_DIR/secret-migration.yaml"
echo "Secrets de aplicacao e de migration materializados a partir do Secrets Manager."

k3s kubectl apply -f "$STAGE_DIR/migration-job.yaml" >/dev/null
echo "Migration Job $JOB aplicado."

migration_ok=0
k3s kubectl -n "$NS" wait --for=condition=complete --timeout="${MIGRATION_TIMEOUT}s" "job/$JOB" >/dev/null 2>&1 || migration_ok=1

echo "----- logs do Migration Job -----"
k3s kubectl -n "$NS" logs "job/$JOB" --tail=-1 2>&1 || true
echo "----- fim dos logs -----"

if [ "$migration_ok" -ne 0 ]; then
    k3s kubectl -n "$NS" describe "job/$JOB" >&2 2>&1 || true
    echo "Migration falhou. Deployment e Service nao foram aplicados." >&2
    exit 1
fi

k3s kubectl apply -f "$STAGE_DIR/deployment.yaml" >/dev/null
k3s kubectl apply -f "$STAGE_DIR/service.yaml" >/dev/null
echo "Deployment e Service aplicados."

if ! k3s kubectl -n "$NS" rollout status "deployment/$SVC" --timeout="${ROLLOUT_TIMEOUT}s"; then
    k3s kubectl -n "$NS" describe "deployment/$SVC" >&2 2>&1 || true
    k3s kubectl -n "$NS" get pods -l "app.kubernetes.io/name=$SVC" -o wide >&2 2>&1 || true
    exit 1
fi

k3s kubectl -n "$NS" delete jobs \
    -l "app.kubernetes.io/name=$SVC,app.kubernetes.io/component=migration,oficina.io/deploy-run!=$DEPLOY_RUN_ID" \
    --ignore-not-found >/dev/null 2>&1 || true
echo "Migration Jobs anteriores removidos por label."

echo "----- capacidade do node -----"
free -m
df -h /
k3s crictl stats 2>/dev/null || true

pending="$(k3s kubectl -n "$NS" get pods --field-selector=status.phase=Pending --no-headers 2>/dev/null | wc -l)"
if [ "$pending" -ne 0 ]; then
    k3s kubectl -n "$NS" get pods --field-selector=status.phase=Pending >&2
    echo "Ha Pod em Pending: capacidade insuficiente." >&2
    exit 1
fi

for condition in MemoryPressure DiskPressure PIDPressure; do
    value="$(k3s kubectl get node -o jsonpath="{.items[0].status.conditions[?(@.type==\"$condition\")].status}")"
    echo "$condition=$value"
    if [ "$value" != "False" ]; then
        echo "Node sob $condition. Aumente k3s_instance_type." >&2
        exit 1
    fi
done

echo DEPLOY_OK
'@

$deploy = Invoke-RunCommand -InstanceId $instanceId -Comment "$serviceName deploy" `
    -ExecutionTimeoutSeconds ([int]$config.deploy.migrationTimeoutSeconds + [int]$config.deploy.rolloutTimeoutSeconds + 900) `
    -PollTimeoutSeconds ([int]$config.deploy.migrationTimeoutSeconds + [int]$config.deploy.rolloutTimeoutSeconds + 900) `
    -Script (Expand-Tokens -Text $deployScript -Tokens @{
        'STAGE_DIR'           = $stageDir
        'NAMESPACE'           = $namespace
        'SERVICE'             = $serviceName
        'SHORT_SHA'           = $shortSha
        'DEPLOY_RUN_ID'       = $deployRunId
        'MIGRATION_JOB_ID'    = $migrationJobId
        'REGION'              = $AwsRegion
        'RUNTIME_IMAGE'       = $RuntimeImage
        'MIGRATION_IMAGE'     = $MigrationImage
        'APP_SECRET_ID'       = $config.secrets.runtimeDatabase
        'MIGRATION_SECRET_ID' = $config.secrets.migrationDatabase
        'MIGRATION_TIMEOUT'   = $config.deploy.migrationTimeoutSeconds
        'ROLLOUT_TIMEOUT'     = $config.deploy.rolloutTimeoutSeconds
    })

Write-Host $deploy.StandardOutput
if ($deploy.Status -ne 'Success') {
    Write-Host $deploy.StandardError
    throw "Deploy falhou com status $($deploy.Status)."
}

# ---------------------------------------------------------------------------
# Validacao do Target Group
# ---------------------------------------------------------------------------

Write-Host 'Aguardando o Target Group ficar healthy.'
$deadline = (Get-Date).AddMinutes(10)
while ($true) {
    $health = (Invoke-Aws -Arguments @('elbv2', 'describe-target-health', '--target-group-arn', $targetGroupArn, '--region', $AwsRegion, '--output', 'json')).Output | ConvertFrom-Json
    $descriptions = @($health.TargetHealthDescriptions)
    $healthy = @($descriptions | Where-Object { $_.TargetHealth.State -eq 'healthy' })

    if ($descriptions.Count -gt 0 -and $healthy.Count -eq $descriptions.Count) {
        Write-Host "Target Group healthy ($($healthy.Count)/$($descriptions.Count))."
        break
    }
    if ((Get-Date) -gt $deadline) {
        foreach ($description in $descriptions) {
            Write-Host "  target=$($description.Target.Id):$($description.Target.Port) state=$($description.TargetHealth.State) reason=$($description.TargetHealth.Reason)"
        }
        throw 'O Target Group nao ficou healthy dentro da janela esperada.'
    }
    Start-Sleep -Seconds 15
}

Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Deploy concluido: $serviceName em $shortSha, NodePort $nodePort, namespace $namespace."
