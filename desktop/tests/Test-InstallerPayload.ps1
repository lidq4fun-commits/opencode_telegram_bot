[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$InstallerPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$installer = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $InstallerPath).Path)
$stream = $installer.GetManifestResourceStream('BotRuntime.zip')
if (-not $stream) { throw 'Installer has no embedded runtime archive' }
$destination = Join-Path ([IO.Path]::GetTempPath()) ('OpenCode relocated payload ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $destination | Out-Null
try {
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $prefix = [IO.Path]::GetFullPath($destination) + [IO.Path]::DirectorySeparatorChar
        foreach ($entry in $archive.Entries) {
            $output = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
            if (-not $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe installer archive path' }
            if (-not $entry.Name) { New-Item -ItemType Directory -Force $output | Out-Null; continue }
            New-Item -ItemType Directory -Force (Split-Path -Parent $output) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $output)
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
$inventory = Get-Content -Raw -LiteralPath (Join-Path $destination 'offline-dependencies.json') | ConvertFrom-Json
if ($inventory.release -ne $installer.GetName().Version.ToString(3)) { throw 'Installer version differs from the payload version' }
foreach ($file in $inventory.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $destination $file.path))
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe inventory path' }
    if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Payload checksum mismatch: $($file.path)" }
}
Write-Output "PASS: standalone setup embeds its complete dependency inventory ($($inventory.files.Count) verified files)"
$app = Join-Path $destination 'OpenCodeTelegram.exe'
& (Join-Path $PSScriptRoot 'Test-OfflineDependencies.ps1') -AssemblyPath $app
Write-Output "PASS: installer payload works from a new path containing spaces: $destination"
Write-Output 'No registry entries, shortcuts or existing installations were changed. This does not replace a clean-machine OS compatibility test.'
