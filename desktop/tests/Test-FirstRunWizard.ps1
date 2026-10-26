[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName System.Runtime.Serialization
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$settingsType = $assembly.GetType('DesktopSettings', $true)
$uiType = $assembly.GetType('DesktopApp', $true)
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Output "PASS: $Message"
}
foreach ($locale in @('zh', 'en')) {
    foreach ($step in 0..3) {
        $settings = [Activator]::CreateInstance($settingsType, $true)
        $settings.Locale = $locale
        $settings.SetupStep = $step
        Assert-True (-not $settings.SetupCompleted) 'New settings require the wizard'
        $ui = [Activator]::CreateInstance($uiType, $true)
        $uiType.GetField('settings', $flags).SetValue($ui, $settings)
        $window = $uiType.GetMethod('CreateWindow', $flags).Invoke($ui, @())
        $uiType.GetField('window', $flags).SetValue($ui, $window)
        $content = $uiType.GetField('content', $flags).GetValue($ui)
        $sidebar = $uiType.GetField('sidebar', $flags).GetValue($ui)
        Assert-True (-not $sidebar.IsEnabled) "Wizard prevents leaving incomplete setup ($locale, step=$step)"
        Assert-True ($content.Children[0].Text.Contains("$($step + 1) / 4")) "Wizard resumes the saved step ($locale, step=$step)"
        Assert-True ($content.Children[1].Text.Length -gt 100) "Wizard includes actionable instructions ($locale, step=$step)"
        if ($step -eq 1) { Assert-True ($content.Children[1].Text.Contains('@BotFather') -and $content.Children[1].Text.Contains('@userinfobot')) 'Bot binding identifies where to obtain both inputs' }
        if ($step -eq 3) { Assert-True ($content.Children[1].Text.Contains('V2')) 'Engine step explains selection' }
    }
    $settings.SetupCompleted = $true
    $serializer = New-Object Runtime.Serialization.Json.DataContractJsonSerializer($settingsType)
    $stream = New-Object IO.MemoryStream
    try {
        $serializer.WriteObject($stream, $settings)
        $stream.Position = 0
        $restored = $serializer.ReadObject($stream)
        Assert-True $restored.SetupCompleted 'Completion survives settings serialization'
    } finally { $stream.Dispose() }
    $ui = [Activator]::CreateInstance($uiType, $true)
    $uiType.GetField('settings', $flags).SetValue($ui, $restored)
    $window = $uiType.GetMethod('CreateWindow', $flags).Invoke($ui, @())
    Assert-True (-not $uiType.GetField('wizardActive', $flags).GetValue($ui)) 'Completed setup does not show the wizard again'
    $content = $uiType.GetField('content', $flags).GetValue($ui)
    $statusText = $uiType.GetField('statusText', $flags).GetValue($ui)
    Assert-True ($content.Children[0] -isnot [Windows.Controls.ProgressBar] -and $statusText -ne $null) 'Later launches show the usable dashboard immediately'
    Assert-True ($statusText.Text -match 'OpenCode') 'The dashboard status explains that startup is still running'
    $selectedPage = $uiType.GetField('selectedPage', $flags).GetValue($ui)
    Assert-True ($selectedPage -eq 0) 'Startup keeps the dashboard page selected'
}
Write-Output 'No login, services, registry changes or settings writes were performed.'
