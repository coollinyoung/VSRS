# 發行入口：必須使用 dotnet publish，普通 build 並非自包含部署結果。
# $PSScriptRoot 讓腳本可從任意目前工作目錄執行。
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\VSRS\VSRS.csproj'
$output = Join-Path $PSScriptRoot '..\publish\win-x64'

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host '完成：publish\win-x64\VSRS.exe'
Write-Host '這是內含 .NET 8 Runtime 的 Windows Forms x64 單檔程式。'
