[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$NodeHome = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$parsedVersion = [version]$Version
if ($parsedVersion.Build -lt 0 -or $parsedVersion.Revision -gt 0) { throw 'Use a three-component release version, such as 1.0.10.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$botRoot = Join-Path $repoRoot 'bot'
$stageRoot = Join-Path $PSScriptRoot ('.build-release-' + [Guid]::NewGuid().ToString('N'))
$bundleRoot = Join-Path $stageRoot 'bundle'
$botBundle = Join-Path $bundleRoot 'bot'
$runtimeRoot = Join-Path $bundleRoot 'runtime'
$artifactRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $PSScriptRoot 'artifacts' } else { [IO.Path]::GetFullPath($OutputDirectory) }
$bundleZip = Join-Path $stageRoot 'BotRuntime.zip'
$setupExe = Join-Path $artifactRoot "OpenCodeTelegram-Setup-$Version.exe"
$releaseRoot = Join-Path $stageRoot 'release'
$releaseZip = Join-Path $artifactRoot "OpenCodeTelegram-$Version-Windows-x64.zip"
$readmeSource = Join-Path $PSScriptRoot 'README-Installer.md'
$windowsRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
$frameworkRoot = Join-Path $windowsRoot 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $frameworkRoot 'csc.exe'
$wpf = Join-Path $frameworkRoot 'WPF'
$icon = Join-Path $repoRoot 'OpenCodeTelegramLauncher.ico'
& (Join-Path $PSScriptRoot 'Restore-WebView2.ps1')
$webViewDependencies = Join-Path $PSScriptRoot 'dependencies\webview2'
$offlineDependencies = Join-Path $PSScriptRoot 'dependencies\offline'
foreach ($requiredDependency in @('bun\bun.exe', 'git\cmd\git.exe', 'opencode\v1\opencode.exe', 'opencode\v2\opencode.exe', 'webview2\msedgewebview2.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $offlineDependencies $requiredDependency))) {
        throw "Complete offline payload missing: $requiredDependency. Run Restore-OfflineDependencies.ps1 with the verified official archives first."
    }
}
foreach ($license in @('OpenCode-LICENSE.txt', 'Bun-LICENSE.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $offlineDependencies "licenses\$license"))) { throw "Offline dependency license is missing: $license" }
}

if (-not [Environment]::Is64BitOperatingSystem) { throw 'The desktop package targets 64-bit Windows.' }
if ([string]::IsNullOrWhiteSpace($NodeHome)) {
    $nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    $nodeCandidates = @()
    if ($nodeCommand) { $nodeCandidates += Split-Path -Parent $nodeCommand.Source }
    $nodeCandidates += @(
        (Join-Path $env:ProgramFiles 'nodejs'),
        (Join-Path ${env:ProgramFiles(x86)} 'nodejs'),
        (Join-Path $env:LOCALAPPDATA 'Programs\nodejs')
    )
    $NodeHome = $nodeCandidates | Where-Object {
        $_ -and (Test-Path -LiteralPath (Join-Path $_ 'node.exe')) -and
        (Test-Path -LiteralPath (Join-Path $_ 'node_modules\npm\bin\npm-cli.js'))
    } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($NodeHome)) { throw 'Node.js was not found. Install Node.js 24+ x64 or pass -NodeHome <folder>.' }
$NodeHome = (Resolve-Path -LiteralPath $NodeHome).Path
$nodeExe = Join-Path $NodeHome 'node.exe'
$npmCli = Join-Path $NodeHome 'node_modules\npm\bin\npm-cli.js'
if (-not (Test-Path -LiteralPath $nodeExe)) { throw "Node.js was not found under NodeHome: $NodeHome" }
$nodeArch = & $nodeExe -p 'process.arch'
if ($LASTEXITCODE -ne 0 -or $nodeArch.Trim() -ne 'x64') { throw "The bundled Node.js must be x64; detected architecture: $nodeArch" }
$nodeVersion = & $nodeExe -p 'process.versions.node'
if ($LASTEXITCODE -ne 0 -or [version]$nodeVersion -lt [version]'24.0') { throw "Node.js 24 or newer is required for the bundled runtime; detected $nodeVersion." }

foreach ($required in @(
    (Join-Path $botRoot 'src'),
    (Join-Path $botRoot 'node_modules'),
    $nodeExe,
    $npmCli,
    $csc,
    $icon,
    $readmeSource
)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required build input is missing: $required" }
}

Write-Host "Building with Node $nodeVersion ($nodeArch) from $NodeHome..."
Push-Location $botRoot
try {
    & $nodeExe $npmCli run build
    if ($LASTEXITCODE -ne 0) { throw "Bot source build failed with exit code $LASTEXITCODE." }
} finally { Pop-Location }

New-Item -ItemType Directory -Path $botBundle -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $runtimeRoot 'node_modules') -Force | Out-Null
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

# Stage only runtime inputs; never include developer caches, tests, secrets or
# machine-specific Bot state just because they happen to exist in the checkout.
foreach ($directory in @('dist', 'assets')) {
    $source = Join-Path $botRoot $directory
    if (-not (Test-Path -LiteralPath $source)) { throw "Required Bot runtime directory is missing: $source" }
    & robocopy.exe $source (Join-Path $botBundle $directory) @(
        '/E', '/XJ', '/COPY:DAT', '/DCOPY:DAT', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS'
    )
    if ($LASTEXITCODE -gt 7) { throw "Failed to stage Bot runtime directory: $directory" }
}
foreach ($file in @('package.json', 'package-lock.json', 'EOT10Crypto.mjs', 'LICENSE', 'README.md', '.env.example')) {
    $source = Join-Path $botRoot $file
    if (-not (Test-Path -LiteralPath $source)) { throw "Required Bot runtime file is missing: $source" }
    Copy-Item -LiteralPath $source -Destination $botBundle -Force
}

& robocopy.exe (Join-Path $botRoot 'node_modules') (Join-Path $botBundle 'node_modules') @(
    '/E', '/COPY:DAT', '/DCOPY:DAT', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS'
)
$copyNodeModulesExit = $LASTEXITCODE
if ($copyNodeModulesExit -gt 7) { throw "Failed to stage Bot dependencies (robocopy $copyNodeModulesExit)." }

Write-Host 'Pruning development-only dependencies from the runtime payload...'
Push-Location $botBundle
try {
    & $nodeExe $npmCli prune --offline --omit=dev --ignore-scripts --no-audit --no-fund --progress=true
    if ($LASTEXITCODE -ne 0) { throw "npm prune failed with exit code $LASTEXITCODE." }
} finally { Pop-Location }

Copy-Item -LiteralPath $nodeExe -Destination $runtimeRoot -Force
Copy-Item -LiteralPath (Join-Path $NodeHome 'LICENSE') -Destination $runtimeRoot -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath (Join-Path $NodeHome 'node_modules\npm') -Destination (Join-Path $runtimeRoot 'node_modules') -Recurse -Force
foreach ($dependency in @('bun', 'git', 'opencode', 'webview2')) {
    & robocopy.exe (Join-Path $offlineDependencies $dependency) (Join-Path $bundleRoot "dependencies\$dependency") @(
        '/E', '/XJ', '/COPY:DAT', '/DCOPY:DAT', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS'
    )
    if ($LASTEXITCODE -gt 7) { throw "Failed to stage offline dependency: $dependency" }
}
New-Item -ItemType Directory -Force (Join-Path $bundleRoot 'dependencies\licenses') | Out-Null
Copy-Item -LiteralPath (Join-Path $offlineDependencies 'licenses\OpenCode-LICENSE.txt'), (Join-Path $offlineDependencies 'licenses\Bun-LICENSE.md') -Destination (Join-Path $bundleRoot 'dependencies\licenses')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OfflineDependencies.json') -Destination (Join-Path $bundleRoot 'dependency-sources.json')
$inventory = Get-ChildItem -LiteralPath (Join-Path $bundleRoot 'dependencies') -Recurse -File | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($bundleRoot.Length + 1).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
[ordered]@{ release = $Version; platform = 'windows-x64'; files = @($inventory) } | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $bundleRoot 'offline-dependencies.json')

$appExe = Join-Path $bundleRoot 'OpenCodeTelegram.exe'
$assemblyMetadata = Join-Path $stageRoot 'AssemblyVersion.cs'
@"
[assembly: System.Reflection.AssemblyVersion("$($parsedVersion.ToString(3)).0")]
[assembly: System.Reflection.AssemblyFileVersion("$($parsedVersion.ToString(3)).0")]
[assembly: System.Reflection.AssemblyInformationalVersion("$Version")]
"@ | Set-Content -LiteralPath $assemblyMetadata -Encoding UTF8
Copy-Item -Path (Join-Path $webViewDependencies '*.dll') -Destination $bundleRoot
$appArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', "/out:$appExe", "/win32icon:$icon",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xaml.dll',
    "/reference:$wpf\WindowsBase.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\PresentationFramework.dll",
    '/reference:System.Security.dll', '/reference:System.Runtime.Serialization.dll',
    '/reference:System.Management.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll',
    "/reference:$webViewDependencies\Microsoft.Web.WebView2.Core.dll", "/reference:$webViewDependencies\Microsoft.Web.WebView2.Wpf.dll",
    (Join-Path $PSScriptRoot 'OpenCodeTelegram.Desktop.cs'), $assemblyMetadata
)
& $csc @appArgs
if ($LASTEXITCODE -ne 0) { throw "Desktop app compilation failed with exit code $LASTEXITCODE." }

Push-Location $botBundle
try {
    & (Join-Path $runtimeRoot 'node.exe') 'dist\cli.js' '--help'
    if ($LASTEXITCODE -ne 0) { throw 'Bundled Bot CLI smoke check failed.' }
    & (Join-Path $runtimeRoot 'node.exe') '-e' "const D=require('better-sqlite3');const db=new D(':memory:');if(db.prepare('select 1 as ok').get().ok!==1)process.exit(2);db.close()"
    if ($LASTEXITCODE -ne 0) { throw 'Bundled native SQLite smoke check failed.' }
} finally { Pop-Location }

if (Test-Path -LiteralPath $bundleZip) { Remove-Item -LiteralPath $bundleZip -Force }
Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $bundleZip -CompressionLevel Optimal
$setupArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/define:SETUP_BUILD', "/out:$setupExe", "/win32icon:$icon", "/resource:$bundleZip,BotRuntime.zip",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xaml.dll',
    "/reference:$wpf\WindowsBase.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\PresentationFramework.dll",
    '/reference:System.Security.dll', '/reference:System.Runtime.Serialization.dll',
    '/reference:System.Management.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll',
    "/reference:$webViewDependencies\Microsoft.Web.WebView2.Core.dll", "/reference:$webViewDependencies\Microsoft.Web.WebView2.Wpf.dll",
    (Join-Path $PSScriptRoot 'OpenCodeTelegram.Desktop.cs'), $assemblyMetadata
)
& $csc @setupArgs
if ($LASTEXITCODE -ne 0) { throw "Self-extracting setup compilation failed with exit code $LASTEXITCODE." }

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Copy-Item -LiteralPath $setupExe -Destination (Join-Path $releaseRoot (Split-Path -Leaf $setupExe)) -Force
Copy-Item -LiteralPath $readmeSource -Destination (Join-Path $releaseRoot 'README.md') -Force
if (Test-Path -LiteralPath $releaseZip) { Remove-Item -LiteralPath $releaseZip -Force }
Compress-Archive -Path (Join-Path $releaseRoot '*') -DestinationPath $releaseZip -CompressionLevel Optimal

Get-Item -LiteralPath $releaseZip, $setupExe | Format-Table FullName, Length -AutoSize
Get-FileHash -LiteralPath $releaseZip -Algorithm SHA256 | Format-List Hash
