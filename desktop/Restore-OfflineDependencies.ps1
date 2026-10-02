[CmdletBinding()]
param([string]$ArchiveDirectory = (Join-Path $PSScriptRoot 'dependencies\archives'))

$ErrorActionPreference = 'Stop'
$destination = Join-Path $PSScriptRoot 'dependencies\offline'
foreach ($archive in @('portable-git-x64.exe', 'bun-x64.zip', 'opencode-v1.tgz', 'opencode-v2.tgz', 'webview2-x64.cab')) {
    $file = Join-Path $ArchiveDirectory $archive
    if (-not (Test-Path -LiteralPath $file)) { throw "Offline archive is missing: $file" }
    $status = "$file.download-status.json"
    if ((Test-Path -LiteralPath $status) -and (Get-Content -Raw -LiteralPath $status | ConvertFrom-Json).state -ne 'completed') {
        throw "Offline archive download is not complete: $file"
    }
}
$inputs = @(
    @{ File = 'portable-git-x64.exe'; Algorithm = 'SHA256'; Hash = 'ECEB5E061AA90DF2F69DDD3E90F0030E1B8037A7829934BC40E4BE1CAA1ACCC1' },
    @{ File = 'bun-x64.zip'; Algorithm = 'SHA256'; Hash = '78C221C2376F79731CCF4E4AF0B3BB46D81FEFA3296C5ABEE09AD8A1B21E68C6' },
    @{ File = 'opencode-v1.tgz'; Algorithm = 'SHA512'; Base64 = 'Ng4R5BDWS/xHmHyjBC2hQpakIrvu3vYvO7U2oF820xjih1VJl4zON52VGtT4Zh1o6kbf8f6s7yLPMzzblGSXuw==' },
    @{ File = 'opencode-v2.tgz'; Algorithm = 'SHA512'; Base64 = 'o7gQWmvckjnLjYTCjsHiztfLfhNZMHpPdRwM/Y/TsYaQsSqAiM3l65OaHBH6q4cEClnOYlpWqU2Jpb+iLwqvIQ==' }
)
foreach ($input in $inputs) {
    $file = Join-Path $ArchiveDirectory $input.File
    $expected = $input.Hash
    if ($input.Base64) { $expected = [BitConverter]::ToString([Convert]::FromBase64String($input.Base64)).Replace('-', '') }
    if ((Get-FileHash -LiteralPath $file -Algorithm $input.Algorithm).Hash -ne $expected) { throw "Publisher checksum mismatch: $file" }
}
New-Item -ItemType Directory -Force $destination | Out-Null
$stage = Join-Path $destination ('.restore-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($version in @('v1', 'v2')) {
    $folder = Join-Path $stage $version
    New-Item -ItemType Directory -Force $folder | Out-Null
    & tar.exe -xzf (Join-Path $ArchiveDirectory "opencode-$version.tgz") -C $folder
    if ($LASTEXITCODE) { throw "Cannot extract OpenCode $version" }
    $target = Join-Path $destination "opencode\$version"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item -LiteralPath (Join-Path $folder 'package\bin\opencode.exe') -Destination $target -Force
    Copy-Item -LiteralPath (Join-Path $folder 'package\package.json') -Destination $target -Force
}
Expand-Archive -LiteralPath (Join-Path $ArchiveDirectory 'bun-x64.zip') -DestinationPath (Join-Path $stage 'bun')
$bun = Get-ChildItem -LiteralPath (Join-Path $stage 'bun') -Recurse -Filter bun.exe | Select-Object -First 1
New-Item -ItemType Directory -Force (Join-Path $destination 'bun') | Out-Null
Copy-Item -LiteralPath $bun.FullName -Destination (Join-Path $destination 'bun\bun.exe') -Force
$gitDirectory = Join-Path $destination 'git'
if (-not (Test-Path -LiteralPath (Join-Path $gitDirectory 'cmd\git.exe'))) {
    $process = Start-Process -FilePath (Join-Path $ArchiveDirectory 'portable-git-x64.exe') -ArgumentList @('-y', ('-o"' + $gitDirectory + '"')) -Wait -PassThru
    if ($process.ExitCode) { throw "Portable Git extraction failed: $($process.ExitCode)" }
}
$webDirectory = Join-Path $destination 'webview2'
if (-not (Test-Path -LiteralPath (Join-Path $webDirectory 'msedgewebview2.exe'))) {
    $webStage = Join-Path $stage 'webview2'
    New-Item -ItemType Directory -Force $webStage | Out-Null
    & expand.exe (Join-Path $ArchiveDirectory 'webview2-x64.cab') '-F:*' $webStage | Out-Null
    if ($LASTEXITCODE) { throw 'WebView2 cabinet extraction failed' }
    $browser = Get-ChildItem -LiteralPath $webStage -Recurse -Filter msedgewebview2.exe | Select-Object -First 1
    if (-not $browser) { throw 'Fixed WebView2 runtime executable is absent' }
    $signature = Get-AuthenticodeSignature -LiteralPath $browser.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'WebView2 Microsoft signature verification failed' }
    Copy-Item -LiteralPath $browser.Directory.FullName -Destination $webDirectory -Recurse
}
foreach ($executable in @('bun\bun.exe', 'git\cmd\git.exe', 'opencode\v1\opencode.exe', 'opencode\v2\opencode.exe')) {
    & (Join-Path $destination $executable) --version
    if ($LASTEXITCODE) { throw "Offline dependency smoke check failed: $executable" }
}
Write-Output "Offline dependencies restored: $destination"
