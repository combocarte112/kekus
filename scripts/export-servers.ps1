# Export CS 1.6 server list from msboost backend for GoldSrcProbe servers.txt
# Usage: powershell -File scripts\export-servers.ps1 [-BackendUrl URL] [-OutFile path]

param(
    [string]$BackendUrl = "https://msboost.eu/backend/servers.php",
    [string]$OutFile = ""
)

$root = Split-Path $PSScriptRoot -Parent
if ($OutFile -eq "") {
    $OutFile = Join-Path $root "servers.txt"
}

Write-Host "Fetching: $BackendUrl"
try {
    $lines = (Invoke-WebRequest -Uri $BackendUrl -UseBasicParsing -TimeoutSec 30).Content -split "`n"
} catch {
    Write-Error "Failed to fetch server list: $_"
    exit 1
}

$servers = @()
foreach ($line in $lines) {
    $line = $line.Trim()
    if ($line -match '^\d+\.\d+\.\d+\.\d+:\d+$') {
        $servers += $line
    }
}

if ($servers.Count -eq 0) {
    Write-Error "No ip:port lines found"
    exit 1
}

$header = @(
    "# Exported from msboost $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
    "# Count: $($servers.Count)",
    ""
)
($header + $servers) | Set-Content -Path $OutFile -Encoding UTF8
Write-Host "Wrote $($servers.Count) servers -> $OutFile"
