param(
    [Parameter(Mandatory)] [string] $Exe,
    [int] $Port = 5901,
    [string] $Container = "sshm-vnc"
)
# Tests the fixture's X11 clipboard without replacing the Windows desktop clipboard.
$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition @"
public sealed class VncClipboardProbe {
    public volatile string Text;
    public void Receive(string text) { Text = text; }
}
"@
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
$type = $asm.GetType("SshManager.VncClient", $true)
$client = [Activator]::CreateInstance($type)
$probe = New-Object VncClipboardProbe
$received = $type.GetEvent("ClipboardReceived")
$received.AddEventHandler($client, [Delegate]::CreateDelegate($received.EventHandlerType, $probe, "Receive"))
$private = [Reflection.BindingFlags]"Instance,NonPublic"
$clipboard = $type.GetField("clipboard", $private).GetValue($client)
$caps = $clipboard.GetType().GetField("peerCaps", $private)
$start = $type.GetMethod("Start")
$send = $type.GetMethod("SendClipboard")

function Wait-For([scriptblock] $condition, [double] $seconds) {
    $end = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $end)
    return $false
}

try {
    $start.Invoke($client, @("127.0.0.1", $Port, "VncTest1"))
    if (-not (Wait-For { $caps.GetValue($clipboard) -ne 0 } 20)) {
        throw "TigerVNC did not negotiate Extended Clipboard."
    }

    $outgoing = "Windows → сервер" + [char]10 + "Привет, мир 🌍"
    $send.Invoke($client, @($outgoing))
    $receivedOnServer = {
        $base64 = wsl.exe -e docker exec -u tester -e DISPLAY=:1 $Container sh -c "timeout 3 xclip -selection clipboard -out 2>/dev/null | base64 -w0"
        if (-not $base64) { return $false }
        return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($base64)) -eq $outgoing
    }
    if (-not (Wait-For $receivedOnServer 15)) { throw "The server did not receive the Windows UTF-8 clipboard." }
    "ok    Windows to TigerVNC: multiline UTF-8 clipboard"

    $incoming = "Сервер → Windows" + [char]10 + "Кириллица и emoji 🙂"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($incoming))
    wsl.exe -e docker exec -u tester -e DISPLAY=:1 $Container sh -c "printf %s $encoded | base64 -d | xclip -selection clipboard -in >/dev/null 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "Could not set the fixture's clipboard." }
    if (-not (Wait-For { ([string]$probe.Text -replace "\r\n", [char]10) -eq $incoming } 15)) {
        throw "Windows did not receive the server UTF-8 clipboard."
    }
    "ok    TigerVNC to Windows: multiline UTF-8 clipboard"
} finally {
    $client.Dispose()
}
