# Run from an STA Windows PowerShell. Full screen covers the monitor during its check.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [int] $Port = 3390,
    [int] $FixedWidth = 0,
    [int] $FixedHeight = 0,
    [string] $Container = 'sshm-xrdp',
    [string] $Distro = 'Ubuntu',
    [switch] $WindowOnly
)

$ErrorActionPreference = 'Stop'
$nativeWsl = Join-Path $env:WINDIR 'Sysnative/wsl.exe'
$wsl = if (Test-Path -LiteralPath $nativeWsl) { $nativeWsl } else { 'wsl.exe' }
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
$desktopType = $asm.GetType('SshManager.RdpDesktop')
$internal = [Reflection.BindingFlags]'Instance,NonPublic'
$profileType = $asm.GetType('SshManager.RdpProfile')
$credType = $asm.GetType('SshManager.RdpCredential')
$profile = [Activator]::CreateInstance($profileType, [object[]]@([Guid]::NewGuid().ToString('N'),
    'xrdp', '127.0.0.1', $Port, 'tester', '', $null, $FixedWidth, $FixedHeight, $false, $false, $null))
$cred = [Activator]::CreateInstance($credType, [object[]]@('tester', '', 'Rdp-Test-2026'))
$certificate = ((& $wsl -d $Distro -e docker exec $Container openssl x509 -in /etc/xrdp/cert.pem -noout -fingerprint -sha256) -join '').Trim()
if ($LASTEXITCODE -ne 0 -or $certificate -notmatch '=(.+)$') { throw 'Could not read the test server certificate.' }
$expectedFingerprint = $Matches[1]

function Wait-For([string] $description, [scriptblock] $condition, [int] $seconds = 35) {
    $end = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $end) {
        [Windows.Forms.Application]::DoEvents()
        if (& $condition) { "ok    $description"; return }
        if (-not $desktop.IsOpen) { break }
        Start-Sleep -Milliseconds 30
    }
    $reason = $desktopType.BaseType.GetField('overlayDetail', $internal).GetValue($desktop).Text
    throw "$description failed ($($desktop.Status), frame $($desktop.DesktopSize)). $reason"
}

function Check-Size([string] $label, [int] $width, [int] $height) {
    Wait-For "$label $width x $height" {
        $desktop.DesktopSize.Width -eq $width -and $desktop.DesktopSize.Height -eq $height
    }
    $remote = (& $wsl -d $Distro -e docker exec -u tester $Container remote-size) -join ' '
    if ($remote -notmatch "\b$width\s*x\s*$height\b") { throw "Server resolution differs ($remote)." }
}

$form = New-Object Windows.Forms.Form
$form.StartPosition = 'Manual'
$form.Location = New-Object Drawing.Point(-30000, -30000)
$form.ShowInTaskbar = $false
$form.ClientSize = New-Object Drawing.Size(1000, 640)
$desktop = [Activator]::CreateInstance($desktopType)
$desktopType.GetField('ConfirmCertificate', $internal).SetValue($desktop, [Func[string,string,bool]]{
    param($serverAddress, $fingerprint)
    ($serverAddress -eq "127.0.0.1:$Port") -and ($fingerprint -eq $expectedFingerprint)
})
$form.Controls.Add($desktop)
$form.Show()
try {
    $desktop.Connect($profile, $cred)
    Wait-For 'FreeRDP connected and rendered' { $desktop.IsConnected -and $desktop.DesktopSize.Width -gt 0 }
    Wait-For 'remote desktop pixels arrived' {
        $bitmap = $desktopType.GetField('frame', $internal).GetValue($desktop)
        foreach ($column in 1..8) {
            foreach ($row in 1..8) {
                $pixel = $bitmap.GetPixel([int]($bitmap.Width * $column / 9), [int]($bitmap.Height * $row / 9))
                if ($pixel.R -or $pixel.G -or $pixel.B) { return $true }
            }
        }
        return $false
    }
    if ($FixedWidth) {
        Check-Size 'fixed' $FixedWidth $FixedHeight
    } else {
        Check-Size 'initial' 1000 640
    }
    $form.ClientSize = New-Object Drawing.Size(1200, 760)
    if ($FixedWidth) {
        Check-Size 'fixed after resize' $FixedWidth $FixedHeight
    } else {
        Check-Size 'resized' 1200 760
    }
    if (-not $WindowOnly) {
        $monitor = [Windows.Forms.Screen]::FromControl($desktop).Bounds.Size
        $desktop.FullScreen()
        Check-Size 'full screen' $monitor.Width $monitor.Height
        $desktopType.GetField('fullScreenForm', $internal).GetValue($desktop).Close()
        if ($FixedWidth) { Check-Size 'fixed after full screen' $FixedWidth $FixedHeight }
        else { Check-Size 'window after full screen' 1200 760 }
    }
    $worker = $desktopType.GetField('worker', $internal).GetValue($desktop)
    Stop-Process -Id $worker.ProcessId
    Wait-For 'lost worker ends only the session' { -not $desktop.IsOpen }
    if ($form.IsDisposed) { throw 'The application window closed with the RDP worker.' }
    $desktop.Reconnect()
    Wait-For 'replacement worker connects' { $desktop.IsConnected -and $desktop.DesktopSize.Width -gt 0 }
    $desktop.Disconnect()
    Wait-For 'user disconnect' { -not $desktop.IsOpen }
}
finally { $form.Dispose() }
