param(
    [string] $Sources = "tests/adoption-sources.txt",
    [string] $Output = "artifacts/adoption-compatibility/sources"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath((Join-Path $root $Output))
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$urls = Get-Content (Join-Path $root $Sources) | Where-Object { $_ -match '^https://' }
$curlCommand = (Get-Command curl.exe -ErrorAction SilentlyContinue)?.Source
if (-not $curlCommand) { $curlCommand = (Get-Command curl -CommandType Application -ErrorAction Stop).Source }
$results = $urls | Sort-Object -Unique | ForEach-Object -Parallel {
    $url = $_
    $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($url))).ToLowerInvariant().Substring(0, 16)
    $file = Join-Path $using:outputPath "$key.body"
    $status = & $using:curlCommand --silent --show-error --location --max-time 50 --retry 1 --output $file --write-out '%{http_code} %{url_effective}' -- $url
    $code = $LASTEXITCODE
    [ordered]@{ url = $url; transportExit = $code; response = "$status"; file = "$key.body"; sha256 = if (Test-Path $file) { (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null } }
} -ThrottleLimit 6
$results | Sort-Object { $_.url } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $outputPath 'manifest.json') -Encoding utf8NoBOM
$results | ForEach-Object { '{0} {1}' -f $_.response, $_.file }
if (@($results | Where-Object { $_.transportExit -ne 0 }).Count) { throw 'Some curl requests failed; inspect the manifest.' }
