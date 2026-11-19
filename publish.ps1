[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $NotesFile,
    [string] $Python = 'python'
)

$ErrorActionPreference = 'Stop'
$repository = 'Clarkufa/sshmanager'
$notes = (Resolve-Path -LiteralPath $NotesFile).Path
if (-not (Test-Path -LiteralPath $notes -PathType Leaf)) {
    throw 'Provide a file with release notes.'
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

    $output = Join-Path $PSScriptRoot 'build\publish'
    & (Join-Path $PSScriptRoot 'build.ps1') -Python $Python -OutputDirectory $output
    $exe = Join-Path $output 'SshManager.exe'
    $msi = Join-Path $output 'SshManagerSetup.msi'
    $file = (Get-Item -LiteralPath $exe).VersionInfo
    $version = "$($file.FileMajorPart).$($file.FileMinorPart).$($file.FileBuildPart)"
    $tag = "v$version"
    [xml]$manifest = Get-Content -Raw -LiteralPath 'installer\app.manifest'
    if ($manifest.assembly.assemblyIdentity.version -ne $file.FileVersion) {
        throw 'The manifest version does not match the executable.'
    }

    $tagCommit = gh api "repos/$repository/commits/$tag" --jq .sha 2>$null
    if ($LASTEXITCODE -eq 0 -and $tagCommit -ne $commit) {
        throw "The existing tag $tag points to a different commit."
    }
    gh release create $tag $msi $exe --repo $repository --target $commit --title "SSH Manager $version" --notes-file $notes --latest
    if ($LASTEXITCODE -ne 0) { throw 'GitHub did not publish the release.' }
}
finally { Pop-Location }
