[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$AssemblyPath)

$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$type = $assembly.GetType('BrowserLoginRedirect', $true)
$constructor = $type.GetConstructor([Type[]]@([Uri], [string[]]))
$target = [Uri]'http://127.0.0.1:49374/server/test/session/ses_test?keep=yes#part'
$cookie = 'opencode_session_test=test_session_value; Path=/; HttpOnly; SameSite=Lax; Max-Age=120'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Output "PASS: $Message"
}
function New-Redirect([Uri]$Target, [string[]]$Cookies) {
    $arguments = New-Object object[] 2
    $arguments[0] = $Target.PSObject.BaseObject
    $arguments[1] = $Cookies.PSObject.BaseObject
    return $constructor.Invoke($arguments)
}

$login = New-Redirect $target @($cookie)
try {
    Assert-True ($login.Address.IsLoopback -and $login.Address.Port -ne $target.Port) 'Login redirect binds only to a temporary loopback port'
    $complete = $login.CompleteAsync()

    $client = New-Object Net.Sockets.TcpClient
    $client.Connect('127.0.0.1', $login.Address.Port)
    $stream = $client.GetStream()
    $stream.ReadTimeout = 3000
    $request = [Text.Encoding]::ASCII.GetBytes("GET $($login.Address.AbsolutePath) HTTP/1.1`r`nHost: evil.invalid`r`n`r`n")
    $stream.Write($request, 0, $request.Length)
    $reader = New-Object IO.StreamReader($stream)
    $rejected = $reader.ReadToEnd()
    $reader.Dispose()
    $client.Dispose()
    Assert-True ($rejected.StartsWith('HTTP/1.1 404') -and $rejected -notmatch 'Set-Cookie') 'A mismatched Host cannot receive a login cookie'

    $request = [Net.HttpWebRequest]::Create($login.Address)
    $request.Proxy = $null
    $request.Timeout = 5000
    $request.AllowAutoRedirect = $false
    $request.CookieContainer = New-Object Net.CookieContainer
    $response = $request.GetResponse()
    try {
        Assert-True ([int]$response.StatusCode -eq 303) 'Login redirects to the requested workspace'
        Assert-True ($response.Headers['Location'] -eq $target.AbsoluteUri) 'Project, session, query and fragment are preserved'
        Assert-True ($response.Headers['Location'] -notmatch 'auth_token|test_session_value') 'Redirect URL contains no login token'
        Assert-True ($response.Headers['Set-Cookie'] -match 'HttpOnly' -and $response.Headers['Set-Cookie'] -match 'SameSite=Lax') 'Native cookie security flags are preserved'
        Assert-True ($response.Headers['Cache-Control'] -eq 'no-store' -and $response.Headers['Referrer-Policy'] -eq 'no-referrer') 'Login responses cannot be cached or leak the one-use URL as a referrer'
        Assert-True ($request.CookieContainer.GetCookies($target).Count -eq 1) 'Browser cookie remains available on the original service port after redirect and refresh'
    } finally { $response.Dispose() }
    [void]$complete.GetAwaiter().GetResult()
    $probe = New-Object Net.Sockets.TcpClient
    $closed = $false
    try { $probe.Connect('127.0.0.1', $login.Address.Port) } catch { $closed = $true } finally { $probe.Dispose() }
    Assert-True $closed 'The temporary redirect stops immediately after one successful login'
} finally { $login.Dispose() }

$rejected = $false
try { $unexpected = New-Redirect ([Uri]'https://example.invalid/') @($cookie); $unexpected.Dispose() } catch { $rejected = $true }
Assert-True $rejected 'Non-loopback targets cannot start a cookie redirect'
$rejected = $false
try { $unexpected = New-Redirect $target @("cookie=x`r`nInjected: value"); $unexpected.Dispose() } catch { $rejected = $true }
Assert-True $rejected 'Cookie header injection is rejected before binding a port'
$settings = [Activator]::CreateInstance($assembly.GetType('DesktopSettings'), $true)
$settings.OpenCodeServerVersion = 'v2'
$settings.OpenCodeUrl = $target.GetLeftPart([UriPartial]::Authority)
$arguments = New-Object object[] 2
$arguments[0] = [Uri]'http://127.0.0.1:49375/'
$arguments[1] = $settings.PSObject.BaseObject
$rejected = $false
try {
    $prepare = $assembly.GetType('BotRuntime').GetMethod('PrepareBrowserLogin', [Reflection.BindingFlags]'Public,Static').Invoke($null, $arguments)
    $unexpected = $prepare.GetAwaiter().GetResult()
    $unexpected.Dispose()
} catch { $rejected = $_.Exception.ToString().Contains('must match the configured OpenCode service') }
Assert-True $rejected 'Login credentials cannot be forwarded to a different local service'
Write-Output 'All browser login checks passed; no browser was opened and no real credential was used.'
