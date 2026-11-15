# Unattended missile range run: deploys the built fork DLL, writes GameData/BDArmory/PluginData/missile_range.cfg, lets BDAPerf's
# benchmark driver load the save and spawn AutoSpawn/<Folder>, waits for KSP to quit after -Duration seconds and restores every file
# it touched. Results: the MissileShot / MissileRangeTrial lines in <OutRoot>\<Tag>\KSP.log (analyse with range_report.py).
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$OutRoot,
    [int]$Duration = 3600,
    [string]$Folder = "AIRange",
    [string]$Missiles = "AIM-120D,AIM9X,RVV-AE-PD,METEOR,MICA.IR,bahaAim9,AA11Archer,AA13R37Arrow,bahaAim120", # The air-to-air missiles on the save's craft.
    [int]$Pairs = 4,
    [int]$Trials = 0,
    [int]$Seed = 1,
    [double]$StraightFraction = 0.2,
    [string]$RangeOptions = "",
    [int]$RepeatGeometry = 1,       # Fly each launch geometry this many trials in a row (one per missile variant, or repeats of one missile).
    [string]$PatchFile = "",        # ModuleManager patch (missile variants under test) placed in GameData for this run only.
    [int]$StopAfterShots = 0,       # Stop once this many shot results are logged instead of flying out -Duration.
    [string]$Expect = "",           # Log patterns, separated by ";", that must each have a match once the first ten shots are in; the run is stopped if one hasn't.
    [string]$Ksp = "D:\BDArmory KSP Save\KerbalSpaceProgram\English",
    [string]$Dll = "$PSScriptRoot\..\..\BDArmory\bin\Release\BDArmory.dll"
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\..\harness.ps1"
$out = Join-Path $OutRoot $Tag
if (Get-Process KSP_x64 -ErrorAction SilentlyContinue) { "KSP is running; not starting"; exit 1 }
if (-not (Restore-Pending $Ksp)) { "an interrupted run's files could not be restored; not starting"; exit 1 }
New-Item -ItemType Directory -Force $out | Out-Null
$perf = "$Ksp\GameData\BDAPerf\PluginData\BDAPerf.cfg"
$rangeCfg = "$Ksp\GameData\BDArmory\PluginData\missile_range.cfg"
Backup-GameFiles $Ksp $out
Copy-Item $Dll "$Ksp\GameData\BDArmory\Plugins\BDArmory.dll" -Force

$t = [IO.File]::ReadAllText($perf)
$t = $t -replace '(?m)^(\s*benchmark\s*=\s*)\w+', '${1}true'
$t = $t -replace '(?m)^(\s*spawnFolder\s*=\s*)\S+', "`${1}$Folder"
$t = $t -replace '(?m)^(\s*minLoadedVessels\s*=\s*)\d+', "`${1}$(2 * $Pairs)"
$t = $t -replace '(?m)^(\s*durationSeconds\s*=\s*)\d+', "`${1}$Duration"
[IO.File]::WriteAllText($perf, $t)

# Spawn over the sea east of the space centre, already flying, and start the competition straight away.
Set-HarnessGameSettings $Ksp @{
    VESSEL_SPAWN_DISTANCE = "4000"; VESSEL_SPAWN_GEOCOORDS = "(1, -63)"; VESSEL_SPAWN_WORLDINDEX = "1"; VESSEL_SPAWN_ALTITUDE = "6000"
    VESSEL_SPAWN_DISTANCE_TOGGLE = "True"; VESSEL_SPAWN_INITIAL_VELOCITY = "True"; COMPETITION_START_NOW_AFTER = "1"; COMPETITION_DISTANCE = "8000"
    INFINITE_COUNTERMEASURES = "True" # The same craft fly every trial; without this the targets run dry and later trials are undefended.
}

$cfg = @"
MISSILE_RANGE
{
    missiles = $Missiles
    pairs = $Pairs
    trials = $Trials
    seed = $Seed
    straightFraction = $StraightFraction
    repeatGeometry = $RepeatGeometry
    $($RangeOptions -replace ';', "`n    ")
}
"@
$variantPatch = "$Ksp\GameData\Custom\Patches\zzRangeVariants.cfg"
Add-HarnessCleanup @($rangeCfg, $variantPatch)
[IO.File]::WriteAllText($rangeCfg, $cfg)
if ($PatchFile) { Copy-Item $PatchFile $variantPatch -Force }
try {
    $p = Start-Process "$Ksp\KSP_x64.exe" -WorkingDirectory $Ksp -PassThru
    Wait-HarnessRun $p $Ksp $Duration $StopAfterShots @() @($Expect -split ';' | Where-Object { $_ })
}
finally {
    $exitWait = (Get-Date).AddSeconds(60)
    while ((Get-Process KSP_x64 -ErrorAction SilentlyContinue) -and (Get-Date) -lt $exitWait) { Start-Sleep 2 }
    if (-not (Get-Process KSP_x64 -ErrorAction SilentlyContinue)) {
        Restore-GameFiles $out
        $atmosphere = "$Ksp\GameData\BDArmory\PluginData\range_atmosphere.txt"
        if (Test-Path $atmosphere) { Move-Item $atmosphere "$out\atmosphere.txt" -Force }
        Copy-Item "$Ksp\KSP.log" "$out\KSP.log" -Force
        "restored; log copied to $out\KSP.log"
    } else { "KSP still running: NOT restored" }
}
