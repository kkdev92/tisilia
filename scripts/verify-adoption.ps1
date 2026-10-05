$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
$env:MSBUILDDISABLENODEREUSE = '1'
dotnet build src/backend/Tisilia.Tool -nologo
if ($LASTEXITCODE -ne 0) { throw 'adoption CLI build failed' }
dotnet build tests/fixtures/AdoptionApi -nologo
if ($LASTEXITCODE -ne 0) { throw 'adoption fixture build failed' }
node scripts/verify-adoption.mjs
if ($LASTEXITCODE -ne 0) { throw 'adoption HTTP/browser checks failed' }
