param([Parameter(Mandatory)] [string] $Exe)
# Embedded RDP with «Дополнительно» options against the tests/xrdp bench (test account only).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$NP = [Reflection.BindingFlags]'Instance,Static,NonPublic,Public'
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
$desktopType = $asm.GetType('SshManager.RdpDesktop')
$desktopType.GetField('ServerAuthentication', $NP).SetValue($null, [uint32]0)
$folder = Join-Path $env:TEMP ('sshm-share-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $folder | Out-Null
$options = [Activator]::CreateInstance($asm.GetType('SshManager.RdpOptions'), $NP, $null, [object[]]@([string[]]@($folder), $true, $false, '', ''), $null)
$profile = [Activator]::CreateInstance($asm.GetType('SshManager.RdpProfile'), $NP, $null, [object[]]@([Guid]::NewGuid().ToString('N'), 'xrdp', '127.0.0.1', 3390,
    'tester', '', $null, 0, 0, $true, $false, $options), $null)
$cred = [Activator]::CreateInstance($asm.GetType('SshManager.RdpCredential'), [object[]]@('tester', '', 'Rdp-Test-2026'))
function Pump([double]$seconds) { $end = [DateTime]::Now.AddSeconds($seconds); while ([DateTime]::Now -lt $end) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 } }
$failures = 0
function Check([string] $what, $condition) { if ($condition) { "ok    $what" } else { "FAIL  $what"; $script:failures++ } }

$form = New-Object Windows.Forms.Form
$form.StartPosition = 'Manual'; $form.Location = New-Object Drawing.Point(-30000, -30000); $form.ClientSize = New-Object Drawing.Size(1000, 640); $form.ShowInTaskbar = $false
$desktop = [Activator]::CreateInstance($desktopType)
$form.Controls.Add($desktop); $form.Show(); Pump 0.5
try {
    $desktop.Connect($profile, $cred)
    # Read back what the client will redirect: the same COM interface the app uses.
    $client = $desktopType.GetField('client', $NP).GetValue($desktop)
    $iface = $desktopType.GetNestedType('IMsRdpClientNonScriptable3', $NP)
    $args1 = [object[]]@($null); [void]$iface.GetMethod('GetDriveCollection').Invoke($client, $args1); $drives = $args1[0]
    $driveIface = $desktopType.GetNestedType('IMsRdpDriveCollection', $NP); $oneIface = $desktopType.GetNestedType('IMsRdpDrive', $NP)
    $countArgs = [object[]]@([uint32]0); [void]$driveIface.GetMethod('GetDriveCount').Invoke($drives, $countArgs)
    $states = @()
    for ($i = 0; $i -lt $countArgs[0]; $i++) {
        $a = [object[]]@([uint32]$i, $null); [void]$driveIface.GetMethod('GetDriveByIndex').Invoke($drives, $a)
        $n = [object[]]@($null); [void]$oneIface.GetMethod('GetName').Invoke($a[1], $n)
        $st = [object[]]@([int16]0); [void]$oneIface.GetMethod('GetRedirectionState').Invoke($a[1], $st)
        $states += "$($n[0])=$($st[0])"
    }
    "drives: " + ($states -join ', ')
    $letter = $folder.Substring(0, 1).ToUpper()
    Check "drive ${letter}: redirected, others not" (($states | Where-Object { $_ -match "\($letter`:\)|^$letter`:" }) -match '=-1$' -and -not ($states | Where-Object { $_ -notmatch "\($letter`:\)|^$letter`:" -and $_ -match '=-1$' }))
    $settings = $client.GetType().InvokeMember('AdvancedSettings8', [Reflection.BindingFlags]::GetProperty, $null, $client, $null)
    Check 'printers redirected' ($settings.GetType().InvokeMember('RedirectPrinters', [Reflection.BindingFlags]::GetProperty, $null, $settings, $null) -eq $true)
    $end = [DateTime]::Now.AddSeconds(60)
    while (-not $desktop.IsConnected -and $desktop.IsOpen -and [DateTime]::Now -lt $end) { Pump 0.2 }
    Check "session connected with the options ($($desktop.Status))" $desktop.IsConnected
    Pump 8
    "server side: " + ((wsl.exe -e sh -c "docker exec sshm-xrdp sh -c 'ls /home/tester/thinclient_drives 2>&1; pgrep -l chansrv'") -join ' | ')
    $desktop.Disconnect(); Pump 2
}
finally { $form.Dispose(); Remove-Item -Recurse -Force $folder -ErrorAction SilentlyContinue }
"failures: $failures"
