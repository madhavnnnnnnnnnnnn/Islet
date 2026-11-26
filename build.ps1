# Builds Islet into .\dist
#   .\build.ps1             -> dist\Islet.exe           (small; needs the .NET 8 Desktop Runtime)
#   .\build.ps1 -Portable   -> dist\Islet-portable.exe  (larger; runs on any Windows 10/11 x64 PC)
param([switch]$Portable)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if ($Portable) {
    dotnet publish Islet/Islet.csproj -c Release --self-contained true -p:EnableCompressionInSingleFile=true -o out/sc
    New-Item -ItemType Directory -Force dist | Out-Null
    Copy-Item out/sc/Islet.exe dist/Islet-portable.exe -Force
    Write-Host "Built dist\Islet-portable.exe"
} else {
    dotnet publish Islet/Islet.csproj -c Release -o dist
    Write-Host "Built dist\Islet.exe"
}
