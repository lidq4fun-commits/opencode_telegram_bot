[CmdletBinding()]
param([string]$PackagePath = '')

$ErrorActionPreference = 'Stop'
$version = '1.0.4258.31'
$destination = Join-Path $PSScriptRoot 'dependencies\webview2'
$files = @{
    'lib/net462/Microsoft.Web.WebView2.Core.dll' = 'Microsoft.Web.WebView2.Core.dll'
    'lib/net462/Microsoft.Web.WebView2.Wpf.dll' = 'Microsoft.Web.WebView2.Wpf.dll'
    'runtimes/win-x64/native/WebView2Loader.dll' = 'WebView2Loader.dll'
}
if (-not $PackagePath) {
    if (@($files.Values | Where-Object { -not (Test-Path (Join-Path $destination $_)) }).Count -eq 0) { return }
    throw "WebView2 SDK missing. Download official NuGet package Microsoft.Web.WebView2 $version, then run desktop\Restore-WebView2.ps1 -PackagePath <package.nupkg>. No installer is built by this command."
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
try {
    foreach ($entryName in $files.Keys) {
        $entry = $archive.GetEntry($entryName)
        if (-not $entry) { throw "Missing SDK entry: $entryName" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $destination $files[$entryName]), $true)
    }
} finally { $archive.Dispose() }
Get-ChildItem -LiteralPath $destination
