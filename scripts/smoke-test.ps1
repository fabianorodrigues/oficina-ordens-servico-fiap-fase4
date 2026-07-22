param(
    [Parameter(Mandatory = $true)][string]$BaseUrl
)

$ErrorActionPreference = "Stop"
$base = $BaseUrl.TrimEnd("/")

foreach ($path in @("/health", "/ready")) {
    $response = Invoke-WebRequest -Uri "$base$path" -Method Get -TimeoutSec 10
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "$path retornou HTTP $($response.StatusCode)."
    }
}

Write-Host "Smoke test OK."
