# Renders SSH Manager windows to PNG. Emulates a higher system DPI by faking WinForms' DpiHelper
# (DeviceDpi) and scaling the app's fonts, which is what a system-DPI-aware app sees at that scale.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Out,
    [double] $Scale = 1,
    [string[]] $Scenes = @('all'),
    [switch] $Dark
)
$ErrorActionPreference = 'Stop'
$Scenes = @($Scenes | ForEach-Object { $_ -split ',' })
Add-Type -Namespace Snap -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$NP = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$SNP = [Reflection.BindingFlags]'Static,NonPublic,Public'
if ($Scale -ne 1) {
    $dh = [Windows.Forms.Form].Assembly.GetType('System.Windows.Forms.DpiHelper')
    $dh.GetField('deviceDpi', $SNP).SetValue($null, [double](96 * $Scale))
    $dh.GetField('isInitialized', $SNP).SetValue($null, $true)
}
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
function T($name) { $asm.GetType('SshManager.' + $name, $true) }
function Unwrap([object[]] $a) { $list = New-Object 'System.Collections.Generic.List[object]'; foreach ($x in $a) { if ($x -is [psobject]) { $list.Add($x.psobject.BaseObject) } else { $list.Add($x) } }; ,$list.ToArray() }
function New($name, [object[]] $a) { [Activator]::CreateInstance((T $name), $NP, $null, (Unwrap $a), $null) }
function Call($o, $name, [object[]] $a = @()) { if ($o -is [Type]) { $o.GetMethod($name, $SNP).Invoke($null, (Unwrap $a)) } else { $o.GetType().GetMethod($name, $NP).Invoke($o, (Unwrap $a)) } }
function Field($o, $name) { $o.GetType().GetField($name, $NP).GetValue($o) }
$theme = T 'Theme'
if ($Scale -ne 1) {
    foreach ($f in 'Body', 'Title', 'Emphasis') {
        $field = $theme.GetField($f, $SNP); $font = $field.GetValue($null)
        $field.SetValue($null, (New-Object Drawing.Font($font.FontFamily, [single]($font.SizeInPoints * $Scale), $font.Style)))
    }
}
if ($Dark) { $theme.GetProperty('IsDark', $SNP).SetValue($null, $true) }

New-Item -ItemType Directory -Force $Out | Out-Null
$data = Join-Path $env:TEMP ('sshm-harness-' + [Guid]::NewGuid().ToString('N'))
$ssh = New-Item -ItemType Directory -Force (Join-Path $data '.ssh')
@'
Host sshm-web-prod
  HostName 192.168.10.21
  User deploy
  Port 2222
  ProxyJump bastion
  LocalForward 8080 localhost:80
  DynamicForward 1080
Host sshm-db
  HostName db.internal.example.com
  User postgres
Host sshm-very-long-alias-for-checking-the-ellipsis-in-the-catalog
  HostName 2001:db8::1
  User administrator
Host sshm-bastion
  HostName 10.0.0.5
'@ | Set-Content -Encoding UTF8 (Join-Path $ssh 'config')
$tabA = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; $tabB = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; $tabC = 'cccccccccccccccccccccccccccccccc'
$tabsPath = Join-Path $data 'tabs.xml'
@"
<tabs$(if ($Dark) { ' theme="dark"' }) autoReconnect="1">
  <favorite key="ssh:sshm-db"/><favorite key="rdp:11111111111111111111111111111111"/>
  <recent key="ssh:sshm-bastion" at="1764000000"/><recent key="ssh:sshm-db" at="1763000000"/>
  <snippet name="Свободное место" command="df -h"/><snippet name="Журнал nginx" command="sudo tail -n 50 /var/log/nginx/error.log"/>
  <tab id="$tabA" name="Продакшн" color="blue"><host alias="sshm-web-prod"/><host alias="sshm-db"/></tab>
  <tab id="$tabB" name="Тестовые стенды и разработка" color="amber"/>
  <tab id="$tabC" name="Пустая"/>
  <rdp id="11111111111111111111111111111111" name="Офисный ПК" host="10.1.2.3" port="3389" user="ivan" domain="CORP" group="$tabA" drives="C:\Data;D:\" printers="1" gateway="gw.example.com"/>
  <vnc id="44444444444444444444444444444444" name="Сервер сборки" host="build.example.com" port="5901" user="" group="$tabA" scale="fit" clipboard="1" via="sshm-bastion"/>
  <vnc id="55555555555555555555555555555555" name="Камера в цеху" host="10.20.0.7" port="5900" user="" scale="native" viewOnly="1" clipboard="0"/>
  <rdp id="22222222222222222222222222222222" name="Сервер терминалов с длинным названием" host="ts.example.com" port="3390" user="admin" domain=""/>
</tabs>
"@ | Set-Content -Encoding UTF8 $tabsPath

function Pump([double] $seconds = 0.3) {
    $end = [DateTime]::Now.AddSeconds($seconds)
    do { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 } while ([DateTime]::Now -lt $end)
}
function Remove-Shown($form) {
    $events = [ComponentModel.Component].GetProperty('Events', $NP).GetValue($form)
    $key = [Windows.Forms.Form].GetField('EVENT_SHOWN', $SNP).GetValue($null)
    $handler = $events[$key]; if ($handler) { $events.RemoveHandler($key, $handler) }
}
function Snap($control, $name) {
    $bitmap = New-Object Drawing.Bitmap($control.Width, $control.Height)
    $g = [Drawing.Graphics]::FromImage($bitmap); $hdc = $g.GetHdc()
    [void][Snap.Native]::PrintWindow($control.Handle, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
    $suffix = if ($Scale -ne 1) { '@' + [int]($Scale * 100) } else { '' }
    $file = Join-Path $Out ($name + $suffix + $(if ($Dark) { '-dark' }) + '.png')
    $bitmap.Save($file); $bitmap.Dispose()
    "$name  window=$($control.Size) client=$($control.ClientSize) -> $file"
}
function Show-Form($form, [switch] $KeepShown) {
    $form.StartPosition = 'Manual'; $form.Location = New-Object Drawing.Point(-30000, -30000); $form.ShowInTaskbar = $false
    if (-not $KeepShown) { Remove-Shown $form }
    $form.Show(); Pump 0.4; $form.Refresh(); Pump 0.6
}
function Set-Minimum($form) {
    $form.ClientSize = New-Object Drawing.Size(($form.MinimumSize.Width - ($form.Width - $form.ClientSize.Width)), ($form.MinimumSize.Height - ($form.Height - $form.ClientSize.Height))); Pump
}
function Want($scene) { $Scenes -contains 'all' -or $Scenes -contains $scene }
function Kind($name) { [Enum]::Parse((T 'CatalogKind'), $name) }

$configFile = New 'ConfigFile' @((Join-Path $ssh 'config'))
$connections = @($configFile.Load().Connections())
$groupFile = New 'GroupFile' @($tabsPath)
try { $groups = $groupFile.Load() } catch { $_.Exception.InnerException.InnerException | Format-List * -Force | Out-String | Write-Host; Get-Content $tabsPath | Write-Host; throw }
$profiles = @($groups.Desktops)

if ((Want 'main') -or (Want 'sessions')) {
    $main = New 'ConnectionsWindow' @($ssh.FullName, $tabsPath)
    Show-Form $main
    Call $main 'Reload' @($true) | Out-Null; Pump
    if (Want 'main') {
        Snap $main 'main-ssh'
                Set-Minimum $main; "DIAG form client=$($main.ClientSize) display=$($main.DisplayRectangle) frame=$($main.Controls[0].Bounds) layoutSuspended=$($main.GetType().GetProperty('IsLayoutSuspended', $NP).GetValue($main))"
        $pages = Field $main 'pages'; $cat = Field $main 'catalog'; $add = Field $main 'add'; $grid = Field $main 'grid'
        "DIAG pages=$($pages.Bounds) catalog=$($cat.Bounds) add=$($add.Bounds) addParent=$($add.Parent.Bounds) grid=$($grid.Bounds) gridParent=$($grid.Parent.Bounds)"
        "DIAG rowstyles=$(($cat.RowStyles | % { $_.SizeType.ToString()[0] + [string]$_.Height }) -join ',') padding=$($cat.Padding)"
        Snap $main 'main-minimum-size'
        $main.ClientSize = New-Object Drawing.Size([int](980 * $Scale), [int](570 * $Scale)); Pump
        Call $main 'SelectProtocol' @((Kind 'Rdp')) | Out-Null; Pump; Snap $main 'main-rdp'
        Call $main 'SelectProtocol' @((Kind 'Vnc')) | Out-Null; Pump; Snap $main 'main-vnc'
        Call $main 'SelectProtocol' @((Kind 'Ssh')) | Out-Null
        Call $main 'SelectGroup' @($tabC) | Out-Null; Call $main 'RefreshList' @($null) | Out-Null; Pump; Snap $main 'main-empty-tab'
        Call $main 'SelectGroup' @($null) | Out-Null; Call $main 'RefreshList' @($null) | Out-Null; Pump
        if (Want 'stage2') {
            Call $main 'SelectGroup' @('favorites') | Out-Null; Call $main 'RefreshList' @($null) | Out-Null; Pump; Snap $main 'main-favorites'
            Call $main 'SelectProtocol' @((Kind 'Rdp')) | Out-Null; Pump; Snap $main 'main-favorites-rdp'
            Call $main 'SelectProtocol' @((Kind 'Ssh')) | Out-Null
            Call $main 'SelectGroup' @($null) | Out-Null; Call $main 'RefreshList' @($null) | Out-Null; Pump
        }
    }
    if (Want 'sessions') {
        $page = Call $main 'SessionWorkspace'
        # Run a harmless local console instead of ssh.
        $ptType = T 'PseudoTerminal'
        $funcType = $page.GetType().GetField('startProcess', $NP).FieldType
        $start = [scriptblock]{ param($target, $size)
            if ($target.Alias -eq 'sshm-db') { $ptType::Start("$env:WINDIR\System32\cmd.exe", '/q /c echo Connection to db closed. & exit 255', $size.Width, $size.Height, $null) }
            else { $ptType::Start("$env:WINDIR\System32\cmd.exe", '/q /k prompt $G & echo Connected to ' + $target.Alias, $size.Width, $size.Height, $null) } }
        $page.GetType().GetField('startProcess', $NP).SetValue($page, ($start -as $funcType))
        Call $main 'Connect' @($connections[0], $false) | Out-Null; Pump 1
        Call $main 'Connect' @($connections[0], $true) | Out-Null; Pump 1
        Call $main 'Connect' @($connections[2], $false) | Out-Null; Pump 1.5
        Snap $main 'sessions-ssh'
        # «Серверы» column: groups by catalog tab, a folded tab is saved in tabs.xml, right click opens the catalog menu.
        $picker = Field $page 'serverPicker'
        Call $picker 'SelectProtocol' @((Kind 'Ssh')); Pump
        Snap $main 'sessions-servers-grouped'
        (Field $page 'ServerSectionToggled').Invoke($tabA); Pump
        Snap $main 'sessions-servers-folded'
        "COLLAPSED in tabs.xml: " + ([xml](Get-Content $tabsPath -Raw)).tabs.collapsed
        (Field $page 'ServerSectionToggled').Invoke($tabA); Pump
        $pickerList = Field $picker 'list'
        (Field $page 'ServerMenuRequested').Invoke($connections[1], $pickerList, (New-Object Drawing.Point(20, 60))); Pump
        "SERVER MENU visible: " + (Field $main 'serverMenu').Visible + " for " + (Field $main 'menuConnection').Alias
        (Field $main 'serverMenu').Close()
        Call $page 'ShowServerList' @($true); Pump
        Call (Field $page 'serverPicker') 'SelectProtocol' @((Kind 'Vnc')); Pump
        Snap $main 'sessions-server-list'
        # Back to the default: the server column folds on its own when the window is narrow.
        $page.GetType().GetField('serversForced', $NP).SetValue($page, $false); Call $page 'FitColumns'; Pump
        if (Want 'stage2') {
            Call $main 'Connect' @($connections[1], $false) | Out-Null; Pump 1.5
            Snap $main 'sessions-reconnecting'
            Pump 20
            Snap $main 'sessions-ssh-ended'
            Call $main 'Connect' @($connections[0], $false) | Out-Null; Pump 1
            $menu = Field $page 'snippetMenu'; Call $page 'ShowSnippets' | Out-Null; Pump 0.5
            "SNIPPETS " + (($menu.Items | ForEach-Object { $_.Text }) -join ' | ')
            $menu.Close()
        }
        $credential = New 'RdpCredential' @('user', '', 'x')
        # An unreachable port fails fast and shows the reconnect overlay.
        $closed = New 'RdpProfile' @('33333333333333333333333333333333', 'Недоступный ПК', '127.0.0.1', 1, 'user', '', $null, 0, 0, $false, $false, $null)
        $page.Open((New 'SessionTarget' @($closed, $null)), $true, $credential) | Out-Null
        $end = [DateTime]::Now.AddSeconds(25)
        while ((Field $page 'active').Desktop.IsOpen -and [DateTime]::Now -lt $end) { Pump 0.3 }
        Pump 0.5
        Snap $main 'sessions-rdp-ended'
        Set-Minimum $main; Snap $main 'sessions-min'
        Call $main 'ShowServers' | Out-Null; Pump; Snap $main 'main-with-sessions'
        # «Файлы» open in the workspace and appear under «Открытые файлы». Last, because the connection fails
        # with a message box once ssh gives up; the window is disposed before that.
        $main.ClientSize = New-Object Drawing.Size([int](1280 * $Scale), [int](720 * $Scale)); Pump
        $main.GetType().GetMethod('OpenFiles', $NP, $null, [Type[]]@((T 'Connection')), $null).Invoke($main, @($connections[3])) | Out-Null; Pump 0.6
        Snap $main 'sessions-files'
    }
    $main.Dispose()
}

if (Want 'dialogs') {
    $save3 = [Action[object, string, bool]] { }
    $d = New 'ConnectionDialog' @($ssh.FullName, $null, $false, $null, $groups.Preferences); Show-Form $d; Snap $d 'dlg-connection-new'; $d.Dispose()
    $d = New 'ConnectionDialog' @($ssh.FullName, $connections[0], $true, $null, $null); Show-Form $d; Snap $d 'dlg-connection-edit'; $d.Dispose()
    $d = New 'RdpDialog' @($null, $null, $false, $null); Show-Form $d; Snap $d 'dlg-rdp-new'; $d.Dispose()
    $d = New 'RdpDialog' @($profiles[0], $null, $true, $null); Show-Form $d; Snap $d 'dlg-rdp-edit'; $d.Dispose()
    $d = New 'RdpLogonDialog' @($profiles[0]); Show-Form $d; Snap $d 'dlg-rdp-logon'; $d.Dispose()
    $d = New 'GroupDialog' @($null, $null); Show-Form $d; Snap $d 'dlg-tab'; $d.Dispose()
    $typed = [Array]::CreateInstance((T 'Connection'), $connections.Count); for ($i = 0; $i -lt $connections.Count; $i++) { $typed[$i] = $connections[$i] }
    $d = New 'ServerPickerDialog' @($typed, $groups, (Kind 'Ssh')); Show-Form $d; Snap $d 'dlg-picker'; $d.Dispose()
    $d = New 'ServerPickerDialog' @($typed, $groups, (Kind 'Vnc')); Show-Form $d; Snap $d 'dlg-picker-vnc'; $d.Dispose()
    if (Want 'stage2') {
        $d = New 'SettingsDialog' @($groups.Preferences, [Action]{ }); Show-Form $d; Snap $d 'dlg-settings'; $d.Dispose()
        # «Известные хосты» on a test file (example keys, never the real ~/.ssh/known_hosts)
        $kh = Join-Path $data 'known_hosts_demo'
        @(
            '# demo',
            'web.example.com,192.168.10.21 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl',
            '[db.internal.example.com]:2222 ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBEmKSENjQEezOmxkZMy7opKgwFB9nkt5YRrYMjNuG5N87uRgg6CLrbo5wAdT/y6v0mKV0U2w0WZ2YB/++Tpockg=',
            '|1|JfKTdBh7rNbXkVAQCRp4OQoPfmI=|USECr3SWf1JUPsms5AqfD5QfxkM= ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC8',
            '@cert-authority *.corp.example ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl'
        ) | Set-Content -Encoding ASCII $kh
        $pairType = [Collections.Generic.KeyValuePair``2].MakeGenericType([string], [string[]])
        $files = [Array]::CreateInstance($pairType, 1)
        $files[0] = [Activator]::CreateInstance($pairType, @('~/.ssh/known_hosts_work', [string[]]@('sshm-db')))
        $d = New 'KnownHostsDialog' @($files, $kh, ''); Show-Form $d -KeepShown; Snap $d 'dlg-known-hosts'; $d.Dispose()
        $d = New 'HostKeyDialog' @('[db.internal.example.com]:2222'); Show-Form $d; Snap $d 'dlg-host-key'; $d.Dispose()
        $aliases = [string[]]@($connections | ForEach-Object { $_.Alias })
        $save = [Action`3].MakeGenericType((T 'VncProfile'), [string], [bool])
        $d = New 'VncDialog' @($null, $null, $false, $aliases, ([scriptblock]{ } -as $save)); Show-Form $d; Snap $d 'dlg-vnc-new'; $d.Dispose()
        $d = New 'VncDialog' @($groups.Vnc[0], $null, $true, $aliases, ([scriptblock]{ } -as $save)); Show-Form $d; Snap $d 'dlg-vnc-edit'; $d.Dispose()
        $d = New 'VncLogonDialog' @($groups.Vnc[0], $true); Show-Form $d; Snap $d 'dlg-vnc-logon'; $d.Dispose()
        $d = New 'SnippetDialog' @(); Show-Form $d; Snap $d 'dlg-snippet'; $d.Dispose()
        $d = New 'ImportDialog' @(); Show-Form $d
        $mx = Join-Path $data 'list.mxtsessions'
        "[Bookmarks]`r`nsrv=#109#0%192.168.1.10%22%admin%%-1%`r`nwin=#91#4%10.0.0.9%3389%user%-1%`r`nlong=#109#0%very-long-host-name.internal.example.com%2200%deployer%%" | Set-Content $mx
        $foundList = $asm.GetType('SshManager.Importers').GetMethod('Read', $SNP).Invoke($null, @([string]$mx)); $d.GetType().GetMethod('ShowFound', $NP).Invoke($d, @(,$foundList)) | Out-Null; Pump
        Snap $d 'dlg-import'; $d.Dispose()
    }
    $d = New 'BackupPasswordDialog' @($true); Show-Form $d; Snap $d 'dlg-backup-create'; $d.Dispose()
    $d = New 'BackupPasswordDialog' @($false); Show-Form $d; Snap $d 'dlg-backup-open'; $d.Dispose()
    $update = New 'AppUpdate' @([Version]'1.10.0', 'x', [long]1, $true, [long]0)
    $d = New 'UpdateDialog' @($update); Show-Form $d; Snap $d 'dlg-update'; $d.Dispose()
    $d = [Activator]::CreateInstance($asm.GetType('SshManager.SftpWindow+FileConflictDialog'), $NP, $null, @('/home/deploy/releases/app-2026-11-25.tar.gz', $true), $null)
    Show-Form $d; Snap $d 'dlg-conflict'; $d.Dispose()
    $d = [Activator]::CreateInstance($asm.GetType('SshManager.SshAskPass+AskPassDialog'), $NP, $null, @("deploy@192.168.10.21's password:"), $null)
    Show-Form $d; Snap $d 'dlg-askpass'; $d.Dispose()
}

if (Want 'sftp') {
    $target = New 'SessionTarget' @($connections[0], $null)
    $connect = $null
    $w = New 'SftpWindow' @($target, $connect, $null); Show-Form $w
    Call $w 'NavigateLocal' @($data) | Out-Null; Pump
    Snap $w 'sftp'
    Set-Minimum $w; Snap $w 'sftp-min'
    $w.Dispose()
}

if (Want 'rdpwindow') {
    $w = New 'RdpWindow' @($profiles[0], (New 'RdpCredential' @('u', '', 'x'))); Show-Form $w
    $desktop = Field $w 'desktop'
    $desktop.GetType().GetField('profile', $NP).SetValue($desktop, $profiles[0])
    Call $desktop 'ShowEnded' @('Подключение прервано', "Удалённый компьютер разорвал соединение.`nКод: 264") | Out-Null; Pump
    Snap $w 'rdp-window'
    $w.Dispose()
}
Remove-Item -Recurse -Force $data
