# Unattended fight run: deploys the built fork DLL, lets BDAPerf's benchmark driver load the save and continuously spawn
# AutoSpawn/<Folder>, and restores every file it touched once KSP quits after -Duration seconds. With fair rounds (default) the
# survivor of a duel is removed when its opponent dies so both respawn together; KSP.log gets "DuelRound won/lost by" lines, the
# pilots' SteeringMetrics lines and MissileShot lines (score with fight_report.py).
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$OutRoot,
    [int]$Duration = 900,
    [string]$Folder = "AIBFM",
    [int]$SpawnDistance = 8000,
    [string]$SpawnCoords = "(-0.1, -74.2)",
    [int]$SpawnAltitude = 5000,
    [int]$MinVessels = 2,
    [switch]$NoFairRounds,
    [switch]$BvrOnly,
    [switch]$Teams,
    [switch]$Patrol, # Craft with nothing to fight patrol random points near the spawn point (use with a large -SpawnDistance).
    [switch]$FlightRecorder,
    [string]$PatchFile = "",  # ModuleManager patch placed in GameData for this run only.
    [string]$ShotModel = "",  # Shot model weights (fit_shot_model.py --out) flown for this run only in place of the installed ones.
    [string]$SmartCraft = "", # Missiles launched by craft whose name contains this word fly as smart seekers, all others as stock ones.
    [string]$AdvancedCraft = "", # Only missiles launched by craft whose name contains this word use their homing law, motor mode, midcourse and salvo behaviour.
    [string]$AwarenessCraft = "", # Only pilots of craft whose name contains this word use the warning-receiver picture, no-escape shot, lofted launch, double notch, drag and masking.
    [int]$StopAfterShots = 0, # Stop once this many shot results are logged (per word in -ArmWords, when given) instead of flying out -Duration.
    [string]$ArmWords = "",     # Craft-name words that tell the arms of a comparison apart, comma-separated (e.g. "Red,Blue").
    [string]$Expect = "",       # Log patterns, separated by ";", that must each have a match once the first ten shots are in; the run is stopped if one hasn't.
    [string]$BdaSettings = "",  # Extra BDArmory settings for the run, "KEY=value;KEY=value" (e.g. DEBUG_SPAWNING=True).
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
$pluginData = "$Ksp\GameData\BDArmory\PluginData"
Backup-GameFiles $Ksp $out
Copy-Item $Dll "$Ksp\GameData\BDArmory\Plugins\BDArmory.dll" -Force

$t = [IO.File]::ReadAllText($perf)
$t = $t -replace '(?m)^(\s*benchmark\s*=\s*)\w+', '${1}true'
$t = $t -replace '(?m)^(\s*spawnFolder\s*=\s*)\S+', "`${1}$Folder"
$t = $t -replace '(?m)^(\s*minLoadedVessels\s*=\s*)\d+', "`${1}$MinVessels"
$t = $t -replace '(?m)^(\s*durationSeconds\s*=\s*)\d+', "`${1}$Duration"
[IO.File]::WriteAllText($perf, $t)

# Spawn apart and already flying over the sea off the space centre; start the competition 10 s after it forms.
Set-HarnessGameSettings $Ksp @{
    VESSEL_SPAWN_DISTANCE = "$SpawnDistance"; VESSEL_SPAWN_GEOCOORDS = $SpawnCoords; VESSEL_SPAWN_WORLDINDEX = "1"; VESSEL_SPAWN_ALTITUDE = "$SpawnAltitude"
    VESSEL_SPAWN_DISTANCE_TOGGLE = "True"; VESSEL_SPAWN_INITIAL_VELOCITY = "True"; COMPETITION_START_NOW_AFTER = "1"; COMPETITION_DISTANCE = "$SpawnDistance"
    VIP_DEATH_ENDS_ROUND = "False"
}
if ($BdaSettings) { $extra = @{}; $BdaSettings -split ';' | Where-Object { $_ } | ForEach-Object { $k, $v = $_ -split '=', 2; $extra[$k.Trim()] = $v.Trim() }; Set-HarnessGameSettings $Ksp $extra }
$flags = @("$pluginData\steering_metrics.on")
if (-not $NoFairRounds) { $flags += "$pluginData\duel_rounds.on" }
if ($BvrOnly) { $flags += "$pluginData\duel_bvr.on" } # Rounds end as draws once both craft have spent their air-to-air missiles.
if ($Teams) { $flags += "$pluginData\test_teams.on" }
if ($Patrol) { $flags += "$pluginData\test_patrol.on" }
if ($SmartCraft) { $flags += "$pluginData\test_smart_seekers.on" }
if ($AdvancedCraft) { $flags += "$pluginData\test_advanced_missiles.on" }
if ($AwarenessCraft) { $flags += "$pluginData\test_pilot_awareness.on" }
$variantPatch = "$Ksp\GameData\Custom\Patches\zzFightVariants.cfg"
Add-HarnessCleanup ($flags + $variantPatch)
foreach ($f in $flags) { Set-Content $f "" }
if ($SmartCraft) { Set-Content "$pluginData\test_smart_seekers.on" $SmartCraft }
if ($AdvancedCraft) { Set-Content "$pluginData\test_advanced_missiles.on" $AdvancedCraft }
if ($AwarenessCraft) { Set-Content "$pluginData\test_pilot_awareness.on" $AwarenessCraft }
if ($PatchFile) { Copy-Item $PatchFile $variantPatch -Force }
if ($ShotModel) { Copy-Item $ShotModel "$pluginData\shot_model.cfg" -Force } # Backed up (or marked for removal) with the other game files.
try {
    $p = Start-Process "$Ksp\KSP_x64.exe" -WorkingDirectory $Ksp -PassThru
    Wait-HarnessRun $p $Ksp $Duration $StopAfterShots @($ArmWords -split ',' | Where-Object { $_ }) @($Expect -split ';' | Where-Object { $_ })
}
finally {
    # A KSP stopped from outside (an obsolete run torn down) can still be exiting when its handle reports exit.
    $exitWait = (Get-Date).AddSeconds(60)
    while ((Get-Process KSP_x64 -ErrorAction SilentlyContinue) -and (Get-Date) -lt $exitWait) { Start-Sleep 2 }
    if (-not (Get-Process KSP_x64 -ErrorAction SilentlyContinue)) {
        Restore-GameFiles $out
        # Flight recordings written by this run only (the recorder is on while steering_metrics.on exists).
        $recordings = Get-ChildItem "$Ksp\GameData\BDArmory\Logs" -Filter "flightdata-*.csv" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $p.StartTime }
        if ($FlightRecorder) { New-Item -ItemType Directory -Force "$out\flightdata" | Out-Null; $recordings | Move-Item -Destination "$out\flightdata\" -Force }
        else { $recordings | Remove-Item -Force }
        Copy-Item "$Ksp\KSP.log" "$out\KSP.log" -Force
        "restored; log copied to $out\KSP.log"
    } else { "KSP still running: NOT restored" }
}
