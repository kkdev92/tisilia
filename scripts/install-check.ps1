# Clean-install check: the packed artifacts are installed into fresh projects outside the repository and used without any
# project reference.
#   NuGet : dotnet pack → local feed → `dotnet new web` app referencing Kkdev92.Tisilia.AspNetCore / Explorer → dotnet build;
#           the CLI as a tool-path tool from the feed → `tisilia export` of that fresh app → `tisilia validate` of the contract
#   npm   : npm pack → a fresh package installs @kkdev92/tisilia-runtime from the tarball plus typescript@6; the CLI generates a
#           client for the fresh app into it (init, generate, check) → tsc under strict settings → node: the runtime alone, then the
#           generated client calling the fresh app over HTTP (an int64 beyond 2^53 and a +09:00 offset must arrive exactly)
#           Explorer is also rebuilt outside the repository using the packed runtime; its CLI bundle digests include the artwork.
# Network: nuget.org / npmjs.com for the third-party dependencies of the fresh projects (the shared framework is local).
param(
  # The version the packages are packed with; by default the one the build files declare.
  [string] $Version = "",
  # A directory that already holds the .nupkg and .tgz files to check (the release's own artifacts); packed here when absent.
  [string] $PackagesFrom = "",
  [switch] $KeepWorkspace
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$env:MSBUILDDISABLENODEREUSE = "1"
$env:DOTNET_CLI_UI_LANGUAGE = "en"
node (Join-Path $PSScriptRoot 'sync-brand-assets.mjs') --check
if ($LASTEXITCODE -ne 0) { throw 'Brand asset validation failed' }
if (-not $Version) {
  $Version = (dotnet msbuild src/backend/Tisilia.Generator/Tisilia.Generator.csproj -getProperty:PackageVersion -nologo | Out-String).Trim()
}
Write-Host "version: $Version"
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("tisilia-install-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
Write-Host "workspace: $work"
# A global packages folder of this run only: NuGet takes a package that is already in the global packages folder without asking any
# source, so with the shared ~/.nuget/packages the fresh app would restore a Kkdev92.Tisilia.* package of the same version packed
# earlier and the check would pass against stale code. Third-party packages still come from the HTTP cache or nuget.org.
$env:NUGET_PACKAGES = Join-Path $work "nuget-packages"

# The entries of a .tgz, read with .NET: a `tar` found on the PATH may be GNU tar (Git for Windows), which takes "C:\…" for host:path.
function TarballEntries([string] $path) {
  $file = [System.IO.File]::OpenRead($path)
  try {
    $reader = [System.Formats.Tar.TarReader]::new([System.IO.Compression.GZipStream]::new($file, [System.IO.Compression.CompressionMode]::Decompress))
    $names = [System.Collections.Generic.List[string]]::new()
    while ($null -ne ($entry = $reader.GetNextEntry())) { $names.Add($entry.Name) }
    return $names
  } finally {
    $file.Dispose()
  }
}

function Step([string] $name, [scriptblock] $body) {
  Write-Host "== $name"
  & $body
  if ($LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)" }
}

try {
  # ---------------------------------------------------------------- NuGet packages → local feed
  $feed = Join-Path $work "feed"
  New-Item -ItemType Directory -Path $feed | Out-Null
  if ($PackagesFrom) {
    Copy-Item -Path (Join-Path $PackagesFrom "*.nupkg") -Destination $feed
    Write-Host "packages from $PackagesFrom"
  } else {
    foreach ($proj in @("src/backend/Tisilia.Abstractions", "src/backend/Tisilia.Generator", "src/backend/Tisilia.AspNetCore", "src/backend/Tisilia.Explorer", "src/backend/Tisilia.Tool")) {
      Step "pack $proj" { dotnet pack $proj -c Release -o $feed -nologo -v q }
    }
  }
  foreach ($id in @("Kkdev92.Tisilia.Abstractions", "Kkdev92.Tisilia.Generator", "Kkdev92.Tisilia.AspNetCore", "Kkdev92.Tisilia.Explorer", "Kkdev92.Tisilia.Tool")) {
    $nupkg = Join-Path $feed "$id.$Version.nupkg"
    if (-not (Test-Path $nupkg)) { throw "missing package $id.$Version.nupkg in $feed" }
    # what every package carries besides its code: the readme nuget.org shows, the license text and NOTICE
    $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
      $entries = @($zip.Entries | ForEach-Object { $_.FullName })
      foreach ($required in @("README.md", "NOTICE", "LICENSE", "nuget-icon.jpg", "BRAND-ASSET-POLICY.md")) {
        if ($entries -notcontains $required) { throw "$id.$Version.nupkg has no $required" }
      }
      $nuspecReader = [IO.StreamReader]::new(($zip.Entries | Where-Object FullName -Like '*.nuspec').Open())
      try { [xml] $nuspec = $nuspecReader.ReadToEnd() } finally { $nuspecReader.Dispose() }
      if ($nuspec.package.metadata.icon -ne 'nuget-icon.jpg') { throw "$id has an incorrect nuspec icon" }
      if (@($entries | Where-Object { $_ -eq 'nuget-icon.jpg' }).Count -ne 1) { throw "$id has duplicate icons" }
      $icon = $zip.GetEntry('nuget-icon.jpg').Open()
      try { $iconHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($icon)) } finally { $icon.Dispose() }
      if ($iconHash -ne (Get-FileHash (Join-Path $root 'assets/brand/tisilia/icons/nuget-icon.jpg')).Hash) { throw "$id changed the JPEG" }
      $policyStream = $zip.GetEntry('BRAND-ASSET-POLICY.md').Open()
      try { $policyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($policyStream)) } finally { $policyStream.Dispose() }
      if ($policyHash -ne (Get-FileHash (Join-Path $root 'assets/brand/tisilia/BRAND-ASSET-POLICY.md')).Hash) { throw "$id changed the brand policy" }
      if ($nuspec.package.metadata.license.'#text' -ne 'MIT') { throw "$id changed the code license" }
      if ($entries | Where-Object { $_ -match '(?i)(mascot-|banner|social|ASSET_PREVIEW|CODEX_TASK)' }) { throw "$id contains unexpected brand source files" }
    } finally {
      $zip.Dispose()
    }
  }

  # ---------------------------------------------------------------- a fresh ASP.NET Core app that only knows the packages
  $app = Join-Path $work "app"
  Step "dotnet new web" { dotnet new web -o $app --no-restore }
  @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="tisilia-local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -Path (Join-Path $app "nuget.config") -Encoding utf8
  Step "add package Kkdev92.Tisilia.AspNetCore" { dotnet add $app package Kkdev92.Tisilia.AspNetCore --version $Version }
  Step "add package Kkdev92.Tisilia.Explorer" { dotnet add $app package Kkdev92.Tisilia.Explorer --version $Version }
  @"
using Tisilia.AspNetCore;
using Tisilia.Explorer;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTisilia(o => o.ApiId = "install-check");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://127.0.0.1:4181").AllowAnyMethod().AllowAnyHeader()));
var app = builder.Build();
app.UseCors();
var downloads = 0;
app.MapGet("/test-counts", () => new { downloads });
app.MapGet("/ping/{id:guid}", (Guid id) => new Pong(id, 9007199254740993L, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(9))))
    .WithTisiliaOperation("ping.get", "ping");
app.MapGet("/optional/{id?}", (string? id) => new { value = id }).WithTisiliaOperation("route.optional");
app.MapGet("/default/{page=1}", (int page) => new { value = page }).WithTisiliaOperation("route.default");
app.MapGet("/files/{**path}", (string? path) => new { value = path }).WithTisiliaOperation("route.files");
app.MapGet("/download", () => { Interlocked.Increment(ref downloads); return Results.File(new byte[] { 0, 255, 1, 195, 40 }, "application/pdf", "packed.pdf"); })
    .Produces<FileContentResult>(200, "application/pdf").WithTisiliaOperation("file.get");
if (app.Environment.IsDevelopment())
{
    app.MapTisiliaContract();
    app.MapTisiliaExplorer();
}

app.Run();

/// <summary>A pong.</summary>
/// <param name="Revision">The revision, beyond 2^53.</param>
public sealed record Pong(Guid Id, long Revision, DateTimeOffset At);

public sealed record Undocumented(int Value);
"@ | Set-Content -Path (Join-Path $app "Program.cs") -Encoding utf8
  # the package turns on the XML documentation file for the app (build/Kkdev92.Tisilia.AspNetCore.Documentation.targets): built with
  # warnings as errors, an undocumented public type must not fail the build, and the comments must reach the contract
  Step "build the fresh app (warnings as errors)" { dotnet build $app -nologo -v q -p:TreatWarningsAsErrors=true }
  if (-not (Test-Path (Join-Path $app "bin/Debug/net10.0/app.xml"))) { throw "the fresh app has no XML documentation file (bin/Debug/net10.0/app.xml)" }
  # the fresh app must run the packages packed above, not another copy of the same version
  $restored = Join-Path $env:NUGET_PACKAGES "kkdev92.tisilia.aspnetcore/$Version/lib/net10.0/Tisilia.AspNetCore.dll"
  $package = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $feed "Kkdev92.Tisilia.AspNetCore.$Version.nupkg"))
  try {
    $stream = $package.GetEntry("lib/net10.0/Tisilia.AspNetCore.dll").Open()
    try { $packedHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
  } finally {
    $package.Dispose()
  }
  if (-not (Test-Path $restored) -or (Get-FileHash $restored -Algorithm SHA256).Hash -ne $packedHash) { throw "the fresh app did not restore the Tisilia.AspNetCore.dll packed by this run ($restored)" }

  # ---------------------------------------------------------------- the CLI from the feed, used against the fresh app
  $tools = Join-Path $work "tools"
  Step "install the tool from the feed" { dotnet tool install Kkdev92.Tisilia.Tool --tool-path $tools --add-source $feed --version $Version }
  $tisilia = Join-Path $tools "tisilia"
  $contract = Join-Path $app "tisilia.contract.json"
  Step "tisilia export (fresh app)" { & $tisilia export --project $app --allow-execute-project --no-build --output $contract }
  Step "tisilia validate (exported contract)" { & $tisilia validate --contract $contract }
  $exported = Get-Content -Raw $contract | ConvertFrom-Json
  if (($exported.operations | Where-Object { $_.id -eq "ping.get" } | Measure-Object).Count -ne 1) { throw "the exported contract has no ping.get operation" }
  $pong = $exported.documentation | Where-Object { $_.summary -eq "A pong." }
  if (($pong | Measure-Object).Count -lt 1 -or -not $pong[0].description.Contains('- `revision` — The revision, beyond 2^53.')) { throw "the exported contract lacks the XML comments of Pong: $($exported.documentation | ConvertTo-Json -Compress)" }
  Write-Host ("exported " + $exported.operations.Count + " operation(s), " + $exported.documentation.Count + " documentation entries, semanticHash " + $exported.semanticHash)

  # ---------------------------------------------------------------- npm packages → tarballs → a fresh consumer
  $npm = Join-Path $work "npm"
  New-Item -ItemType Directory -Path $npm | Out-Null
  foreach ($pkg in @("src/frontend/runtime", "src/frontend/nuxt", "src/frontend/explorer")) {
    if ($PackagesFrom) { continue }
    Step "npm pack $pkg" { npm pack --workspace $pkg --pack-destination $npm --silent }
  }
  if ($PackagesFrom) { Copy-Item -Path (Join-Path $PackagesFrom "*.tgz") -Destination $npm }
  $runtimeTgz = Get-ChildItem $npm -Filter "kkdev92-tisilia-runtime-*.tgz" | Select-Object -First 1
  if (-not $runtimeTgz) { throw "runtime tarball missing in $npm" }
  foreach ($name in @("runtime", "nuxt", "explorer")) {
    $tgz = Get-ChildItem $npm -Filter "kkdev92-tisilia-$name-$Version.tgz" | Select-Object -First 1
    if (-not $tgz) { throw "kkdev92-tisilia-$name-$Version.tgz missing in $npm" }
    $listing = TarballEntries $tgz.FullName
    foreach ($required in @("package/package.json", "package/README.md", "package/LICENSE")) {
      if (-not ($listing -contains $required)) { throw "the $name tarball has no $required" }
    }
    if ($name -eq "explorer") {
      foreach ($required in @("package/NOTICE", "package/third-party-sources.json", "package/public/BRAND-ASSET-POLICY.md", "package/src/assets/brand/tisilia/mascot-chibi-light.jpg", "package/src/assets/brand/tisilia/mascot-chibi-dark.jpg")) {
        if (-not ($listing -contains $required)) { throw "the Explorer tarball has no $required" }
      }
    }
    Write-Host ("$name tarball: " + ($listing | Measure-Object).Count + " files")
    if ($name -ne 'explorer' -and ($listing | Where-Object { $_ -match '(?i)\.(jpg|jpeg)$' })) { throw "$name contains unnecessary artwork" }
    if ($name -ne 'explorer' -and ($listing | Where-Object { $_ -like '*BRAND-ASSET-POLICY.md' })) { throw "$name contains an unnecessary brand policy" }
  }

  # Rebuild the actual shipped Explorer sources with the local runtime tarball, without access to monorepo assets/config.
  $standalone = Join-Path $work 'standalone'
  New-Item -ItemType Directory -Path $standalone | Out-Null
  $explorerTgz = Join-Path $npm "kkdev92-tisilia-explorer-$Version.tgz"
  $archive = [IO.File]::OpenRead($explorerTgz)
  try {
    $gzip = [IO.Compression.GZipStream]::new($archive, [IO.Compression.CompressionMode]::Decompress)
    try { [System.Formats.Tar.TarFile]::ExtractToDirectory($gzip, $standalone, $false) } finally { $gzip.Dispose() }
  } finally { $archive.Dispose() }
  $explorerSource = Join-Path $standalone 'package'
  $canonicalPolicyHash = (Get-FileHash (Join-Path $root 'assets/brand/tisilia/BRAND-ASSET-POLICY.md')).Hash.ToLowerInvariant()
  if ((Get-FileHash (Join-Path $explorerSource 'public/BRAND-ASSET-POLICY.md')).Hash.ToLowerInvariant() -ne $canonicalPolicyHash) { throw 'tarball changed the brand policy' }
  foreach ($theme in @('light', 'dark')) {
    $name = "mascot-chibi-$theme.jpg"
    if ((Get-FileHash (Join-Path $explorerSource "src/assets/brand/tisilia/$name")).Hash -ne
        (Get-FileHash (Join-Path $root "assets/brand/tisilia/illustrations/$name")).Hash) { throw "tarball changed $name" }
  }
  Push-Location $explorerSource
  try {
    Step 'install standalone Explorer with the locally packed runtime' { npm install --no-audit --no-fund $runtimeTgz.FullName }
    Step 'build standalone Explorer' { npm run build }
  } finally { Pop-Location }
  $registry = Join-Path $standalone 'registry.json'
  '{ "format": "tisilia.explorer-registry", "version": "0.3", "explorer": "package", "modules": [] }' | Set-Content -LiteralPath $registry
  $bundle = Join-Path $standalone 'bundle'
  Step 'tisilia explorer build (standalone package)' { & $tisilia explorer build --registry $registry --allow-execute-build --output $bundle }
  $bundleManifest = Get-Content -Raw (Join-Path $bundle 'tisilia.explorer-bundle.json') | ConvertFrom-Json
  $policyEntries = @($bundleManifest.files | Where-Object { $_.path -eq 'BRAND-ASSET-POLICY.md' })
  if ($policyEntries.Count -ne 1 -or $policyEntries[0].digest -ne "sha256:$canonicalPolicyHash" -or
      (Get-FileHash (Join-Path $bundle 'BRAND-ASSET-POLICY.md')).Hash.ToLowerInvariant() -ne $canonicalPolicyHash) { throw 'bundle policy or digest mismatch' }
  $images = @($bundleManifest.files | Where-Object { $_.path -match '\.jpg$' })
  if ($images.Count -ne 2) { throw 'Explorer bundle must contain exactly two JPEGs' }
  foreach ($image in $images) {
    $actualHash = (Get-FileHash (Join-Path $bundle $image.path)).Hash.ToLowerInvariant()
    if ($image.digest -ne "sha256:$actualHash") { throw "bundle digest mismatch: $($image.path)" }
    $theme = if ($image.path -match '-dark-') { 'dark' } else { 'light' }
    if ($actualHash -ne (Get-FileHash (Join-Path $root "assets/brand/tisilia/illustrations/mascot-chibi-$theme.jpg")).Hash.ToLowerInvariant()) { throw 'bundle changed artwork' }
  }

  # ---------------------------------------------------------------- a fresh TypeScript project: the packed runtime and a generated client
  $consumer = Join-Path $work "consumer"
  New-Item -ItemType Directory -Path $consumer | Out-Null
  Set-Content -Path (Join-Path $consumer "package.json") -Value '{ "name": "tisilia-install-check", "private": true, "type": "module" }' -Encoding utf8

  # the config sits above the app and the consumer: its paths may not leave its directory
  $config = Join-Path $work "tisilia.json"
  Step "tisilia init (nodenext)" { & $tisilia init --contract $contract --output (Join-Path $consumer "src/api") --config $config --module-mode nodenext }
  Step "tisilia generate" { & $tisilia generate --config $config }
  Step "tisilia check (up to date)" { & $tisilia check --config $config }

  @'
import { createCodecContext, decimalFromString, formatDecimal, int64, parseJson, scalarCodec, writeJson } from "@kkdev92/tisilia-runtime";

const context = createCodecContext();
const revision = scalarCodec<bigint>("int64").decodeResponse!(parseJson("9007199254740993"), context);
if (revision !== int64(9007199254740993n)) {
  throw new Error("int64 precision lost through the packaged runtime");
}
const amount = formatDecimal(decimalFromString("1.2500"));
if (amount !== "1.2500") {
  throw new Error("decimal scale lost through the packaged runtime: " + amount);
}
console.log(JSON.stringify({ revision: revision.toString(), amount, wire: writeJson(parseJson('{"n":1.0,"big":9007199254740993}')) }));
'@ | Set-Content -Path (Join-Path $consumer "src/check.ts") -Encoding utf8

  # the generated client against the fresh app over HTTP: the revision is beyond 2^53 and the offset is +09:00
  @'
import { formatDateTimeOffset, guid, int64 } from "@kkdev92/tisilia-runtime";
import { createInstallCheckClient } from "./api/index.js";

const baseUrl = process.env["TISILIA_INSTALL_CHECK_URL"];
if (baseUrl === undefined) {
  throw new Error("TISILIA_INSTALL_CHECK_URL is not set");
}
const client = createInstallCheckClient({ baseUrl });
const result = await client.pingGet({ id: guid("0f8fad5b-d9cb-469f-a165-70867728950e") });
if (result.kind !== "response") {
  throw new Error("ping.get failed: " + JSON.stringify(result, (_, v) => (typeof v === "bigint" ? v.toString() : v)));
}
if (result.data.revision !== int64(9007199254740993n)) {
  throw new Error("the revision lost precision: " + String(result.data.revision));
}
if (formatDateTimeOffset(result.data.at) !== "2026-10-01T00:00:00+09:00") {
  throw new Error("the offset did not survive: " + formatDateTimeOffset(result.data.at));
}
console.log(JSON.stringify({ status: result.status, revision: result.data.revision.toString(), at: formatDateTimeOffset(result.data.at) }));
const omitted = await client.routeOptional();
if (omitted.kind !== "response" || omitted.data.value !== null) throw new Error("packed optional route failed");
const page = await client.routeDefault();
if (page.kind !== "response" || page.data.value !== 1) throw new Error("packed route default failed");
const path = await client.routeFiles({ path: "a/日本😀/%2e" });
if (path.kind !== "response" || path.data.value !== "a/日本😀/%2e") throw new Error("packed catch-all failed");
const file = await client.fileGet();
if (file.kind !== "response" || JSON.stringify([...file.data.bytes]) !== "[0,255,1,195,40]" || file.data.suggestedFileName !== "packed.pdf") throw new Error("packed file failed");
console.log("packed P0 optional/default/catch-all/file passed");
'@ | Set-Content -Path (Join-Path $consumer "src/call.ts") -Encoding utf8

  # the compiler settings the generated code is promised to compile under
  @'
{
  "compilerOptions": {
    "module": "NodeNext", "moduleResolution": "NodeNext", "target": "ES2022", "types": ["node"], "outDir": "dist", "rootDir": "src",
    "strict": true, "exactOptionalPropertyTypes": true, "noUncheckedIndexedAccess": true, "verbatimModuleSyntax": true,
    "noUnusedLocals": true, "noUnusedParameters": true, "erasableSyntaxOnly": true, "isolatedDeclarations": true, "declaration": true
  },
  "include": ["src"]
}
'@ | Set-Content -Path (Join-Path $consumer "tsconfig.json") -Encoding utf8

  Push-Location $consumer
  try {
    $nuxtTgz = Join-Path $npm "kkdev92-tisilia-nuxt-$Version.tgz"
    Step "npm install (runtime and Nuxt tarballs + pinned compiler/framework)" { npm install --no-audit --no-fund --silent $runtimeTgz.FullName $nuxtTgz "typescript@6.0.3" "@types/node@24" "nuxt@4.5.2" }
    Step "tsc: the generated client and the checks, against the packaged types" { npx tsc -p tsconfig.json }
    Step "node against the packaged runtime" { node dist/check.js }
  } finally {
    Pop-Location
  }

  # the fresh app on a free loopback port, the compiled client calling it
  $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
  $probe.Start()
  $port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port
  $probe.Stop()
  $url = "http://127.0.0.1:$port"
  $windowOptions = if ($IsWindows) { @{ WindowStyle = 'Hidden' } } else { @{} }
  $server = Start-Process -FilePath "dotnet" -ArgumentList @((Join-Path $app "bin/Debug/net10.0/app.dll"), "--urls", $url) -WorkingDirectory $app `
    -RedirectStandardOutput (Join-Path $work "app.out.log") -RedirectStandardError (Join-Path $work "app.err.log") @windowOptions -PassThru
  try {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $ready = $false
    while (-not $ready -and [DateTime]::UtcNow -lt $deadline -and -not $server.HasExited) {
      try {
        Invoke-WebRequest -Uri "$url/ping/0f8fad5b-d9cb-469f-a165-70867728950e" -UseBasicParsing -TimeoutSec 5 | Out-Null
        $ready = $true
      } catch {
        Start-Sleep -Milliseconds 500
      }
    }
    if (-not $ready) { throw "the fresh app did not answer on $url (logs: $work/app.out.log, app.err.log)" }
    $env:TISILIA_INSTALL_CHECK_URL = $url
    Push-Location $consumer
    try {
      Step "node: the generated client calls the fresh app" { node dist/call.js }
      Step "Nuxt packed consumer: SSR, hydration, refresh, imperative file client" { node (Join-Path $root 'scripts/verify-nuxt-consumer.mjs') $consumer $url }
    } finally {
      Pop-Location
      Remove-Item Env:TISILIA_INSTALL_CHECK_URL
    }
  } finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    $server.WaitForExit()
  }

  Write-Host "clean install check passed ($work)"
} finally {
  if (-not $KeepWorkspace) {
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedWork.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolvedWork -Leaf) -notlike 'tisilia-install-*') { throw 'Refusing to remove an unexpected workspace' }
    try { Remove-Item -Recurse -Force $work -ErrorAction Stop } catch { Write-Host "workspace left at $work" }
  }
}
