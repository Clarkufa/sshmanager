param(
    [Parameter(Mandatory)] [string] $Exe,
    [int] $Port = 3390,
    [string] $Container = 'sshm-xrdp',
    [string] $Distro = 'Ubuntu'
)
# Cyrillic and emoji through the embedded RDP clipboard in both directions (run with -STA).
# The Windows clipboard text is saved first and put back at the end.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$internal = [Reflection.BindingFlags]'Instance,NonPublic'
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
$desktopType = $asm.GetType('SshManager.RdpDesktop')
$cred = [Activator]::CreateInstance($asm.GetType('SshManager.RdpCredential'), [object[]]@('tester', '', 'Rdp-Test-2026'))
$certificate = ((wsl.exe -d $Distro -e docker exec $Container openssl x509 -in /etc/xrdp/cert.pem -noout -fingerprint -sha256) -join '').Trim()
if ($certificate -notmatch '=(.+)$') { throw 'Could not read the test server certificate.' }
$expectedFingerprint = $Matches[1]
wsl.exe -d $Distro -e docker exec -u root $Container sh -c 'command -v xclip >/dev/null || (apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq xclip >/dev/null)'

function Wait-For([string] $label, [scriptblock] $condition, [int] $seconds = 35) {
    $end = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $end) {
        [Windows.Forms.Application]::DoEvents()
        if (& $condition) { "ok    $label"; return }
        Start-Sleep -Milliseconds 150
    }
    throw "$label timed out. Last value: $script:last"
}

function Server([string] $command) {
    $display = (wsl.exe -d $Distro -e docker exec $Container sh -c 'ps -eo args | grep ^/usr/lib/xorg/Xorg | grep -o :[0-9]* | head -1')
    wsl.exe -d $Distro -e docker exec -u tester -e "DISPLAY=$display" -e XAUTHORITY=/home/tester/.Xauthority $Container sh -c $command
}

$profile = [Activator]::CreateInstance($asm.GetType('SshManager.RdpProfile'), [object[]]@([Guid]::NewGuid().ToString('N'),
    'xrdp', '127.0.0.1', $Port, 'tester', '', $null, 1000, 640, $true, $false, $null))
$form = New-Object Windows.Forms.Form
$form.StartPosition = 'Manual'
$form.Location = New-Object Drawing.Point(-30000, -30000)
$form.ClientSize = New-Object Drawing.Size(1000, 640)
$form.ShowInTaskbar = $false
$desktop = [Activator]::CreateInstance($desktopType)
$desktopType.GetField('ConfirmCertificate', $internal).SetValue($desktop, [Func[string,string,bool]]{
    param($serverAddress, $fingerprint) $fingerprint -eq $expectedFingerprint })
$form.Controls.Add($desktop)
$form.Show()
$saved = [Windows.Forms.Clipboard]::GetText()
try {
    $desktop.Connect($profile, $cred)
    Wait-For 'connected' { $desktop.IsConnected -and $desktop.DesktopSize.Width -gt 0 }
    Start-Sleep -Seconds 3

    $outgoing = "Windows → сервер`nПривет, мир 🌍"
    [Windows.Forms.Clipboard]::SetText($outgoing)
    Wait-For 'Windows to xrdp: Cyrillic and emoji' {
        $base64 = Server 'timeout 3 xclip -selection clipboard -out 2>&1 | base64 -w0'
        $script:last = if ($base64) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($base64)) } else { '<empty>' }
        $base64 -and ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($base64)) -replace "`r`n", "`n") -eq $outgoing
    } 20

    $incoming = "Сервер → Windows`nКириллица и emoji 🙂"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($incoming))
    Server "printf %s $encoded | base64 -d | xclip -selection clipboard -in >/dev/null 2>&1"
    Wait-For 'xrdp to Windows: Cyrillic and emoji' { ([Windows.Forms.Clipboard]::GetText() -replace "`r`n", "`n") -eq $incoming } 20
    'failures: 0'
} finally {
    $desktop.Disconnect()
    $form.Dispose()
    if ($saved) { [Windows.Forms.Clipboard]::SetText($saved) } else { [Windows.Forms.Clipboard]::Clear() }
}
