[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$AssemblyPath)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$appType = $assembly.GetType('DesktopApp', $true)
$classify = $appType.GetMethod('ClassifyTelegramLoginFailure', [Reflection.BindingFlags]'Public,NonPublic,Static')

function Assert-Status([string]$Text, [int]$ExitCode, [string]$Expected, [string]$Message) {
    $result = $classify.Invoke($null, @($Text, $ExitCode))
    $status = $result.GetType().GetField('Status').GetValue($result).ToString()
    if ($status -ne $Expected) { throw "FAIL: $Message (expected $Expected, got $status)" }
    Write-Output "PASS: $Message"
}

Assert-Status 'rpc error: code = Unavailable desc = connection error' 1 'Network' 'connection errors are treated as network failures'
Assert-Status 'context deadline exceeded' 1 'Network' 'timeouts are treated as network failures'
Assert-Status 'AUTH_KEY_UNREGISTERED' 1 'NotLoggedIn' 'missing auth keys are treated as signed out'
Assert-Status 'not authorized' 1 'NotLoggedIn' 'authorization failures are treated as signed out'
Assert-Status 'something unexpected' 2 'Unavailable' 'unknown failures stay neutral instead of claiming a mismatch'

Write-Output 'All Telegram login classification checks passed.'
