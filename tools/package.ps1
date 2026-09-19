# Builds the release zip: RavenIron-Undertow-<version>.zip in dist\.
# Ported from RagnaroksWrath, whose version was itself corrected on FireFront's upload day.
#
# Guards the mistakes a hand-made zip invites:
#   1. The THREE places the version lives (plugin const, csproj, manifest.json) drifting apart,
#      so a release can never claim a version its own log denies.
#   2. The store layout. Store files at the ROOT, the DLL under plugins\ — Hexium refuses a
#      root-level DLL.
#   3. The zip writer. PS 5.1's Compress-Archive builds archives Hexium's parser rejects
#      ("No manifest.json found"), while .NET Framework's CreateFromDirectory names nested
#      entries with spec-invalid BACKSLASHES. Entries are written by hand.
#   4. A missing icon. The store requires a 256x256 PNG and will reject the upload rather
#      than tell you why, so it is checked here instead.
#   5. A RED HARNESS. "Tests green" is this repo's own definition of done, and it was the one
#      guard a release could skip. The check reads the harness's OUTPUT, not just its exit
#      code: this machine's execution policy refuses an unblessed .ps1 and that refusal also
#      exits non-zero, which is how a 20-mutation suite once scored 20/20 without compiling a
#      line (docs\BACKLOG.md task 8). A run that never STARTED must fail loudly.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# --- the harness must be green ------------------------------------------------------
# -ExecutionPolicy Bypass is not optional: without it this machine refuses the file, and the
# refusal exits non-zero exactly like a real failure would.
Write-Host "Running the test harness..." -ForegroundColor Cyan
# ErrorActionPreference is dropped to Continue for exactly this call. In PS 5.1 the 2>&1 on a
# NATIVE command wraps every stderr line in a NativeCommandError record, and under "Stop" that
# record is terminating - so one harmless warning on stderr would abort packaging with a
# RemoteException while the harness was GREEN. Measured 2026-09-19.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$testOut = & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\tools\run-tests.ps1" 2>&1 | Out-String
$testCode = $LASTEXITCODE
$ErrorActionPreference = $prevEap
Write-Host $testOut

# Prove the harness actually RAN before trusting either verdict. A refusal, a missing dotnet or
# a moved file all produce "non-zero and no tests", which must never read as "tests failed and
# I know why" - and must never read as green either.
if ($testOut -notmatch 'passed,') {
    Write-Host "THE HARNESS DID NOT RUN - refusing to package." -ForegroundColor Red
    Write-Host "  Nothing above reports a pass count, so its exit code means nothing." -ForegroundColor Red
    exit 1
}
# The failure SHAPES are taken from the tooling that prints them, not guessed: a failing
# assertion is "   FAIL  <what>" (indented, from tests\CoreTests\Program.cs) and the banner is
# "TESTS FAILED" (from run-tests.ps1) - so an anchor at column zero matches neither, and -match
# would be case-insensitive enough to trip on the word "failed." in a GREEN summary line. Both
# are -cmatch, both were run red on purpose.
if ($testCode -ne 0 -or
    $testOut -cmatch '(?m)^\s*FAIL\b' -or
    $testOut -cmatch 'TESTS FAILED' -or
    $testOut -notmatch 'All harnesses passed') {
    Write-Host "TESTS ARE RED - refusing to package." -ForegroundColor Red
    exit 1
}

# --- the three versions must agree -------------------------------------------------
$pluginVer   = (Select-String -Path "$root\Undertow\Plugin.cs" -Pattern 'PluginVersion\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
$csprojVer   = (Select-String -Path "$root\Undertow\Undertow.csproj" -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
$manifestVer = (Get-Content "$root\manifest.json" -Raw | ConvertFrom-Json).version_number

if (($pluginVer -ne $csprojVer) -or ($pluginVer -ne $manifestVer)) {
    Write-Host "VERSION MISMATCH - refusing to package:" -ForegroundColor Red
    Write-Host "  Plugin const : $pluginVer"
    Write-Host "  csproj       : $csprojVer"
    Write-Host "  manifest.json: $manifestVer"
    exit 1
}

# --- the store files must exist ----------------------------------------------------
$required = @("manifest.json", "README.md", "CHANGELOG.md", "icon.png")
$missing = @()
foreach ($f in $required) { if (-not (Test-Path (Join-Path $root $f))) { $missing += $f } }
if ($missing.Count -gt 0) {
    Write-Host "MISSING STORE FILES - refusing to package:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

# The store rejects an icon that is not exactly 256x256, and does it late and unhelpfully.
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Image]::FromFile("$root\icon.png")
try {
    if ($icon.Width -ne 256 -or $icon.Height -ne 256) {
        Write-Host "icon.png is $($icon.Width)x$($icon.Height) - the store requires exactly 256x256." -ForegroundColor Red
        exit 1
    }
} finally { $icon.Dispose() }

# --- clean Release build -----------------------------------------------------------
dotnet build "$root\Undertow\Undertow.csproj" -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

$dll = "$root\Undertow\bin\Release\Undertow.dll"
if (-not (Test-Path $dll)) { Write-Host "No Release DLL at $dll" -ForegroundColor Red; exit 1 }

# --- assemble the zip both stores expect -------------------------------------------
$dist = "$root\dist"
$stage = "$dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item "$root\manifest.json", "$root\README.md", "$root\CHANGELOG.md", "$root\icon.png" -Destination $stage
New-Item -ItemType Directory -Force -Path "$stage\plugins" | Out-Null
Copy-Item $dll -Destination "$stage\plugins"

$zip = "$dist\RavenIron-Undertow-$pluginVer.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $_.FullName, $rel,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force

Write-Host "Packaged: $zip" -ForegroundColor Green
Get-Item $zip | Select-Object Name, Length
