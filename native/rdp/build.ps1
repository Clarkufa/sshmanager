[CmdletBinding()]
param([Parameter(Mandatory)] [string] $BuildDirectory)

$ErrorActionPreference = 'Stop'
$root = (New-Item -ItemType Directory -Path $BuildDirectory -Force).FullName
$version = '3.33.1'
$sourceHash = '97207E81C0EB465774852F7E88A875B0238DA612EB1EB9439692AAEB25FB7C18'
# This vcpkg revision supplies zlib 1.4 and the Windows OpenSSL build recipe.
$vcpkgRevision = '9fa95bf687fa6ae7f2da0673a8067e5fc1515c27'
$source = Join-Path $root "freerdp-$version"
$archive = "$source.zip"

function Run([string] $Program, [string[]] $Arguments) {
    & $Program @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$Program exited with code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $sourceHash) {
    $download = "$archive.download"
    Run 'curl.exe' @('--fail', '--location', '--silent', '--show-error', '--retry', '2',
        "https://github.com/FreeRDP/FreeRDP/releases/download/$version/freerdp-$version.zip", '--output', $download)
    if ((Get-FileHash -LiteralPath $download).Hash -ne $sourceHash) { throw 'FreeRDP archive hash mismatch.' }
    Move-Item -LiteralPath $download -Destination $archive -Force
}
$sourceStamp = Join-Path $source '.extracted'
if (-not (Test-Path -LiteralPath $sourceStamp)) {
    Run 'tar.exe' @('-xf', $archive, '-C', $root)
    New-Item -ItemType File -Path $sourceStamp -Force | Out-Null
}

# During an in-session resize xrdp can send graphics updates before Font Map.
# Discard them without decoding, keeping the initial activation check intact.
$updateSource = Join-Path $source 'libfreerdp/core/update.c'
$updateText = [IO.File]::ReadAllText($updateSource)
$receive = $updateText.IndexOf('BOOL update_recv(rdpUpdate* update, wStream* s)', [StringComparison]::Ordinal)
$guard = "if (!rdp_has_reached_state(update->context->rdp, CONNECTION_STATE_ACTIVE))$([char]10)$([char]9)$([char]9)return FALSE;"
$activate = @'
if (!rdp_has_reached_state(update->context->rdp, CONNECTION_STATE_ACTIVE))
	{
		/* xrdp can send graphics before Font Map during reactivation. */
		return context->gdi && Stream_SafeSeek(s, Stream_GetRemainingLength(s));
	}
'@
if ($receive -lt 0) { throw 'FreeRDP update_recv was not found.' }
if (-not $updateText.Contains('xrdp can send graphics before Font Map during reactivation.')) {
    $at = $updateText.IndexOf($guard, $receive, [StringComparison]::Ordinal)
    if ($at -lt 0 -or $at - $receive -gt 400) { throw 'The FreeRDP xrdp compatibility patch no longer applies.' }
    [IO.File]::WriteAllText($updateSource, $updateText.Remove($at, $guard.Length).Insert($at, $activate.Replace("`r`n", "`n")))
}

$vcpkg = Join-Path $root 'vcpkg'
$checkout = (& git -C $vcpkg rev-parse HEAD 2>$null)
if ($checkout -ne $vcpkgRevision) {
    New-Item -ItemType Directory -Path $vcpkg -Force | Out-Null
    Run 'git' @('-C', $vcpkg, 'init', '--quiet')
    Run 'git' @('-C', $vcpkg, 'fetch', '--depth', '1', 'https://github.com/microsoft/vcpkg.git', $vcpkgRevision)
    Run 'git' @('-C', $vcpkg, 'checkout', '--detach', 'FETCH_HEAD')
    Run (Join-Path $vcpkg 'bootstrap-vcpkg.bat') @('-disableMetrics')
} elseif (-not (Test-Path -LiteralPath (Join-Path $vcpkg 'vcpkg.exe'))) {
    Run (Join-Path $vcpkg 'bootstrap-vcpkg.bat') @('-disableMetrics')
}

# Use the supported OpenSSL 3.5 LTS branch with the pinned vcpkg build recipe.
$ports = Join-Path $root 'ports'
$opensslVersion = '3.5.9'
$installed = Join-Path $root "vcpkg-installed-$opensslVersion"
$openssl = (New-Item -ItemType Directory -Path (Join-Path $ports 'openssl') -Force).FullName
Get-ChildItem -LiteralPath (Join-Path $vcpkg 'ports/openssl') | Copy-Item -Destination $openssl -Recurse -Force
$portJson = Join-Path $openssl 'vcpkg.json'
$port = [IO.File]::ReadAllText($portJson) | ConvertFrom-Json
$port.version = $opensslVersion
$port.'port-version' = 0
[IO.File]::WriteAllText($portJson, ($port | ConvertTo-Json -Depth 20))
$portCmake = Join-Path $openssl 'portfile.cmake'
$recipe = [IO.File]::ReadAllText($portCmake)
$upstreamHash = '40f0b346a9a5cf4cb37f1ecebdc74750c39a95783710061bfac58964883b0831543ecee0aee9f747ea093781b13c58a543b0b935d222519e3cb19648d77a0276'
$opensslHash = 'ab29a6ed40dd8c74420b9bd19c5ac119b0f191aee72d555ba5a0601456155d39c809c77300d98c7e0bce607f32c1f7a83fc7f69f86b9909fe557bba3aa2e468f'
if (-not $recipe.Contains($upstreamHash)) { throw 'The pinned vcpkg OpenSSL recipe changed.' }
[IO.File]::WriteAllText($portCmake, $recipe.Replace($upstreamHash, $opensslHash))

$triplets = (New-Item -ItemType Directory -Path (Join-Path $root 'triplets') -Force).FullName

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$cmake = (Get-Command cmake.exe -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $cmake = & $vswhere -latest -products '*' -find '**\CMake\bin\cmake.exe' | Select-Object -First 1
}
if (-not $cmake) { throw 'Install Visual Studio 2022 C++ tools and CMake to build the Windows RDP client.' }

$executables = @{}
foreach ($arch in 'x64', 'x86') {
    $platform = if ($arch -eq 'x64') { 'x64' } else { 'Win32' }
    $triplet = "$arch-windows-static"
    $destination = Join-Path $root "worker-$arch-$opensslVersion"
    $baseTriplet = Join-Path $vcpkg "triplets/$triplet.cmake"
    if (-not (Test-Path -LiteralPath $baseTriplet)) {
        $baseTriplet = Join-Path $vcpkg "triplets/community/$triplet.cmake"
    }
    $tripletSource = [IO.File]::ReadAllText($baseTriplet)
    [IO.File]::WriteAllText((Join-Path $triplets "$triplet.cmake"), $tripletSource + "`nset(VCPKG_BUILD_TYPE release)`n")
    Run (Join-Path $vcpkg 'vcpkg.exe') @('install', "openssl:$triplet", "zlib:$triplet", '--classic', '--recurse',
        "--overlay-ports=$ports", "--overlay-triplets=$triplets", "--x-install-root=$installed")
    $opensslHeader = [IO.File]::ReadAllText((Join-Path $installed "$triplet/include/openssl/opensslv.h"))
    if (-not $opensslHeader.Contains('"' + $opensslVersion + '"')) { throw 'Unexpected OpenSSL build version.' }
    Run $cmake @('-S', $PSScriptRoot, '-B', $destination, '-G', 'Visual Studio 17 2022', '-A', $platform,
        "-DFREERDP_SOURCE=$source", "-DCMAKE_TOOLCHAIN_FILE=$vcpkg/scripts/buildsystems/vcpkg.cmake",
        "-DVCPKG_TARGET_TRIPLET=$triplet", "-DVCPKG_INSTALLED_DIR=$installed", "-DVCPKG_OVERLAY_TRIPLETS=$triplets")
    Run $cmake @('--build', $destination, '--config', 'Release', '--target', 'SshManagerRdp', '--parallel')
    $executables[$arch] = Join-Path $destination 'Release/SshManagerRdp.exe'
}

$licenses = @(
    (Join-Path $source 'LICENSE'),
    (Join-Path $installed 'x64-windows-static/share/openssl/copyright'),
    (Join-Path $installed 'x64-windows-static/share/zlib/copyright')
) | ForEach-Object { [IO.File]::ReadAllText($_) }
$licenseFile = Join-Path $root 'Rdp-LICENSES.txt'
[IO.File]::WriteAllText($licenseFile, ($licenses -join "`r`n`r`n"))
[pscustomobject]@{
    Executables = $executables
    Licenses = $licenseFile
}
