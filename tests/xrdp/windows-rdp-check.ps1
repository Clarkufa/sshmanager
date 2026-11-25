# Connects the Windows app's embedded RDP client to the xrdp test server and checks that the
# remote resolution follows the window, full screen and back. Run in Windows PowerShell (STA):
#   powershell -STA -File tests\xrdp\windows-rdp-check.ps1 -Exe build\release\SshManager.exe
# Full screen covers the monitor for a few seconds.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [int] $Port = 3390,
    [int] $FixedWidth = 0,
    [int] $FixedHeight = 0,
    [string] $Container = 'sshm-xrdp',
    [string] $Distro = 'Ubuntu'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
$desktopType = $asm.GetType('SshManager.RdpDesktop')
# The test server has a self-signed certificate: connect without the identity warning.
$desktopType.GetField('ServerAuthentication', [Reflection.BindingFlags]'Static,NonPublic,Public').SetValue($null, [uint32]0)
$profileType = $asm.GetType('SshManager.RdpProfile'); $credType = $asm.GetType('SshManager.RdpCredential')
$profile = [Activator]::CreateInstance($profileType, [object[]]@([Guid]::NewGuid().ToString('N'), 'xrdp', '127.0.0.1', $Port,
    'tester', '', $null, $FixedWidth, $FixedHeight, $false, $false))
$cred = [Activator]::CreateInstance($credType, [object[]]@('tester', '', 'Rdp-Test-2026'))

function Pump([double]$seconds) {
    $end = [DateTime]::Now.AddSeconds($seconds)
    while ([DateTime]::Now -lt $end) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
}
function Remote-Size { (wsl.exe -d $Distro -e docker exec -u tester $Container remote-size) -join ' ' }

$form = New-Object Windows.Forms.Form
$form.StartPosition = 'Manual'; $form.Location = New-Object Drawing.Point(40, 40); $form.ClientSize = New-Object Drawing.Size(1000, 640)
$desktop = [Activator]::CreateInstance($desktopType)
$form.Controls.Add($desktop); $form.Show(); Pump 0.5
try {
    $desktop.Connect($profile, $cred)
    $end = [DateTime]::Now.AddSeconds(60)
    while (-not $desktop.IsConnected -and $desktop.IsOpen -and [DateTime]::Now -lt $end) { Pump 0.2 }
    "status: " + $desktop.Status
    if (-not $desktop.IsConnected) { throw 'RDP session did not connect.' }
    Pump 6
    "window $($desktop.ClientSize) -> remote $(Remote-Size)"
    $form.ClientSize = New-Object Drawing.Size(1200, 760); Pump 5
    "resized $($desktop.ClientSize) -> remote $(Remote-Size)"
    $desktop.FullScreen(); Pump 6
    "full screen $([Windows.Forms.Screen]::FromControl($desktop).Bounds.Size) -> remote $(Remote-Size)"
    $client = $desktopType.GetField('client', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($desktop)
    [void]$client.GetType().InvokeMember('FullScreen', [Reflection.BindingFlags]::SetProperty, $null, $client, @($false)); Pump 6
    "left full screen $($desktop.ClientSize) -> remote $(Remote-Size)"
    $desktop.Disconnect(); Pump 2
}
finally { $form.Dispose() }
