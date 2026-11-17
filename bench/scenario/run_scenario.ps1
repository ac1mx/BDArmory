# Unattended scenario run: deploys the built fork DLL, puts a scenario config (Testing/Scenario.cs) in PluginData, lets BDAPerf's
# benchmark driver load the save and open a flight, and the scenario spawn and run its flights. Stops KSP once the scenario logs
# "Scenario over" (or after -Duration s), restores every file it touched and prints the scenario timeline.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$OutRoot,
    [string]$Config = "$PSScriptRoot\dawn_sweep.cfg",
    [int]$Duration = 1800,
    [int]$UiCapture = 0, # Seconds between interface screenshots (moved to the run folder's ui); 0: none.
    [string]$PartPhoto = "", # Part names, comma-separated: close-ups of them on their craft every 30 s (ui_part_*.png in the run folder's ui).
    [switch]$FlightRecorder,
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
$t = $t -replace '(?m)^(\s*minLoadedVessels\s*=\s*)\d+', '${1}1'
$t = $t -replace '(?m)^(\s*durationSeconds\s*=\s*)\d+', "`${1}$($Duration + 120)" # The run script stops KSP at the scenario's end.
[IO.File]::WriteAllText($perf, $t)
Set-HarnessGameSettings $Ksp @{ VESSEL_SPAWN_INITIAL_VELOCITY = "True"; VIP_DEATH_ENDS_ROUND = "False" }

$flags = @("$pluginData\steering_metrics.on", "$pluginData\scenario.cfg")
if ($FlightRecorder) { $flags += "$pluginData\flight_recorder.on" }
if ($UiCapture -gt 0) { $flags += "$pluginData\ui_capture.on" }
if ($PartPhoto) { $flags += "$pluginData\part_photo.on" }
Add-HarnessCleanup $flags
if ($PartPhoto) { Set-Content "$pluginData\part_photo.on" $PartPhoto }
Set-Content "$pluginData\steering_metrics.on" ""
if ($FlightRecorder) { Set-Content "$pluginData\flight_recorder.on" "" }
if ($UiCapture -gt 0) { Set-Content "$pluginData\ui_capture.on" "$UiCapture" }
Copy-Item $Config "$pluginData\scenario.cfg" -Force
$log = "$Ksp\KSP.log"
try {
    $p = Start-Process "$Ksp\KSP_x64.exe" -WorkingDirectory $Ksp -PassThru
    $deadline = (Get-Date).AddSeconds($Duration + 600); $overSeen = $null
    while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
        if ($p.WaitForExit(15000)) { break }
        if (-not $overSeen -and (Test-Path $log) -and (Select-String -Path $log -Pattern '[BDArmory.Scenario]: T+' -SimpleMatch -Quiet) -and (Select-String -Path $log -Pattern 'Scenario over (' -SimpleMatch -Quiet)) { $overSeen = Get-Date; "scenario over; stopping KSP in 20 s" }
        if ($overSeen -and ((Get-Date) - $overSeen).TotalSeconds -gt 20) { $p.Kill(); $p.WaitForExit(30000) | Out-Null; break }
    }
    if (-not $p.HasExited) { "time limit; stopping KSP"; $p.Kill(); $p.WaitForExit(30000) | Out-Null }
}
finally {
    $exitWait = (Get-Date).AddSeconds(60)
    while ((Get-Process KSP_x64 -ErrorAction SilentlyContinue) -and (Get-Date) -lt $exitWait) { Start-Sleep 2 }
    if (-not (Get-Process KSP_x64 -ErrorAction SilentlyContinue)) {
        Restore-GameFiles $out
        $recordings = Get-ChildItem "$Ksp\GameData\BDArmory\Logs" -Filter "flightdata-*.csv" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $p.StartTime }
        if ($FlightRecorder) { New-Item -ItemType Directory -Force "$out\flightdata" | Out-Null; $recordings | Move-Item -Destination "$out\flightdata\" -Force }
        else { $recordings | Remove-Item -Force }
        $shots = Get-ChildItem "$Ksp\GameData\BDArmory\Logs" -Filter "ui_*.bmp" -ErrorAction SilentlyContinue
        if ($shots) {
            New-Item -ItemType Directory -Force "$out\ui" | Out-Null
            Add-Type -AssemblyName System.Drawing
            foreach ($s in $shots) {
                $img = [System.Drawing.Image]::FromFile($s.FullName)
                $img.Save("$out\ui\$($s.BaseName).png", [System.Drawing.Imaging.ImageFormat]::Png); $img.Dispose()
                Remove-Item $s.FullName -Force
            }
        }
        Copy-Item $log "$out\KSP.log" -Force
        "restored; log copied to $out\KSP.log"
        Select-String -Path "$out\KSP.log" -Pattern '\[BDArmory\.(Scenario|SurveillanceRadar)\]|\]: Orders ' | ForEach-Object { $_.Line -replace '^\[\w+ [\d:.]+\]: ', '' } | Set-Content "$out\timeline.txt"
        "timeline: $out\timeline.txt ($((Get-Content "$out\timeline.txt").Count) lines)"
    } else { "KSP still running: NOT restored" }
}
