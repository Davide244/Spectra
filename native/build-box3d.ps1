<#
.SYNOPSIS
    Builds the vendored Box3D submodule into runtimes/<rid>/native/.

.DESCRIPTION
    Box3D is a git submodule pinned to an exact commit (see native/README.md).
    This script is the ONE place its build options live, so a developer build, a
    CI build and a release build cannot drift apart in a way that only shows up
    as a physics difference.

    No Developer Command Prompt is needed and none should be used: CMake's
    Visual Studio generator locates MSVC through the registry and drives MSBuild
    itself. `cl` on PATH is irrelevant here.

.PARAMETER Rid
    The .NET runtime identifier to build for. Selects the CMake architecture.

.PARAMETER Config
    CMake build configuration. Release unless you are debugging the solver.

.PARAMETER Clean
    Delete the intermediate build tree first.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',

    [ValidateSet('Release', 'Debug', 'RelWithDebInfo')]
    [string]$Config = 'Release',

    [switch]$Clean,

    # Rebuild the ABI manifest from the C compiler and overwrite the committed
    # copy. A deliberate step, taken when the submodule pin moves, so the layout
    # diff lands in review instead of in a corrupted stack.
    [switch]$Abi
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'external/box3d'
$buildDir = Join-Path $repoRoot "build/box3d-$Rid"
$outDir = Join-Path $PSScriptRoot "runtimes/$Rid/native"

if (-not (Test-Path (Join-Path $source 'CMakeLists.txt'))) {
    throw "Box3D sources are missing at '$source'. The submodule is not checked out — run: git submodule update --init --recursive"
}

$arch = switch ($Rid) {
    'win-x64'   { 'x64' }
    'win-arm64' { 'ARM64' }
}

if ($Clean -and (Test-Path $buildDir)) {
    Write-Host "Cleaning $buildDir"
    Remove-Item -Recurse -Force $buildDir
}

# The option set is the contract, and two of these are decisions rather than
# preferences:
#
#   BOX3D_DOUBLE_PRECISION=OFF  The float build, decided 2026-08-21. The flag is
#                               ABI-affecting — the two modes ship as mutually
#                               exclusive builds — so flipping it invalidates
#                               every bound struct layout, not just performance.
#   BUILD_SHARED_LIBS=ON        A DLL beside the managed assembly, which is what
#                               [LibraryImport] resolves and what a NativeAOT
#                               publish carries.
#
# The rest just refuse work we do not consume: samples pull a windowing stack we
# already have, and unit tests are upstream's, run upstream.
$cmakeArgs = @(
    '-S', $source
    '-B', $buildDir
    '-A', $arch
    '-DBOX3D_SAMPLES=OFF'
    '-DBOX3D_UNIT_TESTS=OFF'
    '-DBOX3D_BENCHMARKS=OFF'
    '-DBOX3D_DOCS=OFF'
    '-DBOX3D_DOUBLE_PRECISION=OFF'
    '-DBUILD_SHARED_LIBS=ON'
)

Write-Host "Configuring Box3D ($Rid, $Config)"
& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($LASTEXITCODE)." }

Write-Host "Building Box3D"
& cmake --build $buildDir --config $Config
if ($LASTEXITCODE -ne 0) { throw "CMake build failed ($LASTEXITCODE)." }

$dll = Get-ChildItem -Path $buildDir -Recurse -Filter 'box3d.dll' |
    Where-Object { $_.FullName -like "*$Config*" } |
    Select-Object -First 1
if ($null -eq $dll) { throw "Build reported success but produced no box3d.dll under '$buildDir'." }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item -Path $dll.FullName -Destination $outDir -Force

$pdb = [IO.Path]::ChangeExtension($dll.FullName, '.pdb')
if (Test-Path $pdb) { Copy-Item -Path $pdb -Destination $outDir -Force }

if ($Abi) {
    $probeSource = Join-Path $PSScriptRoot 'abi-probe'
    $probeBuild = Join-Path $repoRoot "build/abi-probe-$Rid"
    $manifest = Join-Path $PSScriptRoot 'box3d-abi.manifest'

    Write-Host "Configuring ABI probe"
    & cmake -S $probeSource -B $probeBuild -A $arch "-DBOX3D_BUILD_DIR=$buildDir"
    if ($LASTEXITCODE -ne 0) { throw "ABI probe configure failed ($LASTEXITCODE)." }

    & cmake --build $probeBuild --config $Config
    if ($LASTEXITCODE -ne 0) { throw "ABI probe build failed ($LASTEXITCODE)." }

    $probeExe = Get-ChildItem -Path $probeBuild -Recurse -Filter 'abi_probe.exe' | Select-Object -First 1
    if ($null -eq $probeExe) { throw "ABI probe built but produced no abi_probe.exe." }

    # The probe calls into the library for its version line, so the DLL has to
    # sit beside it.
    Copy-Item -Path (Join-Path $outDir 'box3d.dll') -Destination $probeExe.DirectoryName -Force

    $lines = & $probeExe.FullName
    if ($LASTEXITCODE -ne 0) { throw "ABI probe run failed ($LASTEXITCODE)." }

    # WriteAllLines, not Set-Content: PowerShell 5.1's -Encoding utf8 emits a
    # BOM, and this file is meant to be a clean, diffable, reviewable text
    # artifact. (.NET reads past a BOM happily, so the BOM was invisible until
    # someone read the diff.)
    [System.IO.File]::WriteAllLines($manifest, [string[]]$lines)

    Write-Host "ABI manifest -> $manifest"
}

$pinned = (& git -C $source rev-parse HEAD).Trim()
Write-Host ""
Write-Host "box3d.dll -> $outDir"
Write-Host "pinned at  $pinned"
