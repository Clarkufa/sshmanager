param([Parameter(Mandatory)] [string] $Exe)
# Live SSH checks against the tests/xrdp bench (127.0.0.1:2223, test account tester / Rdp-Test-2026).
# Uses a temporary ssh config and known_hosts so the user's own ~/.ssh is not touched.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
# Like the GUI app: no console of our own, so ssh draws into the pseudo console only.
Add-Type -Namespace Live -Name Native -MemberDefinition '[DllImport("kernel32.dll")] public static extern bool FreeConsole();'
[void][Live.Native]::FreeConsole()
$NP = [Reflection.BindingFlags]'Instance,Static,NonPublic,Public'
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
function T($name) { $asm.GetType('SshManager.' + $name, $true) }
function Unwrap([object[]] $a) { $list = New-Object 'System.Collections.Generic.List[object]'; foreach ($x in $a) { if ($x -is [psobject]) { $list.Add($x.psobject.BaseObject) } else { $list.Add($x) } }; ,$list.ToArray() }
function New($name, [object[]] $a) { [Activator]::CreateInstance((T $name), $NP, $null, (Unwrap $a), $null) }
function Call($o, $name, [object[]] $a = @()) { if ($o -is [Type]) { $o.GetMethod($name, $NP).Invoke($null, (Unwrap $a)) } else { $o.GetType().GetMethod($name, $NP).Invoke($o, (Unwrap $a)) } }
function Field($o, $name) { $o.GetType().GetField($name, $NP).GetValue($o) }
function SetField($o, $name, $value) { $o.GetType().GetField($name, $NP).SetValue($o, $value) }
$failures = 0
function Check([string] $what, $condition) { if ($condition) { "ok    $what" } else { "FAIL  $what"; $script:failures++ } }
function Pump([double] $seconds) { $end = [DateTime]::Now.AddSeconds($seconds); do { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 25 } while ([DateTime]::Now -lt $end) }
function Screen($session) {
    $e = $session.Screen.Engine; $text = New-Object Text.StringBuilder
    for ($r = [Math]::Max(0, $e.ViewPort.TopRow - 200); $r -lt $e.ViewPort.TopRow + $e.VisibleRows; $r++) {
        $line = $e.ViewPort.GetLine($r); if ($line) { [void]$text.AppendLine((-join ($line | ForEach-Object { $_.Char })).TrimEnd()) } }
    $text.ToString()
}
function WaitFor($session, [string] $pattern, [double] $seconds = 20) {
    $end = [DateTime]::Now.AddSeconds($seconds)
    while ([DateTime]::Now -lt $end) { Pump 0.2; if ((Screen $session) -match $pattern) { return $true } }
    return $false
}
function Active($page) { Field $page 'active' }

$dir = Join-Path $env:TEMP ('sshm-live-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $dir | Out-Null
$key = Call (T 'SshKeys') 'Generate' @($dir, 'id_bench')
$cfgPath = Join-Path $dir 'config'
$fwd = $dir.Replace('\', '/')
# The app's own config editor writes the jump host and the forward.
$doc = New 'ConfigDocument' @("Host *`r`n    UserKnownHostsFile `"$fwd/known_hosts`"`r`n    StrictHostKeyChecking accept-new`r`n`r`n")
function Values($alias, $hostName, $user, $port, $jump, [string[]] $forwards) {
    $v = New 'ConnectionValues' @(); $type = $v.GetType()
    foreach ($pair in @(@('Alias', $alias), @('HostName', $hostName), @('User', $user), @('Port', $port), @('ProxyJump', $jump))) { $type.GetField($pair[0]).SetValue($v, $pair[1]) }
    $keys = [Activator]::CreateInstance([Collections.Generic.List`1].MakeGenericType((T 'Identity')))
    $keys.Add((New 'Identity' @("$fwd/id_bench", -1)))
    $type.GetField('Keys').SetValue($v, $keys)
    $list = New-Object 'Collections.Generic.List[string]'; foreach ($f in $forwards) { $list.Add($f) }
    $type.GetField('Forwards').SetValue($v, $list); $v
}
Call $doc 'Update' @($null, (Values 'bench' '127.0.0.1' 'tester' '2223' '' @())) | Out-Null
Call $doc 'Update' @($null, (Values 'jumped' '127.0.0.1' 'tester' '22' 'bench' @('LocalForward 18089 127.0.0.1:22'))) | Out-Null
[IO.File]::WriteAllText($cfgPath, $doc.ToString())
"--- generated config`n" + $doc.ToString()

$form = New-Object Windows.Forms.Form; $form.ClientSize = New-Object Drawing.Size(1000, 640); $form.StartPosition = 'Manual'; $form.Location = New-Object Drawing.Point(-30000, -30000); $form.ShowInTaskbar = $false
$page = New 'SessionsPage' @($null)
$prefs = New 'Preferences' @(); $prefs.AutoReconnect = $true
$page.Preferences = $prefs
$form.Controls.Add($page); $form.Show(); Pump 0.5
function Target([string] $arguments) {
    $t = New 'SessionTarget' @((New 'QuickTarget' @('tester@127.0.0.1:2223')))
    SetField $t 'DirectArguments' ("-F " + (& { (T 'Terminal').GetMethod('Quote').Invoke($null, @([string]$cfgPath)) }) + " " + $arguments); $t
}

# 1. «Скопировать ключ на сервер»: the remote command, password typed in the terminal.
$command = Call (T 'SshKeys') 'CopyCommand' @("$key.pub")
$quote = (T 'Terminal').GetMethod('Quote')
$keyTarget = Target ("bench " + $quote.Invoke($null, @([string]$command))); SetField $keyTarget 'RemoteCommand' ([string]$command); [void]$page.Open($keyTarget, $true, $null)
$s = Active $page
if (WaitFor $s 'password:' 25) { $s.Process.Send([Text.Encoding]::UTF8.GetBytes("Rdp-Test-2026`r")) }
Check 'key copied over SSH' (WaitFor $s 'Ключ добавлен' 25)
Pump 2
Check 'key session ended with code 0' ($s.ExitCode -eq 0)

# 2. ProxyJump + LocalForward with the new key, no password.
[void]$page.Open((Target 'jumped'), $true, $null)
$s = Active $page
Check 'connected through ProxyJump with the key' (WaitFor $s 'tester@[0-9a-f]+:~\$' 25)
$tcp = New-Object Net.Sockets.TcpClient
try { $tcp.Connect('127.0.0.1', 18089); $stream = $tcp.GetStream(); $buffer = New-Object byte[] 64; $stream.ReadTimeout = 5000; $n = $stream.Read($buffer, 0, 64); $banner = [Text.Encoding]::ASCII.GetString($buffer, 0, $n) } catch { $banner = "error: $($_.Exception.Message)" } finally { $tcp.Close() }
Check "LocalForward 18089 reaches the remote sshd ($($banner.Trim()))" ($banner -like 'SSH-2.0-*')

# 3. Saved command.
$snippet = New 'Snippet' @('Проверка', "echo SNIPPET-`$((40+2))")
Call $page 'Send' @($snippet) | Out-Null
Check 'snippet sent and executed' (WaitFor $s 'SNIPPET-42' 10)

# 4. Broken connection: automatic reconnect after 5 s.
& wsl.exe -e sh -c "docker exec sshm-xrdp pkill -KILL -u tester -x sshd" | Out-Null
Pump 3
Check "broken session ended with 255 (got $($s.ExitCode))" ($s.ExitCode -eq 255)
Check 'reconnect scheduled' ($s.ReconnectAt -ne $null -and $s.Attempts -eq 1)
$text = (Field $page 'endedText').Text
Check "ended bar says it will retry: $text" ($text -like 'Соединение прервано*')
Pump 7
$fresh = Active $page
Check 'session replaced by a live one' ($fresh -ne $s -and $fresh.IsOpen -and $fresh.Number -eq $s.Number)
Check 'reconnected shell prompt' (WaitFor $fresh 'tester@[0-9a-f]+:~\$' 25)

# 5. Normal exit: no automatic reconnect, then «Переподключить».
$fresh.Process.Send([Text.Encoding]::UTF8.GetBytes("exit`r")); Pump 3
Check 'exit 0 ends without retry' ($fresh.ExitCode -eq 0 -and $fresh.ReconnectAt -eq $null)
Check "ended bar: $((Field $page 'endedText').Text)" ((Field $page 'endedText').Text -eq 'Сеанс SSH завершён (код 0)')
Call $page 'Reconnect' @($fresh, $false) | Out-Null
$again = Active $page
Check 'manual reconnect works' (WaitFor $again 'tester@[0-9a-f]+:~\$' 25)
$again.Process.Send([Text.Encoding]::UTF8.GetBytes("exit`r")); Pump 2

$form.Dispose()
Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
"failures: $failures"
