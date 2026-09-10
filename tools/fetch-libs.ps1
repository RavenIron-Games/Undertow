# Populates .\libs from a local Valheim install.
# Run once per machine, from the solution root:  .\tools\fetch-libs.ps1
# Auto-detects Steam; pass -ValheimPath to override.
#
# libs\ is gitignored on purpose - game assemblies are not ours to redistribute.

param(
    [string]$ValheimPath,
    # Where the *publicized* assemblies live, if not one of the known locations below.
    [string]$PublicizedPath,
    # Where BepInEx\core lives, if not one of the known locations below.
    [string]$BepInExCorePath
)

$ErrorActionPreference = 'Stop'

function Find-Valheim {
    $candidates = @()

    # Default Steam locations
    $candidates += "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
    $candidates += "C:\Program Files\Steam\steamapps\common\Valheim"

    # Extra Steam libraries declared in libraryfolders.vdf
    $vdf = "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches |
            ForEach-Object { $_.Matches } |
            ForEach-Object {
                $p = $_.Groups[1].Value -replace '\\\\', '\'
                $candidates += Join-Path $p "steamapps\common\Valheim"
            }
    }

    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "valheim_Data\Managed")) { return $c }
    }
    return $null
}

if (-not $ValheimPath) { $ValheimPath = Find-Valheim }

if (-not $ValheimPath -or -not (Test-Path $ValheimPath)) {
    Write-Host "Could not locate Valheim automatically." -ForegroundColor Red
    Write-Host "Re-run with:  .\tools\fetch-libs.ps1 -ValheimPath 'D:\Games\Valheim'"
    exit 1
}

Write-Host "Valheim: $ValheimPath" -ForegroundColor Cyan

$managed    = Join-Path $ValheimPath "valheim_Data\Managed"
# The publicized assemblies are NOT necessarily under the game folder.
#
# Learned 2026-09-10, the day Valheim shipped 1.0.7: a game update does NOT regenerate the
# in-game publicized_assemblies folder. It still held 0.2x files from July, so copying from
# it would have quietly rebuilt every mod against the OLD API - a clean build, and a mod that
# dies at runtime. The owner publicizes into a working folder instead. So: consider every
# known location, take the NEWEST, and refuse outright if it predates the game's own assembly.
$publicizedCandidates = @()
if ($PublicizedPath) { $publicizedCandidates += $PublicizedPath }
$publicizedCandidates += (Join-Path $env:USERPROFILE "Desktop\ValheimModding\publicized_assemblies")
$publicizedCandidates += (Join-Path $managed "publicized_assemblies")

$publicized = $null
$publicizedStamp = [datetime]::MinValue
foreach ($candidate in $publicizedCandidates) {
    $probe = Join-Path $candidate "assembly_valheim_publicized.dll"
    if (Test-Path $probe) {
        $stamp = (Get-Item $probe).LastWriteTime
        if ($stamp -gt $publicizedStamp) {
            $publicized = $candidate
            $publicizedStamp = $stamp
        }
    }
}
$bepinex    = Join-Path $ValheimPath "BepInEx\core"
$libs       = Join-Path $PSScriptRoot "..\libs"

New-Item -ItemType Directory -Force -Path $libs | Out-Null
$libs = (Resolve-Path $libs).Path

if (-not $publicized) {
    Write-Host "No publicized assemblies found. Looked in:" -ForegroundColor Red
    $publicizedCandidates | ForEach-Object { Write-Host "  $_" }
    Write-Host "Generate them first (BepInEx.AssemblyPublicizer.MSBuild or a publicizer tool),"
    Write-Host "or point at them:  .\tools\fetch-libs.ps1 -PublicizedPath 'C:\path\to\publicized_assemblies'"
    exit 1
}

# The guard that matters. A publicized set older than the game means the game updated and
# nobody re-publicized; copying it produces a clean build against a dead API.
$gameAssembly = Join-Path $managed "assembly_valheim.dll"
if ((Test-Path $gameAssembly) -and $publicizedStamp -lt (Get-Item $gameAssembly).LastWriteTime) {
    Write-Host "STALE publicized assemblies - refusing to copy." -ForegroundColor Red
    Write-Host "  publicized: $publicized"
    Write-Host "              $($publicizedStamp.ToString('yyyy-MM-dd HH:mm'))"
    Write-Host "  the game:   $($(Get-Item $gameAssembly).LastWriteTime.ToString('yyyy-MM-dd HH:mm'))"
    Write-Host ""
    Write-Host "Valheim has been updated since these were generated. Re-publicize the current"
    Write-Host "assembly_valheim.dll, then run this again. Building against the old ones gives"
    Write-Host "a clean compile and a mod that throws MissingMethodException in-game."
    exit 1
}

Write-Host "Publicized: $publicized ($($publicizedStamp.ToString('yyyy-MM-dd HH:mm')))" -ForegroundColor Cyan

# BepInEx is not necessarily in the game folder either: this machine runs its client through
# Gale, which keeps a full BepInEx per profile and leaves the Steam install vanilla. Only
# BepInEx.dll and 0Harmony.dll are wanted, and they are identical across profiles, so any
# profile that has them will do - newest wins.
if (-not (Test-Path $bepinex)) {
    $coreCandidates = @()
    if ($BepInExCorePath) { $coreCandidates += $BepInExCorePath }
    $galeProfiles = Join-Path $env:APPDATA "com.kesomannen.gale\valheim\profiles"
    if (Test-Path $galeProfiles) {
        $coreCandidates += (Get-ChildItem $galeProfiles -Directory |
            ForEach-Object { Join-Path $_.FullName "BepInEx\core" })
    }

    $bestCore = $null
    $bestCoreStamp = [datetime]::MinValue
    foreach ($candidate in $coreCandidates) {
        $probe = Join-Path $candidate "BepInEx.dll"
        if (Test-Path $probe) {
            $stamp = (Get-Item $probe).LastWriteTime
            if ($stamp -gt $bestCoreStamp) { $bestCore = $candidate; $bestCoreStamp = $stamp }
        }
    }
    if ($bestCore) {
        $bepinex = $bestCore
        Write-Host "BepInEx:    $bepinex" -ForegroundColor Cyan
    }
}

if (-not (Test-Path $bepinex)) {
    Write-Host "BepInEx\core not found. Looked in the game folder and every Gale profile." -ForegroundColor Red
    Write-Host "Install BepInExPack Valheim, or point at it:"
    Write-Host "  .\tools\fetch-libs.ps1 -BepInExCorePath 'C:\path\to\BepInEx\core'"
    exit 1
}

# source folder : file names
$sets = @(
    @{ Path = $publicized; Files = @(
        "assembly_valheim_publicized.dll",
        "assembly_utils_publicized.dll",
        "assembly_postprocessing_publicized.dll",
        "assembly_lux_publicized.dll",
        "assembly_sunshafts_publicized.dll",
        "assembly_guiutils_publicized.dll"
    )},
    @{ Path = $managed; Files = @(
        "UnityEngine.dll",
        "UnityEngine.CoreModule.dll",
        "UnityEngine.PhysicsModule.dll",
        "UnityEngine.ParticleSystemModule.dll",
        "UnityEngine.AudioModule.dll",
        "UnityEngine.ImageConversionModule.dll"
    )},
    @{ Path = $bepinex; Files = @(
        "BepInEx.dll",
        "0Harmony.dll"
    )}
)

$copied = 0
$missing = @()

foreach ($set in $sets) {
    foreach ($f in $set.Files) {
        $src = Join-Path $set.Path $f
        if (Test-Path $src) {
            Copy-Item $src -Destination $libs -Force
            Write-Host "  + $f"
            $copied++
        } else {
            $missing += $f
        }
    }
}

Write-Host ""
Write-Host "Copied $copied file(s) to $libs" -ForegroundColor Green

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "Not found (build will fail if any are actually referenced):" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "  - $_" }
}
