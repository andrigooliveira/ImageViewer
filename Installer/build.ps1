# Builds the MSI installer (self-contained: includes the .NET runtime, no prerequisites).
# Also builds dist\ImageViewer.exe, the small single-file build that needs the .NET 9 Desktop Runtime.
# Requirements: .NET 9 SDK and the WiX v5 tool:
#   dotnet tool install --global wix --version 5.0.2
#   wix extension add --global WixToolset.UI.wixext/5.0.2
param(
    [string]$RepoUrl = "https://github.com/andrigooliveira/ImageViewer"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = "$root\publish"

[xml]$proj = Get-Content "$root\ImageViewer.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version

dotnet publish "$root\ImageViewer.csproj" -c Release -o "$root\dist"
if ($LASTEXITCODE) { exit $LASTEXITCODE }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$root\ImageViewer.csproj" -c Release --self-contained true -p:PublishSingleFile=false -o $publish
if ($LASTEXITCODE) { exit $LASTEXITCODE }

Push-Location $PSScriptRoot
try {
    wix build ImageViewer.wxs -arch x64 -culture pt-BR -ext WixToolset.UI.wixext `
        -d Version=$version -d "RepoUrl=$RepoUrl" -d "PublishDir=$publish" `
        -o "$root\dist\VisualizadorDeImagens-$version-x64.msi"
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
Write-Host "Instalador: $root\dist\VisualizadorDeImagens-$version-x64.msi"
