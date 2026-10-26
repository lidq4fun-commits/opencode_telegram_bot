[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$AssemblyPath)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$type = $assembly.GetType('TelegramWebContext', $true)
$flags = [Reflection.BindingFlags]'Public,Static'
$load = $type.GetMethod('TryLoad', $flags)
$testRoot = Join-Path $env:LOCALAPPDATA ('Temp\opencode\telegram-web-context-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$primary = Join-Path $testRoot 'settings.json'
$backup = Join-Path $testRoot 'settings.json.bak'
$server = [Uri]'http://127.0.0.1:49374/'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Output "PASS: $Message"
}
function Read-Context {
    $arguments = New-Object object[] 2
    $arguments[0] = $testRoot.ToString()
    $loaded = $load.Invoke($null, $arguments)
    return [pscustomobject]@{ Loaded = $loaded; Context = $arguments[1] }
}
function Write-Context([string]$Path, [string]$Project, [string]$Directory, [string]$SessionId = 'ses_test') {
    $value = @{
        currentProject = @{ worktree = $Project; name = 'Test project' }
        currentSession = @{ id = $SessionId; directory = $Directory; title = 'Test session' }
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($Path, $value, (New-Object Text.UTF8Encoding $false))
}

$result = Read-Context
Assert-True ($result.Loaded -and -not $result.Context.HasSession) 'No Bot state does not create a session'
Write-Context $primary 'E:\projects\demo' 'e:/projects/demo/'
$before = [IO.File]::ReadAllText($primary)
$result = Read-Context
Assert-True ($result.Loaded -and $result.Context.HasSession) 'Windows directory separators and case match'
$key = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('http://127.0.0.1:49374')).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$url = $result.Context.WebAddress($server, 'v2')
Assert-True ($url.AbsoluteUri -eq "http://127.0.0.1:49374/server/$key/session/ses_test") 'V2 opens the exact shared session using its native server key'
Assert-True ([IO.File]::ReadAllText($primary) -eq $before) 'Reading context never changes Bot state'

Write-Context $primary '/home/demo' '/home/Demo'
$result = Read-Context
Assert-True (-not $result.Context.HasSession) 'Remote Unix directory matching remains case-sensitive'
Assert-True ($result.Context.WebAddress($server, 'v2') -eq $server) 'Mismatched project/session does not open a stale session'

$unicodeDirectory = 'E:\' + [char]0x9879 + [char]0x76EE + '\demo'
Write-Context $primary $unicodeDirectory $unicodeDirectory
$result = Read-Context
$key = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('http://127.0.0.1:49374')).TrimEnd('=').Replace('+', '-').Replace('/', '_')
Assert-True ($result.Context.WebAddress($server, 'v1').AbsolutePath -eq "/server/$key/session/ses_test") 'Unicode projects use the shared V2 server route even for a V1 caller'

$previousHome = [Environment]::GetEnvironmentVariable('OPENCODE_TELEGRAM_HOME')
try {
    [Environment]::SetEnvironmentVariable('OPENCODE_TELEGRAM_HOME', $testRoot)
    $desktopSettings = [Activator]::CreateInstance($assembly.GetType('DesktopSettings'), $true)
    Assert-True ($desktopSettings.ResolvedWebFollowMode -eq 'force') 'Existing settings always use forced synchronization'
    $desktopSettings.WebFollowMode = 'unknown'
    Assert-True ($desktopSettings.ResolvedWebFollowMode -eq 'force') 'Unknown legacy modes cannot disable synchronization'
    $desktopSettings.WebFollowMode = 'prompt'
    $desktopSettings.OpenCodeServerVersion = 'v2'
    $desktopSettings.OpenCodeUrl = $server.AbsoluteUri
    $arguments = New-Object object[] 1
    $arguments[0] = $desktopSettings.PSObject.BaseObject
    $url = $assembly.GetType('Cli').GetMethod('GetSelectedWebUrl', [Reflection.BindingFlags]'NonPublic,Static').Invoke($null, $arguments)
    Assert-True ($url.AbsoluteUri -eq $result.Context.WebAddress($server, 'v2').AbsoluteUri) 'CLI web open uses the same Telegram session as the desktop page'
    foreach ($mode in @('off', 'prompt', 'force')) {
        $desktopSettings.WebFollowMode = $mode
        $assembly.GetType('SettingsStore').GetMethod('Save', $flags).Invoke($null, $arguments)
        $saved = $assembly.GetType('SettingsStore').GetMethod('Load', $flags).Invoke($null, $null)
        Assert-True ($saved.ResolvedWebFollowMode -eq 'force') "Legacy mode always resolves to synchronization after restart: $mode"
    }
    $desktopSettings.WebFollowMode = 'off'
    $url = $assembly.GetType('Cli').GetMethod('GetSelectedWebUrl', [Reflection.BindingFlags]'NonPublic,Static').Invoke($null, $arguments)
    Assert-True ($url -eq $result.Context.WebAddress($server, 'v2')) 'Legacy no-follow settings cannot open an independent workspace'
} finally {
    [Environment]::SetEnvironmentVariable('OPENCODE_TELEGRAM_HOME', $previousHome)
}

Write-Context $backup 'E:\projects\backup' 'E:\projects\backup' 'ses_backup'
[IO.File]::WriteAllText($primary, '{invalid')
$result = Read-Context
Assert-True ($result.Loaded -and $result.Context.Session.Id -eq 'ses_backup') 'Atomic-write gaps or corrupt primary can use the Bot backup'
[IO.File]::WriteAllText($primary, '{}')
$result = Read-Context
Assert-True ($result.Loaded -and -not $result.Context.HasSession) 'A cleared primary never resurrects the backup session'
[IO.File]::WriteAllText($primary, '{invalid')
[IO.File]::WriteAllText($backup, '{invalid')
$result = Read-Context
Assert-True (-not $result.Loaded) 'Unreadable state is reported without navigating or overwriting it'

$syncDirectory = Join-Path $testRoot 'desktop-sync'
New-Item -ItemType Directory -Path $syncDirectory | Out-Null
$syncFile = Join-Path $syncDirectory 'state.json'
$syncLoad = $type.GetMethod('TryLoadSync', $flags)
$syncArguments = New-Object object[] 3
$syncArguments[0] = $testRoot.ToString()
$syncValue = @{
    instance = 'test-instance'; mobileRevision = 2; mobileActive = $false; busy = $true
    updatedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    currentProject = @{ worktree = 'E:\projects\demo' }
    currentSession = @{ id = 'ses_sync'; directory = 'E:\projects\demo'; title = 'Shared' }
    currentModel = @{ providerID = 'provider'; modelID = 'model'; variant = 'high' }
    acknowledgements = @(@{ id = 'command-test'; reason = $null })
}
[IO.File]::WriteAllText($syncFile, ($syncValue | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
Assert-True ($syncLoad.Invoke($null, $syncArguments)) 'Fresh bot synchronization state is available'
Assert-True ($syncArguments[1].Busy -and $syncArguments[1].MobileRevision -eq 2) 'Answer lock and mobile operation revision are decoded'
Assert-True ($syncArguments[2].Contains('acknowledgements')) 'Raw metadata preserves model and desktop acknowledgements for the embedded page'
$syncValue.updatedAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - 10000
[IO.File]::WriteAllText($syncFile, ($syncValue | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
Assert-True (-not $syncLoad.Invoke($null, $syncArguments)) 'A stopped bot cannot leave a permanent answer lock'
$syncValue.currentSession = $null
[IO.File]::WriteAllText($primary, ($syncValue | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
$result = Read-Context
Assert-True ($result.Context.WebAddress($server, 'v1') -eq $server) 'A phone project without a session opens the server root instead of a stale conversation'
Write-Output 'All Telegram web context checks passed; only isolated temporary fixtures were written.'
