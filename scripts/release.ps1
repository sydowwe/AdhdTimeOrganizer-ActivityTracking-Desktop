<#
.SYNOPSIS
    Publishes the tracker and packs it with Velopack: an installer (Setup.exe) plus the full and delta packages the
    installed trackers update from. Optionally uploads them to GitHub Releases.

.DESCRIPTION
    The version is the csproj's <Version>; raise it before releasing — Velopack only offers a higher one.

    1. dotnet publish, Release, self-contained (.NET and the Windows App SDK both travel with the app, so nothing has to
       be installed first; deltas keep the updates small).
    2. Checks the publish folder holds what the app cannot run without and no tool puts there reliably: the extracted
       Microsoft.WindowsAppRuntime.Insights.Resource.dll (notifications), the icon file (AppNotificationManager.Register)
       and the Windows App SDK runtime.
    3. Downloads the previous release from GitHub (if any), so vpk can build a delta against it.
    4. vpk pack under the package id AdhdTimeOrganizer.Tracker — it installs to %LocalAppData%\AdhdTimeOrganizer.Tracker\,
       deliberately NOT the data folder (%LocalAppData%\AdhdTimeOrganizer.ActivityTracking.Desktop\) or the log folder
       (%LocalAppData%\AdhdTimeOrganizer\): an uninstall deletes the install folder whole.
    5. With -Upload: vpk upload github, as a published release tagged v<version>.

    Needs vpk at the Velopack package's version: `dotnet tool restore` (see .config/dotnet-tools.json).
    Output goes to artifacts-release\ (gitignored). Nothing is installed or run.

.EXAMPLE
    ./scripts/release.ps1                                   # publish + pack locally
    ./scripts/release.ps1 -Upload -Token $env:GITHUB_TOKEN  # what the release workflow runs
#>
param(
    [string]$Runtime = 'win-x64',
    [switch]$Upload,
    [string]$Token
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$appProject = Join-Path $root 'AdhdTimeOrganizer.ActivityTracking.Desktop\AdhdTimeOrganizer.ActivityTracking.Desktop.csproj'
$repoUrl = 'https://github.com/sydowwe/AdhdTimeOrganizer-ActivityTracking-Desktop'
$packId = 'AdhdTimeOrganizer.Tracker'

$version = ([xml](Get-Content $appProject -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $appProject" }

# The workflow runs on a tag; a tag that disagrees with the csproj would publish one version under another's name.
if ($env:GITHUB_REF_NAME -and $env:GITHUB_REF_NAME -ne "v$version") {
    throw "Tag $env:GITHUB_REF_NAME does not match the csproj version $version"
}

$out = Join-Path $root "artifacts-release\$Runtime"
$publish = Join-Path $out 'publish'
$releases = Join-Path $out 'releases'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force $releases | Out-Null

Write-Host "Publishing $version ($Runtime)..."
dotnet publish $appProject -c Release -r $Runtime --self-contained true -o $publish `
    --artifacts-path (Join-Path $root 'artifacts-cli') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($required in @('AdhdTimeOrganizer.exe', 'Microsoft.WindowsAppRuntime.Insights.Resource.dll',
                        'Microsoft.WindowsAppRuntime.dll', 'timeorganizer-icon.ico', 'Velopack.dll')) {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "The publish folder is missing $required" }
}

if (Get-Command vpk -ErrorAction SilentlyContinue) { $vpk = @('vpk') } else { $vpk = @('dotnet', 'vpk') }
function Invoke-Vpk([string[]]$arguments) {
    & $vpk[0] @($vpk | Select-Object -Skip 1) @arguments
    if ($LASTEXITCODE -ne 0) { throw "vpk $($arguments[0]) failed ($LASTEXITCODE)" }
}

# Without the previous full package vpk can only build a full one; the first release has none to fetch.
try {
    Invoke-Vpk @('download', 'github', '--repoUrl', $repoUrl, '--outputDir', $releases, '--channel', $Runtime)
} catch {
    Write-Warning "No previous release to build a delta from: $_"
}

Write-Host "Packing $packId $version..."
Invoke-Vpk @('pack',
    '--packId', $packId,
    '--packVersion', $version,
    '--packTitle', 'AdhdTimeOrganizer Activity Tracker',
    '--packAuthors', 'sydowwe',
    '--packDir', $publish,
    '--mainExe', 'AdhdTimeOrganizer.exe',
    '--icon', (Join-Path $publish 'timeorganizer-icon.ico'),
    '--channel', $Runtime,
    '--outputDir', $releases)

if ($Upload) {
    if (-not $Token) { throw '-Upload needs -Token' }
    Invoke-Vpk @('upload', 'github', '--repoUrl', $repoUrl, '--outputDir', $releases, '--channel', $Runtime,
                 '--tag', "v$version", '--releaseName', "Activity Tracker $version", '--publish', '--token', $Token)
}

Write-Host "Done: $releases"
