param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe).Path)
$profileType = $assembly.GetType('SshManager.VncProfile', $true)
$filesType = $assembly.GetType('SshManager.TightVncFiles', $true)
$token = [Threading.CancellationToken]::None
$work = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('sshm-tight-' + [Guid]::NewGuid().ToString('N')))
$portFile = Join-Path $work.FullName 'port'
$fixture = Join-Path $PSScriptRoot '../vnc/tight-files-fixture.py'
$server = Start-Process $Python -WindowStyle Hidden -PassThru -ArgumentList ('"{0}" "{1}"' -f $fixture, $portFile) -RedirectStandardOutput (Join-Path $work.FullName 'server.out') -RedirectStandardError (Join-Path $work.FullName 'server.err')
$null = $server.Handle

function New-Files([int]$Port, [bool]$ViewOnly = $false) {
    $profile = [Activator]::CreateInstance($profileType, [object[]]@(
        'a872c194a5bb49679ca485c1ebf5c6ab', 'Fixture', '127.0.0.1', $Port, '', '', 'fit', $ViewOnly, $true, $false, ''
    ))
    [Activator]::CreateInstance($filesType, [object[]]@($profile, ''))
}

function Expect-Error($Operation, [string]$Type) {
    $caught = $null
    try { & $Operation }
    catch { $caught = $_.Exception }
    if ($null -eq $caught) { throw "Expected exception $Type" }
    while ($null -ne $caught -and $caught.GetType().Name -ne $Type) { $caught = $caught.InnerException }
    if ($null -eq $caught) { throw "Unexpected exception, expected $Type" }
}

try {
    for ($attempt = 0; $attempt -lt 100 -and !(Test-Path $portFile) -and !$server.HasExited; $attempt++) {
        Start-Sleep -Milliseconds 100
    }
    $port = [int](Get-Content -LiteralPath $portFile)
    if ($port -le 0) { throw 'Fixture did not publish a port' }
    $viewOnly = New-Files $port $true
    try { Expect-Error { $viewOnly.Initialize() } 'VncFilesUnavailable' }
    finally { $viewOnly.Dispose() }

    $client = New-Files $port
    try {
        $client.Initialize()
        if (!$client.Connected -or $client.CanReplace -or $client.Protocol -ne 'TightVNC') { throw 'Bad native negotiation' }
        $drives = $client.List('/', $token)
        if ($drives.Count -ne 1 -or $drives[0].Name -ne 'C:' -or !$drives[0].Metadata.Directory) { throw 'Bad drive listing' }

        $name = [string][char]0x0442 + [char]0x0435 + [char]0x0441 + [char]0x0442
        $folder = '/C:/' + $name
        $temp = $folder + '/payload.part'
        $dest = $folder + '/' + $name + '.bin'
        $client.CreateDirectory($folder)
        $bytes = New-Object byte[] 134071
        [Random]::new(10).NextBytes($bytes)
        $input = [IO.MemoryStream]::new($bytes)
        $output = [IO.MemoryStream]::new()
        $progress = [Action[long,long]]{}
        $created = [Action]{}
        $client.Upload($temp, $input, $progress, $token, $created, $null)
        $client.Move($temp, $dest, $false)
        $metadata = $client.Info($dest)
        if ($metadata.Size -ne $bytes.Length -or $metadata.Directory) { throw 'Bad file metadata' }
        $client.Download($dest, $output, $progress, $token)
        if ([Convert]::ToBase64String($output.ToArray()) -ne [Convert]::ToBase64String($bytes)) { throw 'Changed file payload' }
        $client.Delete($dest, $false)
        $client.Delete($folder, $true)
    }
    finally { $client.Dispose() }

    foreach ($mode in @('absent', 'plain')) {
        $client = New-Files $port
        try { Expect-Error { $client.Initialize() } 'VncFilesUnavailable' }
        finally { $client.Dispose() }
    }
    $client = New-Files $port
    try {
        $client.Initialize()
        Expect-Error { $client.List('/', $token) } 'IOException'
    }
    finally { $client.Dispose() }
    $client = New-Files $port
    try {
        $client.Initialize()
        Expect-Error { $client.List('/', $token) } 'EndOfStreamException'
        if ($client.Connected) { throw 'Broken file connection remained open' }
    }
    finally { $client.Dispose() }
    if (!$server.WaitForExit(5000)) { throw 'Fixture did not exit' }
    Get-Content -LiteralPath (Join-Path $work.FullName 'server.out')
    if ($server.ExitCode -ne 0) { throw "Fixture exited with code $($server.ExitCode)" }
}
finally {
    if (!$server.HasExited) { $server.Kill(); $server.WaitForExit() }
    Get-Content -LiteralPath (Join-Path $work.FullName 'server.err')
    foreach ($name in @('port', 'server.out', 'server.err')) {
        $file = Join-Path $work.FullName $name
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    Remove-Item -LiteralPath $work.FullName
}
