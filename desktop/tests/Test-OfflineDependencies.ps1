[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$AssemblyPath)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Resolve-Path -LiteralPath $AssemblyPath).Path
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$runtime = $assembly.GetType('BotRuntime', $true)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$cache = $runtime.GetField('dependencyCache', $flags).GetValue($null)
$previous = @{}
foreach ($name in @('OPENCODE_TELEGRAM_ROOT', 'OPENCODE_TELEGRAM_HOME', 'PATH')) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('opencode-offline-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fixture | Out-Null
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Output "PASS: $Message"
}
function Invoke-Static([string]$Name, [object[]]$Arguments = @()) {
    return $runtime.GetMethod($Name, $flags).Invoke($null, $Arguments)
}
try {
    $env:OPENCODE_TELEGRAM_ROOT = $root
    $env:OPENCODE_TELEGRAM_HOME = Join-Path $fixture 'home'
    New-Item -ItemType Directory -Force $env:OPENCODE_TELEGRAM_HOME | Out-Null
    $cache.Clear()
    $inventory = Get-Content -Raw -LiteralPath (Join-Path $root 'offline-dependencies.json') | ConvertFrom-Json
    Assert-True ($assembly.GetName().Version.ToString(3) -eq $inventory.release) 'Desktop assembly version matches the packaged release inventory'
    foreach ($file in @('runtime\node.exe', 'runtime\node_modules\npm\bin\npm-cli.js', 'dependencies\bun\bun.exe', 'dependencies\git\cmd\git.exe', 'dependencies\git\bin\bash.exe', 'dependencies\opencode\v1\opencode.exe', 'dependencies\opencode\v2\opencode.exe', 'dependencies\webview2\msedgewebview2.exe', 'dependencies\licenses\OpenCode-LICENSE.txt', 'dependencies\licenses\Bun-LICENSE.md')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $root $file)) "Payload contains $file"
    }
    $settings = [Activator]::CreateInstance($assembly.GetType('DesktopSettings'), $true)
    foreach ($version in @('v1', 'v2')) {
        $settings.OpenCodeServerVersion = $version
        $settings.OpenCodeExecutable = Join-Path $root 'dependencies\opencode\v1\opencode.exe'
        $selected = Invoke-Static 'ResolveOpenCodeExecutable' @($settings)
        Assert-True ($selected -eq (Join-Path $root "dependencies\opencode\$version\opencode.exe")) "Managed offline engine follows the selected API $version"
        $output = & $selected --version
        Assert-True ($LASTEXITCODE -eq 0 -and $output -match $(if ($version -eq 'v1') { '1\.18\.34' } else { '2\.0\.22' })) "Bundled $version binary runs without installing packages"
    }
    $node = Invoke-Static 'ResolveNodeExecutable'
    $module = (Join-Path $root 'bot\node_modules\better-sqlite3').Replace('\', '/').Replace("'", "\'")
    & $node -e "const D=require('$module');const db=new D(':memory:');if(db.prepare('select 1 as ok').get().ok!==1)process.exit(2);db.close()"
    Assert-True ($LASTEXITCODE -eq 0) 'Selected Node can load the packaged native SQLite module'
    $abi = & (Join-Path $root 'runtime\node.exe') -p 'process.versions.modules'
    $selectedAbi = & $node -p 'process.versions.modules'
    Assert-True ($abi -eq $selectedAbi) 'Reused Node preserves the bundled native ABI'
    $bash = & (Join-Path $root 'dependencies\git\bin\bash.exe') --noprofile --norc -c 'printf offline-bash-ok'
    Assert-True ($LASTEXITCODE -eq 0 -and $bash -eq 'offline-bash-ok') 'Packaged Git Bash runs without an external shell installation'
    $fake = Join-Path $fixture 'existing tools with spaces'
    New-Item -ItemType Directory -Force $fake | Out-Null
    $source = Join-Path $fixture 'Probe.cs'
    'using System;using System.IO;using System.Reflection;class Probe{static void Main(){Console.WriteLine(File.ReadAllText(Assembly.GetExecutingAssembly().Location+".version"));}}' | Set-Content -Encoding ASCII $source
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:exe /platform:x64 ("/out:" + (Join-Path $fake 'bun.exe')) $source
    if ($LASTEXITCODE) { throw 'Compatibility fixture compilation failed' }
    Copy-Item -LiteralPath (Join-Path $fake 'bun.exe') -Destination (Join-Path $fake 'git.exe')
    Set-Content -Encoding ASCII (Join-Path $fake 'bun.exe.version') '1.4.2'
    Set-Content -Encoding ASCII (Join-Path $fake 'git.exe.version') 'git version 2.56.0.windows.1'
    $env:PATH = $fake + [IO.Path]::PathSeparator + $previous['PATH']
    $cache.Clear()
    Assert-True ((Invoke-Static 'ResolveBunExecutable') -eq (Join-Path $fake 'bun.exe')) 'Compatible existing Bun is preferred over the bundle'
    Assert-True ((Invoke-Static 'ResolveGitExecutable') -eq (Join-Path $fake 'git.exe')) 'Compatible existing Git is preferred over the bundle'
    Set-Content -Encoding ASCII (Join-Path $fake 'bun.exe.version') '0.1.0'
    Set-Content -Encoding ASCII (Join-Path $fake 'git.exe.version') 'git version 1.0.0'
    $cache.Clear()
    Assert-True ((Invoke-Static 'ResolveBunExecutable') -ne (Join-Path $fake 'bun.exe')) 'Incompatible existing Bun is not reused'
    Assert-True ((Invoke-Static 'ResolveGitExecutable') -ne (Join-Path $fake 'git.exe')) 'Incompatible existing Git is not reused'
    $path = Invoke-Static 'BuildOpenCodePath' @($settings, $null)
    Assert-True ($path.Contains([IO.Path]::GetDirectoryName($node))) 'Child-process PATH includes the compatible Node runtime'
    Assert-True ($path.Contains([IO.Path]::GetDirectoryName((Invoke-Static 'ResolveGitExecutable')))) 'Child-process PATH includes selected Git'
    Assert-True ((Get-Content -Raw (Join-Path $fake 'bun.exe.version')).Trim() -eq '0.1.0') 'Existing incompatible dependencies are not overwritten'
    Write-Output 'Offline payload and compatibility checks passed; no dependency downloads or model requests were made.'
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    $cache.Clear()
}
