[CmdletBinding()]
param(
    [switch] $AppOnly,
    [string] $Python = 'python',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'build\release')
)

$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -CommandType Application).Source
$sdk = & $dotnet --list-sdks | Select-Object -Last 1
if ($sdk -notmatch '^(\S+) \[(.+)\]$') { throw 'A .NET SDK is required to build SSH Manager.' }
$compiler = Join-Path $Matches[2] "$($Matches[1])\Roslyn\bincore\csc.dll"
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $framework)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$refs = 'mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Xml','System.Xml.Linq','System.IO.Compression','System.Runtime.Serialization' |
    ForEach-Object { '/reference:' + (Join-Path $framework ($_ + '.dll')) }

$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$app = Join-Path $output 'SshManager.exe'
$sources = (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File).FullName
$vtArchiveName = 'VtNetCore-4e723ac24e42316e533e6aa4c427ab5273077214'
$thirdParty = Join-Path $PSScriptRoot 'third_party'
$vtBuild = Join-Path $PSScriptRoot 'build\native-terminal'
Expand-Archive -LiteralPath (Join-Path $thirdParty "$vtArchiveName.zip") -DestinationPath $vtBuild -Force
$vtSource = Join-Path $vtBuild "$vtArchiveName\VtNetCore"
$oscReader = Join-Path $vtSource 'XTermParser\XTermSequenceReader.cs'
$oscCode = [IO.File]::ReadAllText($oscReader)
$oscTerminator = 'if (readingCommand || next == 0x07 || next == 0x9C) // BEL or ST'
$replacement = @'
if (next == 0x1B && stream.PeekAhead(0) == '\\')
                {
                    stream.ReadRaw();
                    next = (char)0x9C;
                }

                if (readingCommand || next == 0x07 || next == 0x9C) // BEL or ST
'@
# VtNetCore handles BEL and one-byte ST but misses the standard seven-bit ST (ESC followed by backslash).
# PeekAhead also lets DataConsumer retain a partial terminator when its two bytes arrive separately.
if (-not $oscCode.Contains($oscTerminator)) { throw 'The VtNetCore OSC patch no longer matches the pinned source.' }
[IO.File]::WriteAllText($oscReader, $oscCode.Replace($oscTerminator, $replacement))
$sources += (Get-ChildItem -LiteralPath $vtSource -Recurse -Filter '*.cs' -File).FullName
$vtLicense = Join-Path $thirdParty 'VtNetCore-LICENSE.txt'
$projectLicense = Join-Path $PSScriptRoot 'LICENSE'
$icon = Join-Path $PSScriptRoot 'assets\SshManager.ico'
$mark = Join-Path $PSScriptRoot 'assets\AppMark.png'
$manifest = Join-Path $PSScriptRoot 'installer\app.manifest'
$terminalFonts = Join-Path $thirdParty 'CascadiaMono\CascadiaMono-2407.24.zip'
$terminalFontLicense = Join-Path $thirdParty 'CascadiaMono\LICENSE.txt'
& $dotnet $compiler /nologo /noconfig /nostdlib+ /target:winexe /platform:anycpu /optimize+ /deterministic+ /codepage:65001 `
    "/out:$app" "/win32icon:$icon" "/win32manifest:$manifest" "/resource:$mark,SshManager.AppMark.png" `
    "/resource:$terminalFonts,SshManager.CascadiaMono.zip" "/resource:$terminalFontLicense,SshManager.CascadiaMono-LICENSE.txt" `
    "/resource:$vtLicense,SshManager.VtNetCore-LICENSE.txt" "/resource:$projectLicense,SshManager.LICENSE.txt" $refs $sources
if ($LASTEXITCODE -ne 0) { throw 'App compilation failed.' }

if (-not $AppOnly) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion
    $msi = Join-Path $output 'SshManagerSetup.msi'
    & $Python (Join-Path $PSScriptRoot 'installer\package.py') $app $msi $version
    if ($LASTEXITCODE -ne 0) { throw 'MSI packaging failed. Use Windows Python 3.12, which includes msilib.' }
    Get-Item -LiteralPath $msi
}
Get-Item -LiteralPath $app
