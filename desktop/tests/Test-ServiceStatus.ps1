[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$runtime = $assembly.GetType('BotRuntime', $true)
$settingsType = $assembly.GetType('DesktopSettings', $true)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$fixture = Join-Path "$env:LOCALAPPDATA\Temp\opencode" ('service-status-' + [Guid]::NewGuid().ToString('N'))
$previousHome = [Environment]::GetEnvironmentVariable('OPENCODE_TELEGRAM_HOME', 'Process')
$previousState = [Environment]::GetEnvironmentVariable('XDG_STATE_HOME', 'Process')

function Invoke-Runtime([string]$Name, [object[]]$Arguments = @()) {
    return $runtime.GetMethod($Name, $flags).Invoke($null, $Arguments)
}
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Output "PASS: $Message"
}
function New-Settings([string]$url) {
    $settings = [Activator]::CreateInstance($settingsType, $true)
    $settings.OpenCodeServerVersion = 'v2'
    $settings.OpenCodeUrl = $url
    $settings.OpenCodeUsername = 'opencode'
    $settings.OpenCodePassword = 'secret-password'
    return $settings
}
function Write-Json([string]$Path, [string]$Value) {
    [IO.File]::WriteAllText($Path, $Value, (New-Object System.Text.UTF8Encoding($false)))
}
function Write-DaemonState([int]$processId, [string]$startedAt, [string]$logFile) {
    New-Item -ItemType Directory -Force (Join-Path $fixture 'home\run') | Out-Null
    $state = [ordered]@{ pid = $processId; startedAt = $startedAt; logFilePath = $logFile; mode = 'daemon' }
    Write-Json (Join-Path $fixture 'home\run\bot-service.json') ($state | ConvertTo-Json -Compress)
}
function Write-OpenCodeServiceState([int]$processId, [string]$url) {
    New-Item -ItemType Directory -Force (Join-Path $fixture 'state\opencode') | Out-Null
    $state = [ordered]@{ id = 'fixture'; version = '2.0.24'; url = $url; pid = $processId; password = 'secret-password' }
    Write-Json (Join-Path $fixture 'state\opencode\service.json') ($state | ConvertTo-Json -Compress)
    Remove-Item -LiteralPath (Join-Path $fixture 'state\opencode\service.json.bak') -ErrorAction SilentlyContinue
}

try {
    New-Item -ItemType Directory -Force $fixture | Out-Null
    $env:OPENCODE_TELEGRAM_HOME = Join-Path $fixture 'home'
    $env:XDG_STATE_HOME = Join-Path $fixture 'state'

    Assert-True ((Invoke-Runtime 'ReadServiceStatus') -eq 'Service status: stopped') 'Dashboard status reports stopped without a daemon state file'

    $processStart = (Get-Process -Id $PID).StartTime.ToUniversalTime()
    Write-DaemonState $PID $processStart.AddSeconds(1).ToString('o') (Join-Path $fixture 'home\logs\bot.log')
    $status = Invoke-Runtime 'ReadServiceStatus'
    Assert-True ($status -match 'Service status: running') 'Dashboard status reports a live daemon PID'
    Assert-True ($status -match ("PID: " + $PID)) 'Dashboard status prints the daemon PID'
    Assert-True ($status -match [regex]::Escape('Log file: ' + (Join-Path $fixture 'home\logs\bot.log'))) 'Dashboard status prints the daemon log file'
    Assert-True ($status -match [regex]::Escape('App home: ' + (Join-Path $fixture 'home'))) 'Dashboard status prints the app home'
    Assert-True ($status -match 'Uptime: \d+ sec') 'Dashboard status prints the uptime'

    Write-DaemonState $PID $processStart.AddMinutes(-5).ToString('o') (Join-Path $fixture 'home\logs\bot.log')
    Assert-True ((Invoke-Runtime 'ReadServiceStatus') -eq 'Service status: stopped') 'A reused PID is not reported as the daemon'

    Write-DaemonState 2147483000 $processStart.ToString('o') (Join-Path $fixture 'home\logs\bot.log')
    Assert-True ((Invoke-Runtime 'ReadServiceStatus') -eq 'Service status: stopped') 'A missing daemon PID is reported as stopped'

    $settings = New-Settings 'http://127.0.0.1:51234'
    $arguments = @($settings, $null)
    $reused = Invoke-Runtime 'TryReadRunningV2Service' $arguments
    Assert-True (-not $reused -and $null -eq $arguments[1]) 'No OpenCode service state means no reuse'

    Write-OpenCodeServiceState $PID 'http://127.0.0.1:51234'
    $arguments = @($settings, $null)
    $reused = Invoke-Runtime 'TryReadRunningV2Service' $arguments
    Assert-True ($reused -and $arguments[1].Port -eq 51234) 'A live service on the configured port is reused'

    Write-OpenCodeServiceState $PID 'http://127.0.0.1:51235'
    $arguments = @($settings, $null)
    Assert-True (-not (Invoke-Runtime 'TryReadRunningV2Service' $arguments)) 'A service on another port is not reused'

    Write-OpenCodeServiceState 2147483000 'http://127.0.0.1:51234'
    $arguments = @($settings, $null)
    Assert-True (-not (Invoke-Runtime 'TryReadRunningV2Service' $arguments)) 'A dead service PID is not reused'

    Write-OpenCodeServiceState $PID 'http://127.0.0.1:51234'
    $arguments = @((New-Settings 'http://10.0.0.5:51234'), $null)
    Assert-True (-not (Invoke-Runtime 'TryReadRunningV2Service' $arguments)) 'A remote OpenCode URL is never reused locally'

    $arguments = @($settings, $null)
    Assert-True (-not (Invoke-Runtime 'TryUseRunningV2Service' $arguments)) 'The health probe refuses an unreachable service'

    Write-Output 'No service, process or settings writes were performed.'
}
finally {
    [Environment]::SetEnvironmentVariable('OPENCODE_TELEGRAM_HOME', $previousHome, 'Process')
    [Environment]::SetEnvironmentVariable('XDG_STATE_HOME', $previousState, 'Process')
    Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
}
