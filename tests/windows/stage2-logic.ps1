param([Parameter(Mandatory)] [string] $Exe)
$ErrorActionPreference = 'Stop'
$NP = [Reflection.BindingFlags]'Instance,Static,NonPublic,Public'
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
function T($name) { $asm.GetType('SshManager.' + $name, $true) }
function Unwrap([object[]] $a) { $list = New-Object 'System.Collections.Generic.List[object]'; foreach ($x in $a) { if ($x -is [psobject]) { $list.Add($x.psobject.BaseObject) } else { $list.Add($x) } }; ,$list.ToArray() }
function New($name, [object[]] $a) { [Activator]::CreateInstance((T $name), $NP, $null, (Unwrap $a), $null) }
function Call($o, $name, [object[]] $a = @()) { $type = if ($o -is [Type]) { $o } else { $o.GetType() }; $target = if ($o -is [Type]) { $null } else { $o }; $type.GetMethod($name, $NP).Invoke($target, (Unwrap $a)) }
function Field($o, $name) { $o.GetType().GetField($name, $NP).GetValue($o) }
$failures = 0
function Check([string] $what, $condition) { if ($condition) { "ok    $what" } else { "FAIL  $what"; $script:failures++ } }
function Throws([string] $what, [scriptblock] $block) { try { & $block; "FAIL  $what (no error)"; $script:failures++ } catch { $inner = $_.Exception; while ($inner.InnerException) { $inner = $inner.InnerException }; "ok    $what -> $($inner.Message)" } }
$dir = Join-Path $env:TEMP ('sshm-s2-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $dir | Out-Null

# --- tabs.xml written by the macOS app, then rewritten by Windows
$tabs = Join-Path $dir 'tabs.xml'
@'
<?xml version="1.0" encoding="UTF-8"?>
<tabs sshSort="name" terminalFont="Menlo" terminalSize="13" scrollback="5000" defaultUser="deploy" defaultPort="2222" autoReconnect="1" closeToTray="0" checkUpdates="0" futureOption="x">
  <favorite key="ssh:web"/><favorite key="rdp:11111111111111111111111111111111"/><favorite key="bogus"/>
  <recent key="ssh:web" at="1764000000"/><recent key="ssh:db" at="1763000000"/>
  <snippet name="Диск" command="df -h&#10;uptime"/><snippet name="" command="x"/>
  <unknownElement/>
  <tab id="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" name="Прод"><host alias="web"/></tab>
  <rdp id="11111111111111111111111111111111" name="PC" host="10.0.0.1" port="3389" user="u" domain="" drives="/Users/me/Shared;C:\Data;D:\" printers="1" multimon="1" gateway="gw.example.com:443" gatewayUser="CORP\gw"/>
</tabs>
'@ | Set-Content -Encoding UTF8 $tabs
$file = New 'GroupFile' @($tabs)
$g = $file.Load()
$p = $g.Preferences
Check 'preferences parsed' ($p.TerminalFont -eq 'Menlo' -and $p.TerminalSize -eq 13 -and $p.Scrollback -eq 5000 -and $p.DefaultUser -eq 'deploy' -and $p.DefaultPort -eq '2222' -and $p.AutoReconnect -and -not $p.CloseToTray -and -not $p.CheckUpdates)
Check 'favorites skip bad keys' (($g.Favorites -join ',') -eq 'ssh:web,rdp:11111111111111111111111111111111')
Check 'recent parsed' ($g.Recent.Count -eq 2 -and $g.Recent[0].At -eq 1764000000)
Check 'snippet with newline kept, empty skipped' ($g.Snippets.Count -eq 1 -and $g.Snippets[0].Command -eq "df -h`nuptime")
$d = $g.Desktops[0]
Check 'rdp options: mac folder skipped, windows kept' ((($d.Options.Drives) -join ';') -eq 'C:\Data;D:\' -and $d.Options.Printers -and $d.Options.Multimon -and $d.Options.Gateway -eq 'gw.example.com:443' -and $d.Options.GatewayUser -eq 'CORP\gw')
Check 'drive letters' ((($d.Options.DriveLetters) -join '') -eq 'CD')
$update = [Action[object]] { param($x) Call $x 'ToggleFavorite' @('SSH:Web') | Out-Null; Call $x 'RecordRecent' @('ssh:db') | Out-Null }
$typed = [scriptblock]{ param($x) Call $x 'ToggleFavorite' @('SSH:Web') | Out-Null; Call $x 'RecordRecent' @('ssh:db') | Out-Null } -as [Action`1].MakeGenericType((T 'Groups')); $g2 = Call $file 'Update' @($typed)
$xml = [xml](Get-Content $tabs -Raw)
Check 'favorite toggled off case-insensitively' (@($xml.tabs.favorite).Count -eq 1 -and $xml.tabs.favorite.key -eq 'rdp:11111111111111111111111111111111')
Check 'recent moved to top' ($xml.tabs.recent[0].key -eq 'ssh:db')
Check 'preferences written' ($xml.tabs.terminalFont -eq 'Menlo' -and $xml.tabs.autoReconnect -eq '1' -and $xml.tabs.closeToTray -eq '0' -and $xml.tabs.defaultPort -eq '2222')
Check 'snippet newline survives write' (((Call (T 'GroupFile') 'Parse' @([IO.MemoryStream]::new([IO.File]::ReadAllBytes($tabs)))).Snippets[0].Command) -eq "df -h`nuptime")
Check 'rdp options written' ($xml.tabs.rdp.drives -eq 'C:\Data;D:\' -and $xml.tabs.rdp.printers -eq '1' -and $xml.tabs.rdp.gateway -eq 'gw.example.com:443')
Throws 'drive outside a local disk rejected' { New 'RdpOptions' @([string[]]@('\\server\share'), $false, $false, '', '') }
Throws 'bad gateway rejected' { New 'RdpOptions' @([string[]]@(), $false, $false, 'gw example', '') }

# --- VNC profiles in tabs.xml
$vncTabs = Join-Path $dir 'vnc.xml'
@'
<tabs vncSort="name">
  <tab id="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" name="Цех" vncSort="addressDescending"/>
  <vnc id="66666666666666666666666666666666" name="Камера" host="10.0.0.7" port="5901" user="" group="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" scale="native" viewOnly="1" clipboard="0" via="bastion"/>
  <vnc id="77777777777777777777777777777777" name="Сборка" host="build.local" via="bad alias"/>
</tabs>
'@ | Set-Content -Encoding UTF8 $vncTabs
$vf = New 'GroupFile' @($vncTabs)
$vg = $vf.Load()
$v = $vg.Vnc[0]
Check 'vnc parsed' ($vg.Vnc.Count -eq 2 -and $v.Port -eq 5901 -and $v.Scale -eq 'native' -and $v.ViewOnly -and -not $v.Clipboard -and $v.Via -eq 'bastion' -and $v.GroupId -eq 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb')
Check 'vnc defaults and a broken via dropped' ($vg.Vnc[1].Port -eq 5900 -and $vg.Vnc[1].Clipboard -and $vg.Vnc[1].Via -eq '' -and $vg.Vnc[1].Scale -eq 'fit')
Check 'vnc sort per tab' ((Call $vg 'Sort' @('bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', [Enum]::Parse((T 'CatalogKind'), 'Vnc'))).ToString() -eq 'AddressDescending')
$removeTab = [scriptblock]{ param($x) Call $x 'RemoveTab' @('bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb') | Out-Null } -as [Action`1].MakeGenericType((T 'Groups'))
Call $vf 'Update' @($removeTab) | Out-Null
$vxml = [xml](Get-Content $vncTabs -Raw)
Check 'removing the tab keeps the VNC profile ungrouped' (@($vxml.tabs.vnc).Count -eq 2 -and -not $vxml.tabs.vnc[0].group -and $vxml.tabs.vncSort -eq 'Name')
Throws 'vnc profile rejects a bad via' { New 'VncProfile' @('88888888888888888888888888888888', 'x', 'h', 5900, '', $null, 'fit', $false, $true, $false, 'a b') }
$qv = New 'QuickTarget' @('vnc://pi@10.1.1.5')
Check 'quick vnc parse' ($qv.Vnc -and -not $qv.Rdp -and $qv.Port -eq 5900 -and $qv.User -eq 'pi')

# --- .rdp export and import keep the options
$rdpFile = Join-Path $dir 'pc.rdp'
$d.Export($rdpFile)
$text = Get-Content $rdpFile -Raw -Encoding Unicode
Check '.rdp has drives/printers/multimon/gateway' ($text -match 'drivestoredirect:s:C:\\;D:\\' -and $text -match 'redirectprinters:i:1' -and $text -match 'use multimon:i:1' -and $text -match 'gatewayhostname:s:gw.example.com:443' -and $text -match 'gatewayusername:s:CORP\\gw')
$back = Call (T 'RdpProfile') 'Import' @($rdpFile, $null)
Check '.rdp import round trip' ((($back.Options.Drives) -join ';') -eq 'C:\;D:\' -and $back.Options.Printers -and $back.Options.Multimon -and $back.Options.Gateway -eq 'gw.example.com:443')

# --- ssh config: ProxyJump and forwards
$config = @"
# keep me
Host web
    HostName 10.0.0.5   # comment
    User deploy
    LocalForward 8080 localhost:80
    DynamicForward 1080

Host db
    HostName db.local
"@ -replace "`r?`n", "`r`n"
$doc = New 'ConfigDocument' @($config)
$web = @($doc.Connections())[0]
Check 'forwards read' ((($web.Forwards) -join '|') -eq 'LocalForward 8080 localhost:80|DynamicForward 1080')
function Values($alias, $hostName, $user, $port, $jump, [string[]] $forwards) {
    $v = New 'ConnectionValues' @()
    $type = $v.GetType()
    $type.GetField('Alias').SetValue($v, $alias); $type.GetField('HostName').SetValue($v, $hostName); $type.GetField('User').SetValue($v, $user)
    $type.GetField('Port').SetValue($v, $port); $type.GetField('ProxyJump').SetValue($v, $jump)
    $keys = [Activator]::CreateInstance([Collections.Generic.List`1].MakeGenericType((T 'Identity')))
    $type.GetField('Keys').SetValue($v, $keys)
    $list = New-Object 'Collections.Generic.List[string]'; foreach ($f in $forwards) { $list.Add($f) }
    $type.GetField('Forwards').SetValue($v, $list)
    $v
}
$same = New 'ConfigDocument' @($config)
Call $same 'Update' @(@($same.Connections())[0], (Values 'web' '10.0.0.5' 'deploy' '' '' @('LocalForward 8080 localhost:80', 'DynamicForward 1080'))) | Out-Null
Check 'unchanged edit keeps the file byte-identical' ($same.ToString() -eq $config)
$changed = New 'ConfigDocument' @($config)
Call $changed 'Update' @(@($changed.Connections())[0], (Values 'web' '10.0.0.5' 'deploy' '' 'bastion,admin@jump:2200' @('RemoteForward 9000 localhost:9000'))) | Out-Null
$out = $changed.ToString()
Check 'ProxyJump and new forward written, old ones removed' ($out -match 'ProxyJump bastion,admin@jump:2200' -and $out -match 'RemoteForward 9000 localhost:9000' -and $out -notmatch 'LocalForward' -and $out -notmatch 'DynamicForward' -and $out -match '# keep me' -and $out -match 'Host db')
$web2 = @((New 'ConfigDocument' @($out)).Connections())[0]
Check 'written block parses back' ($web2.Get('proxyjump') -eq 'bastion,admin@jump:2200' -and (($web2.Forwards) -join '|') -eq 'RemoteForward 9000 localhost:9000')
$fresh = New 'ConfigDocument' @('')
Call $fresh 'Update' @($null, (Values 'new' 'h.example' 'u' '2200' 'bastion' @('LocalForward [::1]:5432 db:5432'))) | Out-Null
Check 'new host with jump and IPv6 forward' ($fresh.ToString() -match 'Host new' -and $fresh.ToString() -match 'ProxyJump bastion' -and $fresh.ToString() -match 'LocalForward \[::1\]:5432 db:5432')
foreach ($bad in 'LocalForward 8080', 'DynamicForward 1080 x:1', 'LocalForward 99999 h:80', 'LocalForward 80 h:80; rm') {
    Check "invalid forward '$bad' rejected" (-not (Call (T 'ConfigDocument') 'IsForward' @($bad)))
}
Throws 'bad ProxyJump rejected' { $x = New 'ConfigDocument' @(''); Call $x 'Update' @($null, (Values 'z' 'h' '' '' 'a b' @())) }

# --- quick connect
$q = New 'QuickTarget' @('admin@10.1.1.1:2222')
Check 'quick ssh parse' (-not $q.Rdp -and $q.User -eq 'admin' -and $q.Host -eq '10.1.1.1' -and $q.Port -eq 2222 -and $q.SshArguments -eq '-p 2222 "admin@10.1.1.1"')
$r = New 'QuickTarget' @('rdp://CORP@[fe80::1]:3390/')
Check 'quick rdp IPv6' ($r.Rdp -and $r.Host -eq 'fe80::1' -and $r.Port -eq 3390)
Check 'quick default port' ((New 'QuickTarget' @('host.example')).Port -eq 22 -and (New 'QuickTarget' @('rdp://pc')).Port -eq 3389)
Throws 'quick rejects junk' { New 'QuickTarget' @('a b c') }
Throws 'quick rejects port 70000' { New 'QuickTarget' @('h:70000') }
Check 'free alias' ((Call (T 'QuickTarget') 'Alias' @('db.example.com', [string[]]@('db.example.com', 'DB.example.com-2'))) -eq 'db.example.com-3')

# --- importers
$mr = Join-Path $dir 'confCons.xml'
@'
<?xml version="1.0" encoding="utf-8"?>
<mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.6">
  <Node Name="Folder" Type="Container"><Node Name="web" Type="Connection" Protocol="SSH2" Hostname="10.0.0.5" Port="2222" Username="root" Password="ENC"/></Node>
  <Node Name="pc" Type="Connection" Protocol="RDP" Hostname="pc.local" Port="3389" Username="ivan" Domain="CORP"/>
  <Node Name="vnc" Type="Connection" Protocol="VNC" Hostname="v" Port="5901"/>
  <Node Name="tel" Type="Connection" Protocol="Telnet" Hostname="t"/>
</mrng:Connections>
'@ | Set-Content -Encoding UTF8 $mr
$found = Call (T 'Importers') 'Read' @($mr)
Check 'mRemoteNG: SSH, RDP and VNC, Telnet skipped' ($found.Count -eq 3 -and $found[0].Name -eq 'web' -and $found[0].Port -eq 2222 -and $found[1].Rdp -and $found[1].Domain -eq 'CORP' -and $found[2].Vnc -and $found[2].Port -eq 5901)
$moba = Join-Path $dir 'list.mxtsessions'
"[Bookmarks]`r`nSubRep=`r`nImgNum=42`r`nsrv=#109#0%192.168.1.10%22%admin%%-1%-1%`r`nwin=#91#4%10.0.0.9%3389%user%-1%`r`nscreen=#128#5%10.0.0.10%5900%%-1%`r`ntelnet=#98#1%h%23%%" | Set-Content -Encoding UTF8 $moba
$found = Call (T 'Importers') 'Read' @($moba)
Check 'MobaXterm: SSH, RDP and VNC, telnet skipped' ($found.Count -eq 3 -and $found[0].Host -eq '192.168.1.10' -and $found[0].User -eq 'admin' -and $found[1].Rdp -and $found[2].Vnc -and $found[2].Protocol -eq 'VNC')
$putty = Call (T 'Importers') 'PuTTY' @()
"info  PuTTY sessions on this PC: $($putty.Count)"

# --- keys
$keyDir = Join-Path $dir 'ssh'
$private = Call (T 'SshKeys') 'Generate' @($keyDir, 'id_test')
Check 'ssh-keygen created the pair' ((Test-Path $private) -and (Test-Path "$private.pub"))
$cmd = Call (T 'SshKeys') 'CopyCommand' @("$private.pub")
Check 'copy command quotes the key once' ($cmd -match "grep -qxF 'ssh-ed25519 " -and $cmd -match 'authorized_keys')
Throws 'existing key not overwritten' { Call (T 'SshKeys') 'Generate' @($keyDir, 'id_test') }
Throws 'name with folder rejected' { Call (T 'SshKeys') 'Generate' @($keyDir, '..\x') }
Set-Content (Join-Path $dir 'bad.pub') "ssh-ed25519 AAAA'; rm -rf ~ '"
Throws 'quote in key rejected' { Call (T 'SshKeys') 'CopyCommand' @((Join-Path $dir 'bad.pub')) }

Remove-Item -Recurse -Force $dir
"failures: $failures"
