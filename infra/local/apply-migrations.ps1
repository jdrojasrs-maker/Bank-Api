# Aplica las migraciones de EF Core a las bases de Transaction, Balance y Transfer.
# Ejecutar con SQL Server levantado (docker compose up -d) y tras cada "docker compose down",
# porque el contenedor de SQL no tiene volumen y pierde las bases.
# Requisito: user-secrets con TRANSACTIONSQLDBCONSTR / BALANCESQLDBCONSTR / TRANSFERSQLDBCONSTR.

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root

dotnet tool restore | Out-Null

foreach ($service in 'Transaction', 'Balance', 'Transfer') {
    Write-Host "== $service" -ForegroundColor Cyan
    dotnet tool run dotnet-ef database update --project "src/Bank.$service/Bank.$service.Api"
    if ($LASTEXITCODE -ne 0) { throw "Fallo la migracion de $service" }
}

Write-Host "Migraciones aplicadas." -ForegroundColor Green
