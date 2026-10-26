[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$settings = [Activator]::CreateInstance($assembly.GetType('DesktopSettings'), $true)
$type = $assembly.GetType('DesktopApp', $true)
$ui = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($type)
$type.GetField('settings', $flags).SetValue($ui, $settings)
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Output "PASS: $Message"
}
Assert-True (-not $settings.Eot10Enabled) 'New settings leave encryption disabled'
foreach ($locale in @('zh', 'en')) {
    $settings.Locale = $locale
    foreach ($enabled in @($false, $true)) {
        $settings.Eot10Enabled = $enabled
        $content = New-Object Windows.Controls.StackPanel
        $type.GetField('content', $flags).SetValue($ui, $content)
        $type.GetMethod('BuildEncryptionSettings', $flags).Invoke($ui, @()) | Out-Null
        $checkbox = $type.GetField('eotEnabledBox', $flags).GetValue($ui)
        Assert-True ($checkbox.IsChecked -eq $enabled) "Encryption page preserves the saved choice ($locale, enabled=$enabled)"
        $notice = $content.Children[$content.Children.Count - 2]
        Assert-True ($notice.Text.Contains('EOT10') -and $notice.Text.Contains('Telegram')) "Compatibility warning is at the bottom ($locale)"
        if ($locale -eq 'en') { Assert-True ($notice.Text.Contains('standard Telegram app cannot')) 'English warning explains the customized-client requirement' }
        $download = $content.Children[$content.Children.Count - 1]
        $link = $download.Inlines.FirstInline
        Assert-True ($link.NavigateUri.AbsoluteUri -eq 'https://github.com/lidq4fun-commits/opencode_telegram_bot/releases/tag/desktop-1.0.15-android-11.0.22') "Bottom hyperlink targets the verified Android release ($locale)"
        Assert-True ($link.Inlines.FirstInline.Text.Contains('GitHub') -and $link.Inlines.FirstInline.Text.Contains('v11.0.22')) "Download link identifies the customized Android version ($locale)"
    }
}
