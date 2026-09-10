$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '..\VSRS.sln'
dotnet build $solution -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host '完成：src\VSRS\bin\x64\Release\net48\VSRS.exe'
