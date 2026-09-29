$ErrorActionPreference = 'Stop'

$launcherPath = Join-Path $PSScriptRoot 'OpenCodeTelegram-v11.0.16.exe'
if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "Latest launcher not found: $launcherPath"
}

# Enable the existing DPAPI-protected setting without simulating console keystrokes.
$settingsPath = Join-Path $env:APPDATA 'opencode-telegram-bot\eot11-settings.dpapi'
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw 'No saved EOT10 settings found. Set a password once with the launcher settings menu first.'
}

Add-Type -AssemblyName System.Security
$entropy = [Text.Encoding]::UTF8.GetBytes('OpenCodeTelegram-v11-settings')
$payload = $null
$protectedPayload = $null
$tempSettingsPath = "$settingsPath.tmp"
try {
    $payload = [Security.Cryptography.ProtectedData]::Unprotect(
        [IO.File]::ReadAllBytes($settingsPath),
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    if ($payload.Length -lt 2) {
        throw 'Saved EOT10 settings do not contain a password.'
    }
    $passwordChars = [Text.Encoding]::UTF8.GetChars($payload, 1, $payload.Length - 1)
    try {
        if ($passwordChars.Length -lt 12) {
            throw 'The saved EOT10 password is missing or shorter than 12 characters.'
        }
    }
    finally {
        [Array]::Clear($passwordChars, 0, $passwordChars.Length)
    }

    $payload[0] = 1
    $protectedPayload = [Security.Cryptography.ProtectedData]::Protect(
        $payload,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    [IO.File]::WriteAllBytes($tempSettingsPath, $protectedPayload)
    Move-Item -LiteralPath $tempSettingsPath -Destination $settingsPath -Force
    Write-Host 'EOT10 enabled in the current-user DPAPI settings.'
}
finally {
    if ($payload) { [Array]::Clear($payload, 0, $payload.Length) }
    if ($protectedPayload) { [Array]::Clear($protectedPayload, 0, $protectedPayload.Length) }
    [Array]::Clear($entropy, 0, $entropy.Length)
    Remove-Item -LiteralPath $tempSettingsPath -Force -ErrorAction SilentlyContinue
}

# Close existing launcher windows gracefully so their exit handler can stop the old Bot/server.
$oldLaunchers = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -like 'OpenCodeTelegram-v*'
})
foreach ($old in $oldLaunchers) {
    Write-Host "Closing old launcher PID $($old.Id)..."
    try {
        if (-not $old.CloseMainWindow()) {
            Write-Host "Old launcher did not accept close request; stopping PID $($old.Id)."
            Stop-Process -Id $old.Id -Force -ErrorAction SilentlyContinue
        }
        elseif (-not $old.WaitForExit(15000)) {
            Write-Host "Old launcher did not exit in time; stopping PID $($old.Id)."
            Stop-Process -Id $old.Id -Force -ErrorAction SilentlyContinue
        }
    }
    catch {
        Stop-Process -Id $old.Id -Force -ErrorAction SilentlyContinue
    }
}

Start-Sleep -Seconds 2
$newLauncher = Start-Process -FilePath $launcherPath -WorkingDirectory $PSScriptRoot -PassThru
Write-Host "Started OpenCodeTelegram-v11.0.16.exe, PID $($newLauncher.Id)."
Write-Host 'The launcher will load the enabled setting automatically. Leave its window running.'
