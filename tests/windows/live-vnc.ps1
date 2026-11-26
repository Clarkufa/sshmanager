param([Parameter(Mandatory)] [string] $Exe, [string] $Shots = 'build/vnc-shots')
# Live VNC checks against the tests/vnc bench (VNC 127.0.0.1:5901, password VncTest1; SSH 127.0.0.1:2224, tester).
# The tunnel check uses a temporary ssh config and key, so the user's own ~/.ssh is not touched.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$NP = [Reflection.BindingFlags]'Instance,Static,NonPublic,Public'
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
function T($name) { $asm.GetType('SshManager.' + $name, $true) }
function Unwrap([object[]] $a) { $list = New-Object 'System.Collections.Generic.List[object]'; foreach ($x in $a) { if ($x -is [psobject]) { $list.Add($x.psobject.BaseObject) } else { $list.Add($x) } }; ,$list.ToArray() }
function New($name, [object[]] $a) { [Activator]::CreateInstance((T $name), $NP, $null, (Unwrap $a), $null) }
function Call($o, $name, [object[]] $a = @()) { if ($o -is [Type]) { $o.GetMethod($name, $NP).Invoke($null, (Unwrap $a)) } else { $o.GetType().GetMethod($name, $NP).Invoke($o, (Unwrap $a)) } }
function Field($o, $name) { $o.GetType().GetField($name, $NP).GetValue($o) }
$failures = 0
function Check([string] $what, $condition) { if ($condition) { "ok    $what" } else { "FAIL  $what"; $script:failures++ } }
function Pump([double] $seconds) { $end = [DateTime]::Now.AddSeconds($seconds); do { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 25 } while ([DateTime]::Now -lt $end) }
function Wait([scriptblock] $condition, [double] $seconds) { $end = [DateTime]::Now.AddSeconds($seconds); while ([DateTime]::Now -lt $end) { Pump 0.2; if (& $condition) { return $true } }; return [bool](& $condition) }
function RemoteSize { ((wsl.exe -e sh -c "docker exec -u tester sshm-vnc sh -c 'DISPLAY=:1 xrandr --current | head -1'") -join ' ') -replace '.*current (\d+) x (\d+).*', '$1x$2' }
New-Item -ItemType Directory -Force $Shots | Out-Null

# --- known-answer test for the VNC challenge response (RFC 6143 VNC Authentication, password "password")
$challenge = [byte[]](0..15)
$response = Call (T 'VncClient') 'EncryptChallenge' @($challenge, 'password')
Check "DES challenge response has 16 bytes" ($response.Length -eq 16)
Check "different passwords give different responses" (-not [Linq.Enumerable]::SequenceEqual([byte[]]$response, [byte[]](Call (T 'VncClient') 'EncryptChallenge' @($challenge, 'Password'))))

function Profile([string] $via = '') {
    New 'VncProfile' @([Guid]::NewGuid().ToString('N'), 'bench', '127.0.0.1', 5901, '', $null, 'fit', $false, $true, $false, $via)
}
function Open-Desktop($profile, [string] $password, [scriptblock] $prompt = $null) {
    $form = New-Object Windows.Forms.Form; $form.ClientSize = New-Object Drawing.Size(1000, 640)
    $form.StartPosition = 'Manual'; $form.Location = New-Object Drawing.Point(-30000, -30000); $form.ShowInTaskbar = $false
    $desktop = New 'VncDesktop' @()
    if ($prompt) { $desktop.GetType().GetField('PasswordPrompt').SetValue($desktop, ($prompt -as [Func`3].MakeGenericType((T 'VncProfile'), [bool], [string]))) }
    $form.Controls.Add($desktop); $form.Show(); Pump 0.3
    $desktop.Connect($profile, $password)
    @{ Form = $form; Desktop = $desktop }
}

# --- 1. password, resize to the window, a real picture
$s = Open-Desktop (Profile) 'VncTest1'
Check "connects with the password" (Wait { $s.Desktop.IsConnected } 20)
Check "status «подключён»" ($s.Desktop.Status -eq 'подключён')
$client = Field $s.Desktop 'client'
Check "server announced ExtendedDesktopSize" $client.CanResize
$want = "$($s.Desktop.ClientSize.Width)x$($s.Desktop.ClientSize.Height)"
Check "server desktop follows the window ($want)" (Wait { (RemoteSize) -eq $want } 15)
Pump 3
$frame = $client.Frame
$colours = New-Object 'System.Collections.Generic.HashSet[int]'
for ($i = 0; $i -lt $frame.Pixels.Length; $i += 997) { [void]$colours.Add($frame.Pixels[$i]) }
Check "framebuffer has a picture ($($colours.Count) colours sampled)" ($colours.Count -gt 5)
$frame.Bitmap.Save((Join-Path $Shots 'vnc-frame.png'))
$s.Form.ClientSize = New-Object Drawing.Size(1200, 760); Pump 0.5
$want = "$($s.Desktop.ClientSize.Width)x$($s.Desktop.ClientSize.Height)"
Check "resize follows ($want)" (Wait { (RemoteSize) -eq $want } 15)
# --- Hextile and Raw (the server picks ZRLE when it may)
foreach ($encoding in @(@{ Name = 'Hextile'; List = [int[]](5, 1, 0) }, @{ Name = 'Raw'; List = [int[]](0) })) {
    (T 'VncClient').GetField('EncodingsOverride', $NP).SetValue($null, $encoding.List)
    $t = Open-Desktop (Profile) 'VncTest1'
    $ok = Wait { $t.Desktop.IsConnected } 20
    Pump 3
    $c = Field $t.Desktop 'client'
    $set = New-Object 'System.Collections.Generic.HashSet[int]'
    if ($c) { for ($i = 0; $i -lt $c.Frame.Pixels.Length; $i += 997) { [void]$set.Add($c.Frame.Pixels[$i]) } }
    Check "$($encoding.Name): picture decoded ($($set.Count) colours), still connected" ($ok -and $t.Desktop.IsConnected -and $set.Count -gt 5)
    if ($c) { $c.Frame.Bitmap.Save((Join-Path $Shots ("vnc-" + $encoding.Name.ToLower() + ".png"))) }
    $t.Form.Dispose()
}
(T 'VncClient').GetField('EncodingsOverride', $NP).SetValue($null, $null)

# --- 2. server restart: overlay, then «Переподключить»
wsl.exe -e sh -c "docker restart sshm-vnc >/dev/null" | Out-Null
Check "server restart ends the session" (Wait { -not $s.Desktop.IsOpen } 20)
Check "status after the drop: $($s.Desktop.Status)" ($s.Desktop.Status -like 'отключён*')
Pump 4
$s.Desktop.Reconnect()
Check "«Переподключить» connects again" (Wait { $s.Desktop.IsConnected } 25)
$s.Form.Dispose()

# --- 3. wrong password asks again; cancelling ends with an error
$asked = @{ count = 0; wrong = $false }
$s = Open-Desktop (Profile) 'nope1234' { param($p, $wrong) $asked.count++; $asked.wrong = $wrong; $null }
Check "wrong password asks the user" (Wait { $asked.count -gt 0 } 15)
Check "the prompt knows the password was wrong" $asked.wrong
Check "cancel leaves «отключён (ошибка)»" (Wait { $s.Desktop.Status -eq 'отключён (ошибка)' } 5)
$s.Form.Dispose()

# --- 4. no saved password: the server asks, the prompt answers, it connects
$asked = @{ count = 0 }
$s = Open-Desktop (Profile) $null { param($p, $wrong) $asked.count++; 'VncTest1' }
Check "connects after asking for the password" ((Wait { $s.Desktop.IsConnected } 25) -and $asked.count -eq 1)
$s.Form.Dispose()

# --- 5. through an SSH tunnel (key added to the bench for this run only)
$dir = Join-Path $env:TEMP ('sshm-vnc-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $dir | Out-Null
$key = Call (T 'SshKeys') 'Generate' @($dir, 'id_bench')
$public = (Get-Content "$key.pub" -Raw).Trim()
# Only the key type and body: the comment would need quoting inside sh -c.
$public = ($public -split ' ')[0..1] -join ' '
wsl.exe -e docker exec -u tester sshm-vnc sh -c "mkdir -p ~/.ssh && chmod 700 ~/.ssh && echo $public >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys" | Out-Null
$fwd = $dir.Replace('\', '/')
@"
Host vncbench
    HostName 127.0.0.1
    Port 2224
    User tester
    IdentityFile "$fwd/id_bench"
    IdentitiesOnly yes
    UserKnownHostsFile "$fwd/known_hosts"
    StrictHostKeyChecking accept-new
"@ | Set-Content -Encoding ASCII (Join-Path $dir 'config')
(T 'SshTunnel').GetField('ConfigOverride', $NP).SetValue($null, [string](Join-Path $dir 'config'))
$s = Open-Desktop (Profile 'vncbench') 'VncTest1'
Check "connects through the SSH tunnel" (Wait { $s.Desktop.IsConnected } 30)
$tunnel = Field $s.Desktop 'tunnel'
Check "tunnel listens on a local port ($($tunnel.LocalPort))" ($tunnel -ne $null -and $tunnel.LocalPort -gt 0)
$s.Desktop.Disconnect()
Check "«Отключить» ends with «Сеанс завершён»" (Wait { $s.Desktop.Status -eq 'отключён' } 10)
$s.Form.Dispose()
(T 'SshTunnel').GetField('ConfigOverride', $NP).SetValue($null, $null)
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
"failures: $failures"
exit $failures
