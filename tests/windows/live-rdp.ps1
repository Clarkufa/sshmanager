param(
    [Parameter(Mandatory)] [string] $Exe,
    [int] $Port = 3390,
    [string] $Container = 'sshm-xrdp',
    [string] $Distro = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$internal = [Reflection.BindingFlags]'Instance,NonPublic'
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
$desktopType = $asm.GetType('SshManager.RdpDesktop')
$profileType = $asm.GetType('SshManager.RdpProfile')
$cred = [Activator]::CreateInstance($asm.GetType('SshManager.RdpCredential'), [object[]]@('tester', '', 'Rdp-Test-2026'))
$certificate = ((wsl.exe -d $Distro -e docker exec $Container openssl x509 -in /etc/xrdp/cert.pem -noout -fingerprint -sha256) -join '').Trim()
if ($LASTEXITCODE -ne 0 -or $certificate -notmatch '=(.+)$') { throw 'Could not read the test server certificate.' }
$expectedFingerprint = $Matches[1]
wsl.exe -d $Distro -e docker exec $Container test -c /dev/fuse
if ($LASTEXITCODE -ne 0) { throw 'The folder test requires xrdp with /dev/fuse. See tests/xrdp/README.md.' }
$marker = 'sshm-' + [Guid]::NewGuid().ToString('N') + '.txt'
$folder = Join-Path $env:TEMP ('sshm-share-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $folder
Set-Content -LiteralPath (Join-Path $folder $marker) -Value 'RDP folder redirection'

function Wait-For([string] $label, [scriptblock] $condition) {
    $end = [DateTime]::UtcNow.AddSeconds(35)
    while ([DateTime]::UtcNow -lt $end) {
        [Windows.Forms.Application]::DoEvents()
        if (& $condition) { "ok    $label"; return }
        Start-Sleep -Milliseconds 40
    }
    throw "$label timed out ($($desktop.Status))."
}

function Make-Desktop {
    $panel = [Activator]::CreateInstance($desktopType)
    $desktopType.GetField('ConfirmCertificate', $internal).SetValue($panel, [Func[string,string,bool]]{
        param($serverAddress, $fingerprint)
        ($serverAddress -eq "127.0.0.1:$Port") -and ($fingerprint -eq $expectedFingerprint)
    })
    return $panel
}

$options = [Activator]::CreateInstance($asm.GetType('SshManager.RdpOptions'), [object[]]@([string[]]@($folder), $true, $false, '', ''))
$profile = [Activator]::CreateInstance($profileType, [object[]]@([Guid]::NewGuid().ToString('N'),
    'xrdp', '127.0.0.1', $Port, 'tester', '', $null, 1000, 640, $true, $false, $options))
$otherProfile = [Activator]::CreateInstance($profileType, [object[]]@([Guid]::NewGuid().ToString('N'),
    'xrdp independent session', '127.0.0.1', $Port, 'tester', '', $null, 800, 600, $false, $false, $null))
$form = New-Object Windows.Forms.Form
$form.StartPosition = 'Manual'
$form.Location = New-Object Drawing.Point(-30000, -30000)
$form.ClientSize = New-Object Drawing.Size(1000, 640)
$form.ShowInTaskbar = $false
$desktop = Make-Desktop
$other = Make-Desktop
$form.Controls.Add($other)
$form.Controls.Add($desktop)
$form.Show()
try {
    $desktop.Connect($profile, $cred)
    Wait-For 'connected with clipboard, printer and a folder configured' {
        $desktop.IsConnected -and $desktop.DesktopSize.Width -gt 0
    }
    $mountDeadline = [DateTime]::UtcNow.AddSeconds(20)
    $found = $false
    while (-not $found -and [DateTime]::UtcNow -lt $mountDeadline) {
        [Windows.Forms.Application]::DoEvents()
        $found = (wsl.exe -d $Distro -e docker exec -u tester $Container sh -c "test -f /home/tester/thinclient_drives/Folder1/$marker && echo found") -contains 'found'
        if (-not $found) { Start-Sleep -Milliseconds 350 }
    }
    if (-not $found) { throw 'The selected local folder was not mounted inside xrdp.' }
    'ok    selected folder mounted on the server'

    $other.Connect($otherProfile, $cred)
    Wait-For 'second session connected' { $other.IsConnected -and $other.DesktopSize.Width -gt 0 }
    $firstWorker = $desktopType.GetField('worker', $internal).GetValue($desktop)
    $secondWorker = $desktopType.GetField('worker', $internal).GetValue($other)
    if ($firstWorker.ProcessId -eq $secondWorker.ProcessId) { throw 'The two sessions share a worker.' }
    Stop-Process -Id $firstWorker.ProcessId
    Wait-For 'first session ended without stopping the second' {
        -not $desktop.IsOpen -and $other.IsConnected
    }
    $other.Disconnect()
    Wait-For 'second session disconnected normally' { -not $other.IsOpen }
}
finally {
    $form.Dispose()
    Remove-Item -LiteralPath (Join-Path $folder $marker) -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $folder -ErrorAction SilentlyContinue
}
'failures: 0'
