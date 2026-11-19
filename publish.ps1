[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $NotesFile,
    [string] $Python = 'python',
    [string] $MacDmg,
    [string] $LinuxDeb,
    [string] $LinuxArchive
)

$ErrorActionPreference = 'Stop'
$repository = 'Clarkufa/sshmanager'
$notes = (Resolve-Path -LiteralPath $NotesFile).Path
if (-not (Test-Path -LiteralPath $notes -PathType Leaf)) {
    throw 'Provide a file with release notes.'
}
if ([bool]$LinuxDeb -ne [bool]$LinuxArchive) {
    throw 'Provide the new Linux DEB and TAR.GZ together.'
}

function Assert-ReleaseAsset($Asset, [string] $Path) {
    $file = Get-Item -LiteralPath $Path
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($null -eq $Asset -or $Asset.state -ne 'uploaded' -or
        $file.Length -eq 0 -or $Asset.size -ne $file.Length -or $Asset.digest -ne "sha256:$hash") {
        throw "GitHub asset '$($file.Name)' is missing or does not match the local file."
    }
}

$platformSources = [ordered]@{
    'SSH-Manager-macOS.dmg' = $MacDmg
    'SSH-Manager-Linux.deb' = $LinuxDeb
    'SSH-Manager-Linux.tar.gz' = $LinuxArchive
}
$platformLabels = @{
    'SSH-Manager-macOS.dmg' = 'macOS - DMG for Apple Silicon and Intel'
    'SSH-Manager-Linux.deb' = 'Linux - DEB for Debian and Ubuntu'
    'SSH-Manager-Linux.tar.gz' = 'Linux - TAR.GZ'
}
foreach ($name in @($platformSources.Keys)) {
    $source = $platformSources[$name]
    if ($source) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Platform build '$source' is not a file."
        }
        $platformSources[$name] = (Resolve-Path -LiteralPath $source).Path
    }
}

Push-Location $PSScriptRoot
try {
    $changes = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) {
        throw 'Commit the source changes before publishing.'
    }
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the current Git commit.' }
    $publishedCommit = gh api "repos/$repository/commits/$commit" --jq .sha
    if ($LASTEXITCODE -ne 0 -or $publishedCommit -ne $commit) {
        throw 'Push the source commit to GitHub before publishing.'
    }

    $latestJson = gh api "repos/$repository/releases/latest"
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the current GitHub release.' }
    $latest = $latestJson | ConvertFrom-Json
    if ($latest.tag_name -notmatch '^v\d+\.\d+\.\d+$') {
        throw 'The current release must use a Windows version tag such as v1.8.3.'
    }

    $output = Join-Path (Join-Path $PSScriptRoot 'build/publish') ([guid]::NewGuid().ToString('N'))
    New-Item -Path $output -ItemType Directory -Force | Out-Null
    $assets = [ordered]@{}
    $upload = @()
    foreach ($name in $platformSources.Keys) {
        $path = Join-Path $output $name
        $source = $platformSources[$name]
        $label = $platformLabels[$name]
        if ($source) {
            Copy-Item -LiteralPath $source -Destination $path
            if ((Get-Item -LiteralPath $path).Length -eq 0) { throw "Platform build '$source' is empty." }
        } else {
            $existing = $latest.assets | Where-Object { $_.name -ceq $name }
            if ($null -eq $existing) { throw "The current release is missing '$name'. Provide a platform build." }
            gh release download $latest.tag_name --repo $repository --pattern $name --dir $output
            if ($LASTEXITCODE -ne 0) { throw "Could not download '$name' from the current release." }
            Assert-ReleaseAsset $existing $path
            if ($existing.label) { $label = $existing.label }
        }
        $assets[$name] = $path
        $upload += "$path#$label"
    }

    & (Join-Path $PSScriptRoot 'build.ps1') -Python $Python -OutputDirectory $output
    $exe = Join-Path $output 'SshManager.exe'
    $msi = Join-Path $output 'SshManagerSetup.msi'
    $file = (Get-Item -LiteralPath $exe).VersionInfo
    $version = "$($file.FileMajorPart).$($file.FileMinorPart).$($file.FileBuildPart)"
    $tag = "v$version"
    [xml]$manifest = Get-Content -Raw -LiteralPath 'installer/app.manifest'
    if ($manifest.assembly.assemblyIdentity.version -ne $file.FileVersion) {
        throw 'The manifest version does not match the executable.'
    }
    if ([version]$version -le [version]($latest.tag_name.Substring(1))) {
        throw 'The Windows version must be newer than the current release.'
    }

    $tagCommit = gh api "repos/$repository/commits/$tag" --jq .sha 2>$null
    if ($LASTEXITCODE -eq 0 -and $tagCommit -ne $commit) {
        throw "The existing tag $tag points to a different commit."
    }
    $assets['SshManager.exe'] = $exe
    $assets['SshManagerSetup.msi'] = $msi
    $upload += $msi, $exe
    foreach ($path in $assets.Values) {
        if ((Get-Item -LiteralPath $path).Length -eq 0) { throw "Release asset '$path' is empty." }
    }

    gh release create $tag @upload --repo $repository --target $commit --title "SSH Manager $version" --notes-file $notes --draft --latest=false
    if ($LASTEXITCODE -ne 0) { throw "Could not create release draft $tag with all platform builds." }

    $draftJson = gh release view $tag --repo $repository --json isDraft,assets
    if ($LASTEXITCODE -ne 0) { throw "Could not verify release draft $tag." }
    $draft = $draftJson | ConvertFrom-Json
    if (-not $draft.isDraft -or @($draft.assets).Count -ne $assets.Count) {
        throw "Release draft $tag must contain exactly the five platform builds."
    }
    foreach ($name in $assets.Keys) {
        $uploaded = $draft.assets | Where-Object { $_.name -ceq $name }
        Assert-ReleaseAsset $uploaded $assets[$name]
    }

    $currentTag = gh api "repos/$repository/releases/latest" --jq .tag_name
    if ($LASTEXITCODE -ne 0 -or $currentTag -ne $latest.tag_name) {
        throw 'The latest release changed during publishing. Recheck the platform builds before publishing the draft.'
    }
    gh release edit $tag --repo $repository --draft=false --latest
    if ($LASTEXITCODE -ne 0) { throw "Could not publish verified release draft $tag." }
}
finally { Pop-Location }
