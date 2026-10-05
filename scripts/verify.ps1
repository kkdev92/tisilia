# The repository's own checks, in the order CI runs them: the npm packages are built first, because Tisilia.Explorer embeds the
# Explorer page and the Explorer's tests read the list of packages its build bundles. CI then runs scripts/install-check.ps1,
# which tries the packed packages in fresh projects.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$env:MSBUILDDISABLENODEREUSE = "1"

npm run brand:check
if ($LASTEXITCODE -ne 0) { throw "brand asset check failed" }

Write-Host "== npm run build (runtime → nuxt module → explorer SPA)"
npm run build
if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }

Write-Host "== dotnet build src/backend/Tisilia.slnx (embeds src/frontend/explorer/dist into Tisilia.Explorer)"
dotnet build src/backend/Tisilia.slnx -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

Write-Host "== dotnet test src/backend/Tisilia.slnx"
dotnet test src/backend/Tisilia.slnx --no-build -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }

Write-Host "== npm run typecheck (sources and tests of every package; the Explorer's templates through vue-tsc; compile-time tests such as nuxt/test/config-types.ts)"
npm run typecheck
if ($LASTEXITCODE -ne 0) { throw "npm run typecheck failed" }

Write-Host "== npm test (runtime, nuxt module, explorer; frozen fixtures under tests/fixtures)"
npm test
if ($LASTEXITCODE -ne 0) { throw "npm test failed" }

Write-Host "== adoption: Kestrel, generated client, interpreter, Chromium/Firefox/WebKit"
& (Join-Path $PSScriptRoot 'verify-adoption.ps1')
if ($LASTEXITCODE -ne 0) { throw 'adoption checks failed' }
Write-Host "verify passed"
