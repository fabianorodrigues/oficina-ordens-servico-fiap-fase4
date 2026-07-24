param(
    [Parameter(Mandatory = $true)][string]$BaseUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:Base = $BaseUrl.TrimEnd('/')

function Test-SmokeEndpoint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedStatus
    )

    $uri = "$script:Base$Path"
    try {
        $response = Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 10 -MaximumRedirection 0 -UseBasicParsing -ErrorAction Stop
    }
    catch {        
        $resp = $null
        $respProp = $_.Exception.PSObject.Properties['Response']
        if ($respProp) { $resp = $respProp.Value }
        if ($resp) {
            $code = $null
            try { $code = [int]$resp.StatusCode } catch { $code = $null }
            throw "[app] $Path respondeu HTTP $code (esperado 200)."
        }
        throw "[infra] Nao foi possivel acessar ${uri}: $($_.Exception.Message)"
    }

    if ([int]$response.StatusCode -ne 200) {
        throw "[app] $Path respondeu HTTP $($response.StatusCode) (esperado 200)."
    }

    $status = $null
    try { $status = ($response.Content | ConvertFrom-Json).status } catch { $status = $null }
    if ($status -ne $ExpectedStatus) {
        throw "[app] $Path retornou corpo invalido (status='$status', esperado '$ExpectedStatus')."
    }

    Write-Host "  OK  $Path -> $status"
}

Test-SmokeEndpoint -Path "/health" -ExpectedStatus "Healthy"
Test-SmokeEndpoint -Path "/ready" -ExpectedStatus "Ready"

Write-Host "Smoke test de ordens concluido."
