[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$AssemblyPath, [switch]$CheckUpdateMetadata)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$runtime = $assembly.GetType('BotRuntime', $true)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$previous = @{}
foreach ($name in @('OPENCODE_TELEGRAM_ROOT', 'OPENCODE_TELEGRAM_HOME', 'PATH')) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$fixture = Join-Path "$env:LOCALAPPDATA\Temp\opencode" ('engine-selection-' + [Guid]::NewGuid().ToString('N'))
function Invoke-Runtime([string]$Name, [object[]]$Arguments = @()) { $runtime.GetMethod($Name, $flags).Invoke($null, $Arguments) }
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw "FAIL: $Message" }; Write-Output "PASS: $Message" }
try {
    $env:OPENCODE_TELEGRAM_ROOT = Join-Path $fixture 'app'
    $env:OPENCODE_TELEGRAM_HOME = Join-Path $fixture 'home'
    New-Item -ItemType Directory -Force $fixture, $env:OPENCODE_TELEGRAM_ROOT, $env:OPENCODE_TELEGRAM_HOME | Out-Null
    $source = Join-Path $fixture 'Probe.cs'
    'using System;using System.IO;using System.Reflection;class Probe{static void Main(){Console.WriteLine(File.ReadAllText(Assembly.GetExecutingAssembly().Location+".version"));}}' | Set-Content -Encoding ASCII $source
    $probe = Join-Path $fixture 'probe.exe'
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 "/out:$probe" $source
    if ($LASTEXITCODE) { throw 'Fixture compilation failed' }
    $bundled = Join-Path $env:OPENCODE_TELEGRAM_ROOT 'dependencies\opencode\v2\opencode.exe'
    $selected = Join-Path $env:OPENCODE_TELEGRAM_HOME 'opencode-runtime\v2\versions\test version\node_modules\.bin\opencode.exe'
    $legacy = Join-Path $fixture 'legacy\opencode.exe'
    foreach ($file in @($bundled, $selected, $legacy)) { New-Item -ItemType Directory -Force (Split-Path -Parent $file) | Out-Null; Copy-Item $probe $file }
    Set-Content -Encoding ASCII "$bundled.version" '2.0.22'
    Set-Content -Encoding ASCII "$selected.version" '2.0.23'
    Set-Content -Encoding ASCII "$legacy.version" '1.18.34'
    $settings = [Activator]::CreateInstance($assembly.GetType('DesktopSettings'), $true)
    Assert-True ($settings.OpenCodeServerVersion -eq 'v2') 'New settings default to V2'
    $settings.OpenCodeExecutable = $selected
    Assert-True ((Invoke-Runtime 'ResolveOpenCodeExecutable' @($settings)) -eq $selected) 'Explicit managed version is not replaced by a different managed engine'
    Invoke-Runtime 'ConfigureOpenCodeCommand' @($settings)
    $shim = Join-Path $env:OPENCODE_TELEGRAM_ROOT 'bin\opencode.cmd'
    $first = & $env:ComSpec /d /c "`"$shim`" --version"
    Assert-True ($LASTEXITCODE -eq 0 -and $first -eq '2.0.23') 'Stable CLI entry point runs the applied version'
    $settings.OpenCodeExecutable = $bundled
    Invoke-Runtime 'ConfigureOpenCodeCommand' @($settings)
    $second = & $env:ComSpec /d /c "`"$shim`" --version"
    Assert-True ($LASTEXITCODE -eq 0 -and $second -eq '2.0.22') 'The same entry point follows a new selection without reopening the shell'
    Assert-True (@($env:PATH.Split(';') | Where-Object { $_ -eq (Split-Path -Parent $shim) }).Count -eq 1) 'Repeated switches do not grow duplicate PATH entries'
    $env:PATH = (Split-Path -Parent $legacy) + ';' + $env:PATH
    $settings.OpenCodeExecutable = $legacy
    $installations = Invoke-Runtime 'ScanOpenCodeInstallations' @($settings)
    Assert-True (@($installations | Where-Object { $_.Executable -eq $legacy }).Count -eq 0) 'Scanner excludes V1 installations'
    Assert-True (@($installations | Where-Object { $_.Executable -eq $bundled -and $_.IsBundled }).Count -eq 1) 'Scanner marks the bundled V2 engine'
    Assert-True (@($installations | Where-Object { $_.Executable -eq $shim }).Count -eq 0) 'Scanner does not mistake the dynamic shim for another installation'
    $item = [Activator]::CreateInstance($assembly.GetType('OpenCodeInstallation'), $true)
    $item.Executable = $legacy
    $rejected = $false
    try { Invoke-Runtime 'UninstallOpenCodeInstallation' @($item) } catch { $rejected = $true }
    Assert-True ($rejected -and (Test-Path $legacy)) 'Uninstall refuses to recursively delete external installations'
    if ($CheckUpdateMetadata) {
        Set-Content -Encoding ASCII "$selected.version" '2.999.0'
        $settings.OpenCodeExecutable = $selected
        $update = Invoke-Runtime 'UpdateOpenCode' @($settings, $null)
        Assert-True ($null -eq $update -and $settings.OpenCodeExecutable -eq $selected) 'Update reads the official release metadata without downgrading or changing the applied engine'
        Assert-True (@([IO.Directory]::GetDirectories((Join-Path $env:OPENCODE_TELEGRAM_HOME 'opencode-runtime\v2\versions'))).Count -eq 1) 'No download is started when the applied engine is newer'
    }
    Add-Type -AssemblyName PresentationFramework
    $uiType = $assembly.GetType('DesktopApp', $true)
    $instanceFlags = [Reflection.BindingFlags]'Public,NonPublic,Instance'
    $ui = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($uiType)
    $uiType.GetField('settings', $instanceFlags).SetValue($ui, $settings)
    $uiType.GetField('window', $instanceFlags).SetValue($ui, (New-Object Windows.Window))
    $listType = [Collections.Generic.List``1].MakeGenericType($assembly.GetType('OpenCodeInstallation'))
    $list = [Activator]::CreateInstance($listType)
    foreach ($file in @($bundled, $selected)) {
        $entry = [Activator]::CreateInstance($assembly.GetType('OpenCodeInstallation'), $true)
        $entry.Executable = $file; $entry.Version = '2.0.22'; $entry.ApiVersion = 'v2'; $entry.IsBundled = $file -eq $bundled
        $list.Add($entry)
    }
    $settings.OpenCodeExecutable = $bundled
    foreach ($locale in @('zh', 'en')) {
        $settings.Locale = $locale
        $uiType.GetField('content', $instanceFlags).SetValue($ui, (New-Object Windows.Controls.StackPanel))
        $uiType.GetMethod('BuildOpenCodeSettings', $instanceFlags).Invoke($ui, @()) | Out-Null
        $uiType.GetMethod('RenderOpenCodeInstallations', $instanceFlags).Invoke($ui, [object[]]@(,$list)) | Out-Null
        $panel = $uiType.GetField('openCodeInstallationsPanel', $instanceFlags).GetValue($ui)
        $badge = $panel.Children[0].Children[0].Children[1]
        $applied = $panel.Children[0].Children[2].Children[0]
        $apply = $panel.Children[1].Children[2].Children[0]
        $remove = $panel.Children[1].Children[2].Children[1]
        Assert-True ($badge.Background.Color.ToString() -eq '#FF2B67E6') "Bundled badge is blue ($locale)"
        Assert-True ($applied.Background.Color.ToString() -eq '#FF21804A' -and -not $applied.IsEnabled) "Applied button is green ($locale)"
        Assert-True ($apply.Background.Color.ToString() -eq '#FF70788C' -and $apply.IsEnabled) "Apply button is gray ($locale)"
        Assert-True ($remove.Background.Color.ToString() -eq '#FFBF3737') "Uninstall button is red ($locale)"
        $expectedBadge = if ($locale -eq 'en') { 'Bundled' } else { [string][char]0x5185 + [char]0x7F6E }
        $expectedUpdate = if ($locale -eq 'en') { 'Update OpenCode' } else { [string][char]0x66F4 + [char]0x65B0 + ' OpenCode' }
        Assert-True ($badge.Child.Text -eq $expectedBadge) "Bundled badge is localized ($locale)"
        Assert-True ($uiType.GetField('updateOpenCodeButton', $instanceFlags).GetValue($ui).Content -eq $expectedUpdate) "Update button is localized ($locale)"
        Assert-True ($uiType.GetField('serverVersionBox', $instanceFlags).GetValue($ui).Items.Count -eq 1) "Engine page has no V1 selector ($locale)"
    }
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    $runtime.GetField('dependencyCache', $flags).GetValue($null).Clear()
    if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
