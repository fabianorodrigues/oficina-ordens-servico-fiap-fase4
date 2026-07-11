$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workflowDir = Join-Path $root '.github/workflows'
if (-not (Test-Path -LiteralPath $workflowDir)) { throw 'Diretorio .github/workflows ausente.' }

$repo = Split-Path $root -Leaf
$contracts = @{
  'oficina-cadastro-fiap-fase4' = @{ Ci = 'Cadastro CI'; Manuals = @{ 'Cadastro Deploy' = 'DEPLOY' } }
  'oficina-estoque-fiap-fase4' = @{ Ci = 'Estoque CI'; Manuals = @{ 'Estoque Deploy' = 'DEPLOY' } }
  'oficina-ordens-servico-fiap-fase4' = @{ Ci = 'Ordens CI'; Manuals = @{ 'Ordens Deploy' = 'DEPLOY' } }
  'oficina-auth-lambda-fiap-fase4' = @{ Ci = 'Auth CI'; Manuals = @{ 'Auth Deploy' = 'DEPLOY'; 'Auth JWT Secret Sync' = 'SYNC' } }
  'oficina-infra-db-fiap-fase4' = @{ Ci = 'Infra DB CI'; Manuals = @{ 'Backend Terraform Bootstrap' = 'CREATE'; 'Infra DB Deploy' = 'APPLY'; 'Database Secrets Sync' = 'SYNC'; 'Database Bootstrap Deploy' = 'BOOTSTRAP' } }
  'oficina-infra-fiap-fase4' = @{ Ci = 'Platform CI'; Manuals = @{ 'Platform Deploy' = 'APPLY'; 'Ingress Deploy' = 'DEPLOY'; 'Entrypoint Deploy' = 'APPLY'; 'Observability Secret Sync' = 'SYNC'; 'Observability Validate' = 'VALIDATE' } }
}
if (-not $contracts.ContainsKey($repo)) { throw "Repositorio sem contrato esperado: $repo" }

$errors = New-Object System.Collections.Generic.List[string]
function Add-Err([string]$Message) { $script:errors.Add($Message) }
function Read-Workflow([string]$Path) { Get-Content -LiteralPath $Path -Raw }
function Has([string]$Text, [string]$Pattern) { return $Text -match $Pattern }

$files = @(Get-ChildItem -LiteralPath $workflowDir -Filter '*.yml' -File)
if ($files.Count -eq 0) { Add-Err 'Nenhum workflow YAML encontrado.' }
$joined = ($files | ForEach-Object { Read-Workflow $_.FullName }) -join "`n---`n"
$blockedRollback = 'roll' + 'back'
$blockedDestroy = 'des' + 'troy'
$blockedLatest = ':lat' + 'est'
foreach ($rule in @(
  @{ Pattern = 'pull_request_target'; Label = 'pull_request_target' },
  @{ Pattern = 'continue-on-error:\s*true'; Label = 'continue-on-error true' },
  @{ Pattern = '(?m)^\s*environment\s*:'; Label = 'GitHub Environment' },
  @{ Pattern = '(?m)^\s*push\s*:'; Label = 'deploy/CI por push' },
  @{ Pattern = 'permissions:\s*write-all'; Label = 'permissions write-all' },
  @{ Pattern = 'kubectl\s+rollout\s+undo'; Label = 'kubectl rollout undo' },
  @{ Pattern = "terraform\s+$blockedDestroy"; Label = 'terraform destroy' },
  @{ Pattern = 'aws\s+delete-'; Label = 'aws delete' },
  @{ Pattern = 'set\s+-x'; Label = 'set -x' },
  @{ Pattern = 'printenv'; Label = 'printenv' },
  @{ Pattern = 'Get-ChildItem\s+Env:'; Label = 'Get-ChildItem Env' },
  @{ Pattern = $blockedLatest; Label = 'imagem latest' }
)) {
  if (Has $joined $rule.Pattern) { Add-Err "Padrao proibido em workflows: $($rule.Label)" }
}
if ($files.Name -match "(^|[-])$blockedRollback\.yml$") { Add-Err 'Workflow dedicado de rollback encontrado.' }
if ($files.Name -match "$blockedDestroy\.yml$|terraform-$blockedDestroy") { Add-Err 'Workflow dedicado de destroy encontrado.' }
if (Has $joined "(?i)\b$blockedRollback\b|ROLLBACK") { Add-Err 'Referencia de rollback encontrada em workflow.' }

$contract = $contracts[$repo]
$ciFiles = @()
foreach ($file in $files) {
  $text = Read-Workflow $file.FullName
  $nameMatch = [regex]::Match($text, '(?m)^name:\s*(.+?)\s*$')
  if (-not $nameMatch.Success) { Add-Err "$($file.Name): name ausente."; continue }
  $name = $nameMatch.Groups[1].Value.Trim('"'' ')
  if (-not (Has $text '(?m)^permissions:\s*\r?\n\s+contents:\s*read')) { Add-Err "${name}: permissions contents: read ausente." }
  if (-not (Has $text 'timeout-minutes:')) { Add-Err "${name}: timeout-minutes ausente." }
  if ($name -eq $contract.Ci) {
    $ciFiles += $file.Name
    if (-not (Has $text '(?m)^\s*pull_request\s*:')) { Add-Err "${name}: CI deve executar em pull_request." }
    if (-not (Has $text '(?m)^\s*branches:\s*(\[[^\]]*\bmain\b[^\]]*\]|\r?\n\s*-\s*main)')) { Add-Err "${name}: CI deve mirar main." }
    if (Has $text 'workflow_dispatch') { Add-Err "${name}: CI principal nao deve depender de workflow_dispatch." }
    if (Has $text 'aws-actions/configure-aws-credentials|secrets\.AWS_ACCESS_KEY_ID|terraform\s+apply|kubectl\s+apply(?!\s+--dry-run=client)|helm\s+upgrade|docker\s+push|aws\s+(put|create|update|delete)-') { Add-Err "${name}: CI contem credencial AWS ou comando mutavel." }
  }
  elseif ($contract.Manuals.ContainsKey($name)) {
    $expected = $contract.Manuals[$name]
    if (-not (Has $text '(?m)^\s*workflow_dispatch\s*:')) { Add-Err "${name}: workflow mutavel deve ser manual." }
    if (Has $text '(?m)^\s*pull_request\s*:|(?m)^\s*push\s*:') { Add-Err "${name}: workflow mutavel nao pode rodar em PR/push." }
    if (-not (Has $text 'refs/heads/main')) { Add-Err "${name}: validacao de branch main ausente." }
    if (-not (Has $text 'confirmation:')) { Add-Err "${name}: input confirmation ausente." }
    if (-not (Has $text ([regex]::Escape($expected)))) { Add-Err "${name}: confirmacao esperada $expected ausente." }
    if (-not (Has $text '(?m)^concurrency:\s*\r?\n\s+group:\s*[-a-z0-9]+')) { Add-Err "${name}: concurrency ausente." }
  }
  else { Add-Err "$($file.Name): workflow nao esperado pelo contrato: $name" }
}
if ($ciFiles.Count -ne 1) { Add-Err "Quantidade invalida de CI principal: $($ciFiles.Count)." }
foreach ($manualName in $contract.Manuals.Keys) {
  if (-not (Has $joined "(?m)^name:\s*$([regex]::Escape($manualName))\s*$")) { Add-Err "Workflow manual esperado ausente: $manualName" }
}
if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_ }; exit 1 }
Write-Host "Workflow contract OK for $repo."
