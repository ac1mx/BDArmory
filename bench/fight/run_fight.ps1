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
    [string]$Ksp = "D:\BDArmory KSP Save\KerbalSpaceProgram\English",
    [string]$Dll = "$PSScriptRoot\..\..\BDArmory\bin\Release\BDArmory.dll"
)
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\..\harness.ps1"
$out = Join-Path $OutRoot $Tag
if (Get-Process KSP_x64 -ErrorAction SilentlyContinue) { "KSP is running; not starting"; exit 1 }
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
$flags = @("$pluginData\steering_metrics.on")
if (-not $NoFairRounds) { $flags += "$pluginData\duel_rounds.on" }
if ($BvrOnly) { $flags += "$pluginData\duel_bvr.on" } # Rounds end as draws once both craft have spent their air-to-air missiles.
if ($Teams) { $flags += "$pluginData\test_teams.on" }
if ($Patrol) { $flags += "$pluginData\test_patrol.on" }
foreach ($f in $flags) { Set-Content $f "" }
try {
    $p = Start-Process "$Ksp\KSP_x64.exe" -WorkingDirectory $Ksp -PassThru
    # Quit watchdog: KSP can hang tearing down the flight scene (missiles detonating as their parts are destroyed), so a KSP still
    # running 90 s after BDAPerf logged its quit is stopped. It is this run's own process, by handle.
    $deadline = (Get-Date).AddSeconds($Duration + 900); $quitSeen = $null; $fightEnd = (Get-Date).AddSeconds($Duration)
    while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
        if ($p.WaitForExit(15000)) { break }
        if ((Get-Date) -lt $fightEnd) { continue }
        if (-not $quitSeen -and (Select-String -Path "$Ksp\KSP.log" -Pattern 'Benchmark done, quitting' -SimpleMatch -Quiet)) { $quitSeen = Get-Date }
        if ($quitSeen -and ((Get-Date) - $quitSeen).TotalSeconds -gt 90) { "KSP hung after quitting; stopping it"; $p.Kill(); $p.WaitForExit(30000) | Out-Null; break }
    }
    if (-not $p.HasExited) { "KSP did not exit by itself within the limit; left running" }
}
finally {
    # A KSP stopped from outside (an obsolete run torn down) can still be exiting when its handle reports exit.
    $exitWait = (Get-Date).AddSeconds(60)
    while ((Get-Process KSP_x64 -ErrorAction SilentlyContinue) -and (Get-Date) -lt $exitWait) { Start-Sleep 2 }
    if (-not (Get-Process KSP_x64 -ErrorAction SilentlyContinue)) {
        Restore-GameFiles $out
        Remove-Item $flags -Force -ErrorAction SilentlyContinue
        # Flight recordings written by this run only (the recorder is on while steering_metrics.on exists).
        $recordings = Get-ChildItem "$Ksp\GameData\BDArmory\Logs" -Filter "flightdata-*.csv" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $p.StartTime }
        if ($FlightRecorder) { New-Item -ItemType Directory -Force "$out\flightdata" | Out-Null; $recordings | Move-Item -Destination "$out\flightdata\" -Force }
        else { $recordings | Remove-Item -Force }
        Copy-Item "$Ksp\KSP.log" "$out\KSP.log" -Force
        "restored; log copied to $out\KSP.log"
    } else { "KSP still running: NOT restored" }
}
