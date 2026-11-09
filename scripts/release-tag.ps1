<#
.SYNOPSIS
    Cuts a release: runs the tests, raises the csproj's <Version>, commits "release: <version>" and tags v<version>.
    Nothing is pushed — pushing the tag is what publishes (.github/workflows/release.yml), so that stays your step.

.DESCRIPTION
    Refuses unless you are on main with a clean working tree (the tag must name exactly what was tested), the new version
    is plain SemVer (x.y.z — the server parses X-Client-Version and the update feed takes no pre-releases) and higher than
    the current one (Velopack only ever offers a higher version), and the tag exists neither here nor on origin.

    The tests run before the version changes; a failure leaves everything as it was.

.EXAMPLE
    ./scripts/release-tag.ps1 -Bump patch            # 1.1.1 -> 1.1.2
    ./scripts/release-tag.ps1 -Version 1.2.0
    ./scripts/release-tag.ps1 -Bump minor -DryRun    # the checks and the new version only; changes nothing
#>
[CmdletBinding(DefaultParameterSetName = 'Bump')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Bump')]
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Bump,

    [Parameter(Mandatory, ParameterSetName = 'Version')]
    [string]$Version,

    # Skip the test run (it is the full suite, NativeSmokeTests included). The workflow still runs the pure tests.
    [switch]$SkipTests,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$appProject = Join-Path $root 'AdhdTimeOrganizer.ActivityTracking.Desktop\AdhdTimeOrganizer.ActivityTracking.Desktop.csproj'
$semVer = '^\d+\.\d+\.\d+$'

function Invoke-Git {
    $output = & git -C $root @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed ($LASTEXITCODE)" }
    $output
}

$branch = Invoke-Git branch --show-current
if ($branch -ne 'main') { throw "Releases are cut from main; this is '$branch'." }

$dirty = Invoke-Git status --porcelain
if ($dirty) { throw "The working tree is not clean — the tag must name exactly what was tested:`n$($dirty -join "`n")" }

$current = ([xml](Get-Content $appProject -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($current -notmatch $semVer) { throw "The csproj's <Version> '$current' is not plain x.y.z." }

if ($PSCmdlet.ParameterSetName -eq 'Bump') {
    $v = [version]$current
    $Version = switch ($Bump) {
        'major' { "$($v.Major + 1).0.0" }
        'minor' { "$($v.Major).$($v.Minor + 1).0" }
        'patch' { "$($v.Major).$($v.Minor).$($v.Build + 1)" }
    }
}
if ($Version -notmatch $semVer) { throw "'$Version' is not plain x.y.z." }
if ([version]$Version -le [version]$current) { throw "$Version is not higher than the current $current." }

$tag = "v$Version"
if (Invoke-Git tag --list $tag) { throw "Tag $tag already exists here." }
if (Invoke-Git ls-remote --tags origin "refs/tags/$tag") { throw "Tag $tag already exists on origin." }

Write-Host "Release $current -> $Version (tag $tag)"
if ($DryRun) {
    Write-Host 'Dry run: nothing tested, changed, committed or tagged.'
    return
}

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE); nothing was changed." }
}

# Only the <Version> element changes; the file keeps its encoding (no BOM today) and line endings.
$bytes = [System.IO.File]::ReadAllBytes($appProject)
$hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$text = [System.IO.File]::ReadAllText($appProject)
$pattern = '<Version>' + [regex]::Escape($current) + '</Version>'
if ([regex]::Matches($text, $pattern).Count -ne 1) { throw "Expected exactly one $pattern in the csproj." }
$text = [regex]::Replace($text, $pattern, "<Version>$Version</Version>")
[System.IO.File]::WriteAllText($appProject, $text, [System.Text.UTF8Encoding]::new($hasBom))

Invoke-Git add -- $appProject | Out-Null
Invoke-Git commit -m "release: $Version" | Out-Null
Invoke-Git tag -a $tag -m "Activity Tracker $Version" | Out-Null

Write-Host ''
Write-Host "Committed and tagged $tag. To publish (main first, so the tagged commit is on the branch):"
Write-Host '    git push origin main'
Write-Host "    git push origin $tag"
Write-Host 'The Release workflow then packs and uploads; installed trackers pick it up within 6 h, or 2 min after a restart.'
