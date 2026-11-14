$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.runtime'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
Write-Host 'SICKO DISTRICT | http://localhost:5080' -ForegroundColor White
Write-Host 'Admin local: admin@sicko.local | District@2026' -ForegroundColor Gray
dotnet run --project SickoDistrict.csproj --urls http://localhost:5080
