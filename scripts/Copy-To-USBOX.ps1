# 部署到 USBOX 指定目錄；Tools 需由使用者補入官方工具，此腳本不下載它們。
# -Force 會覆蓋同名程式，執行前應關閉目的地的 VSRS。
param(
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\publish\win-x64'
if (-not (Test-Path (Join-Path $source 'VSRS.exe'))) { throw '請先執行 Build-Release.ps1 產生自包含單檔版本。' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item (Join-Path $source 'VSRS.exe') $Destination -Force

$tools = Join-Path $PSScriptRoot '..\Tools'
if (Test-Path $tools) {
    Copy-Item $tools $Destination -Recurse -Force
}
Write-Host "已複製自包含 VSRS.exe 與 Tools 到 $Destination"
