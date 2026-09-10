param(
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\src\VSRS\bin\x64\Release\net48'
if (-not (Test-Path (Join-Path $source 'VSRS.exe'))) { throw '請先執行 Build-Release.ps1。' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item (Join-Path $source '*') $Destination -Recurse -Force
Write-Host "已複製到 $Destination"
