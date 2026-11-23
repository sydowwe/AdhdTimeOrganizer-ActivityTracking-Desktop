<#
.SYNOPSIS
    Builds the desktop tracker and runs its tests, without touching bin/ or obj/.

.DESCRIPTION
    Everything is built into artifacts-cli/ (gitignored), so a tracker running from bin/ — or Rider
    holding it — never blocks the build with "file is being used by another process".
    The test project is an xunit v3 executable and is run directly; `dotnet test` is not used.

.EXAMPLE
    ./scripts/test.ps1                                  # build + run everything
    ./scripts/test.ps1 -Class '*ActivityRecorderTests'  # one class (xunit wildcard syntax)
    ./scripts/test.ps1 -Method '*.Normalize*'
    ./scripts/test.ps1 -NotTrait 'Needs=Desktop'        # everything a build runner can run (what CI runs)
    ./scripts/test.ps1 -NotClass '*NativeSmokeTests'    # everything but one class
    ./scripts/test.ps1 -BuildOnly                       # compile check, Debug and Release

    Each filter takes a comma-separated list. Includes of one kind OR together, excludes AND together, and different
    kinds AND together (xunit's simple filtering).
#>
param(
    [string[]]$Class = @(),
    [string[]]$Method = @(),
    [string[]]$Trait = @(),
    [string[]]$NotClass = @(),
    [string[]]$NotTrait = @(),
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts-cli'
$testProject = Join-Path $root 'AdhdTimeOrganizer.ActivityTracking.Desktop.Tests\AdhdTimeOrganizer.ActivityTracking.Desktop.Tests.csproj'
$appProject = Join-Path $root 'AdhdTimeOrganizer.ActivityTracking.Desktop\AdhdTimeOrganizer.ActivityTracking.Desktop.csproj'

Write-Host "Building into artifacts-cli..."
dotnet build $testProject --artifacts-path $artifacts --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($BuildOnly) {
    # Release compiles the other ApiBaseUrl branch in AppConfig.
    dotnet build $appProject -c Release --artifacts-path $artifacts --nologo -v quiet
    exit $LASTEXITCODE
}

$exe = Get-ChildItem (Join-Path $artifacts 'bin') -Recurse -Filter 'AdhdTimeOrganizer.ActivityTracking.Desktop.Tests.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "Test executable not found under $artifacts" }

$filters = @()
foreach ($c in $Class) { $filters += @('-class', $c) }
foreach ($m in $Method) { $filters += @('-method', $m) }
foreach ($t in $Trait) { $filters += @('-trait', $t) }
foreach ($c in $NotClass) { $filters += @('-class-', $c) }
foreach ($t in $NotTrait) { $filters += @('-trait-', $t) }

& $exe.FullName @filters
exit $LASTEXITCODE
