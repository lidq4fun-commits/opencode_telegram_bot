[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$SetupPath, [string]$ExpectedVersion = '1.0.12')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $SetupPath).Path)
$runtime = $assembly.GetManifestResourceStream('BotRuntime.zip')
if (-not $runtime) { throw 'The installer does not contain the Bot runtime.' }
$archive = New-Object IO.Compression.ZipArchive($runtime, [IO.Compression.ZipArchiveMode]::Read, $false)
$root = Join-Path $env:LOCALAPPDATA ('Temp\opencode\sync-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Output "PASS: $Message"
}
function Read-Entry([string]$Name) {
    $entry = $archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq $Name } | Select-Object -First 1
    if (-not $entry) { throw "Missing runtime entry: $Name" }
    $reader = New-Object IO.StreamReader($entry.Open(), [Text.Encoding]::UTF8)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
try {
    $packaged = Read-Entry 'bot/dist/app/services/desktop-sync-service.js'
    $expected = [IO.File]::ReadAllText((Join-Path $repoRoot 'bot\dist\app\services\desktop-sync-service.js'))
    Assert-True ($packaged -ceq $expected) 'Packaged Bot synchronization matches the final compiled source'
    foreach ($name in @('OpenCodeTelegram.exe', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll')) {
        $entry = $archive.GetEntry($name)
        if (-not $entry) { throw "Missing desktop runtime entry: $name" }
        $inputStream = $entry.Open()
        $outputStream = [IO.File]::Create((Join-Path $root $name))
        try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
    }
    $desktop = [Reflection.Assembly]::LoadFrom((Join-Path $root 'OpenCodeTelegram.exe'))
    $reader = New-Object IO.StreamReader($desktop.GetManifestResourceStream('EmbeddedWebSync.js'), [Text.Encoding]::UTF8)
    try { $embedded = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $expected = [IO.File]::ReadAllText((Join-Path $repoRoot 'desktop\EmbeddedWebSync.js'))
    Assert-True ($embedded -ceq $expected) 'Embedded web synchronization matches the final JavaScript source'
    Assert-True ($desktop.GetName().Version.ToString(3) -eq $ExpectedVersion) 'The packaged desktop has the new release version'
    & (Join-Path $PSScriptRoot 'Test-TelegramWebContext.ps1') -AssemblyPath (Join-Path $root 'OpenCodeTelegram.exe')
    & (Join-Path $PSScriptRoot 'Test-BrowserLogin.ps1') -AssemblyPath (Join-Path $root 'OpenCodeTelegram.exe')
} finally {
    $archive.Dispose()
    $runtime.Dispose()
}
Write-Output 'Packaged synchronization checks passed; no live Bot state or installed application was changed.'
