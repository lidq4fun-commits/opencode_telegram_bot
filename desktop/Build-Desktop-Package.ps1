[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$NodeHome = 'D:\Program Files\nodejs'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$botRoot = Join-Path $repoRoot 'bot'
$stageRoot = Join-Path $PSScriptRoot '.build'
$bundleRoot = Join-Path $stageRoot 'bundle'
$botBundle = Join-Path $bundleRoot 'bot'
$runtimeRoot = Join-Path $bundleRoot 'runtime'
$artifactRoot = Join-Path $PSScriptRoot 'artifacts'
$bundleZip = Join-Path $stageRoot 'BotRuntime.zip'
$setupExe = Join-Path $artifactRoot "OpenCodeTelegram-Setup-$Version.exe"
$releaseRoot = Join-Path $stageRoot 'release'
$releaseDependencies = Join-Path $releaseRoot 'Dependencies'
$releaseZip = Join-Path $artifactRoot "OpenCodeTelegram-$Version-Windows-x64.zip"
$readmeSource = Join-Path $PSScriptRoot 'README-Installer.md'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$icon = Join-Path $repoRoot 'OpenCodeTelegramLauncher.ico'

foreach ($required in @(
    (Join-Path $botRoot 'src'),
    (Join-Path $botRoot 'node_modules'),
    (Join-Path $NodeHome 'node.exe'),
    (Join-Path $NodeHome 'node_modules\npm\bin\npm-cli.js'),
    $csc,
    $icon,
    $readmeSource
)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required build input is missing: $required" }
}

Write-Host 'Building the forked TypeScript Bot...'
$botBuild = Start-Process -FilePath 'cmd.exe' -ArgumentList '/d', '/c', 'npm run build' -WorkingDirectory $botRoot -Wait -PassThru -NoNewWindow
if ($botBuild.ExitCode -ne 0) { throw "Bot source build failed with exit code $($botBuild.ExitCode)." }

if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $botBundle -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $runtimeRoot 'node_modules') -Force | Out-Null
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

$copyBot = Start-Process -FilePath 'robocopy.exe' -ArgumentList @(
    $botRoot, $botBundle,
    '/E', '/XD', 'node_modules', '.git', 'logs', '/XF', '.env', '.env.*',
    '/COPY:DAT', '/DCOPY:DAT', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS'
) -Wait -PassThru -NoNewWindow
if ($copyBot.ExitCode -gt 7) { throw "Failed to stage forked Bot source (robocopy $($copyBot.ExitCode))." }
if (Test-Path -LiteralPath (Join-Path $botRoot '.env.example')) {
    Copy-Item -LiteralPath (Join-Path $botRoot '.env.example') -Destination $botBundle -Force
}

$copyNodeModules = Start-Process -FilePath 'robocopy.exe' -ArgumentList @(
    (Join-Path $botRoot 'node_modules'),
    (Join-Path $botBundle 'node_modules'),
    '/E', '/COPY:DAT', '/DCOPY:DAT', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS'
) -Wait -PassThru -NoNewWindow
if ($copyNodeModules.ExitCode -gt 7) { throw "Failed to stage Bot dependencies (robocopy $($copyNodeModules.ExitCode))." }

Write-Host 'Pruning development-only dependencies from the runtime payload...'
$prune = Start-Process -FilePath 'cmd.exe' -ArgumentList '/d', '/c', 'npm prune --offline --omit=dev --ignore-scripts --no-audit --no-fund --progress=true' -WorkingDirectory $botBundle -Wait -PassThru -NoNewWindow
if ($prune.ExitCode -ne 0) { throw "npm prune failed with exit code $($prune.ExitCode)." }

Copy-Item -LiteralPath (Join-Path $NodeHome 'node.exe') -Destination $runtimeRoot -Force
Copy-Item -LiteralPath (Join-Path $NodeHome 'LICENSE') -Destination $runtimeRoot -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath (Join-Path $NodeHome 'node_modules\npm') -Destination (Join-Path $runtimeRoot 'node_modules') -Recurse -Force

$appExe = Join-Path $bundleRoot 'OpenCodeTelegram.exe'
$appArgs = @(
    '/nologo', '/target:winexe', '/optimize+', "/out:$appExe", "/win32icon:$icon",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xaml.dll',
    "/reference:$wpf\WindowsBase.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\PresentationFramework.dll",
    '/reference:System.Security.dll', '/reference:System.Runtime.Serialization.dll',
    '/reference:System.Management.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll',
    (Join-Path $PSScriptRoot 'OpenCodeTelegram.Desktop.cs')
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
    '/nologo', '/target:winexe', '/optimize+', '/define:SETUP_BUILD', "/out:$setupExe", "/win32icon:$icon",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xaml.dll',
    "/reference:$wpf\WindowsBase.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\PresentationFramework.dll",
    '/reference:System.Security.dll', '/reference:System.Runtime.Serialization.dll',
    '/reference:System.Management.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll',
    (Join-Path $PSScriptRoot 'OpenCodeTelegram.Desktop.cs')
)
& $csc @setupArgs
if ($LASTEXITCODE -ne 0) { throw "Self-extracting setup compilation failed with exit code $LASTEXITCODE." }

New-Item -ItemType Directory -Path $releaseDependencies -Force | Out-Null
Copy-Item -LiteralPath $setupExe -Destination (Join-Path $releaseRoot (Split-Path -Leaf $setupExe)) -Force
Copy-Item -LiteralPath $readmeSource -Destination (Join-Path $releaseRoot 'README.md') -Force
Copy-Item -LiteralPath $bundleZip -Destination (Join-Path $releaseDependencies 'BotRuntime.zip') -Force
if (Test-Path -LiteralPath $releaseZip) { Remove-Item -LiteralPath $releaseZip -Force }
Compress-Archive -Path (Join-Path $releaseRoot '*') -DestinationPath $releaseZip -CompressionLevel Optimal

Get-Item -LiteralPath $releaseZip, $setupExe | Format-Table FullName, Length -AutoSize
Get-FileHash -LiteralPath $releaseZip -Algorithm SHA256 | Format-List Hash
