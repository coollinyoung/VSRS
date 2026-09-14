$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\VSRS\VSRS.csproj'
$output = Join-Path $PSScriptRoot '..\publish\win-x64'

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host '完成：publish\win-x64\VSRS.exe'
Write-Host '這是內含 .NET 8 Runtime 的 Windows Forms x64 單檔程式。'
