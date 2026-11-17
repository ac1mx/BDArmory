# Shared by the unattended test runs (dot-source it): back up the game files a run changes, apply the test settings, restore them.
# Test settings: lethal damage (well above both the save's and BDArmory's defaults, so that hits end fights), and TUFX's empty
# profile and no volumetric clouds in flight (through KSPTweaks at run time, so no ModuleManager config changes).
#
# A run that is stopped before it restores (its shell killed) leaves <Ksp>\harness_pending.json, which names its backup folder and
# everything it put in the game folder. The next run restores from it before touching anything, so a backup is never taken of files
# an earlier run left changed.

function Get-PendingPath([string]$Ksp) { "$Ksp\harness_pending.json" }

function Save-Pending {
    $state = @{ out = $script:HarnessOut; files = $script:HarnessFiles; remove = @($script:HarnessRemove) }
    [IO.File]::WriteAllText((Get-PendingPath $script:HarnessKsp), ($state | ConvertTo-Json -Depth 4))
}

# Restores what an interrupted run left behind. Returns $false while KSP is still running (nothing can be restored yet).
function Restore-Pending([string]$Ksp) {
    $pending = Get-PendingPath $Ksp
    if (-not (Test-Path $pending)) { return $true }
    if (Get-Process KSP_x64 -ErrorAction SilentlyContinue) { return $false }
    $state = Get-Content $pending -Raw | ConvertFrom-Json
    foreach ($f in $state.files.PSObject.Properties) {
        if (Test-Path "$($state.out)\$($f.Name).before") { Copy-Item "$($state.out)\$($f.Name).before" $f.Value -Force }
    }
    foreach ($r in $state.remove) { Remove-Item $r -Force -ErrorAction SilentlyContinue }
    Remove-Item $pending -Force
    Write-Host "restored the files an interrupted run left changed (backup: $($state.out))"
    return $true
}

function Backup-GameFiles([string]$Ksp, [string]$Out) {
    $script:HarnessKsp = $Ksp; $script:HarnessOut = $Out; $script:HarnessRemove = @()
    $script:HarnessFiles = [ordered]@{
        "persistent.sfs"   = "$Ksp\saves\Modern Warfare 1\persistent.sfs"
        "BDAPerf.cfg"      = "$Ksp\GameData\BDAPerf\PluginData\BDAPerf.cfg"
        "bda_settings.cfg" = "$Ksp\GameData\BDArmory\PluginData\settings.cfg"
        "KSPTweaks.cfg"    = "$Ksp\GameData\KSPTweaks\PluginData\KSPTweaks.cfg"
        "TUFX.cfg"         = "$Ksp\GameData\TUFX\TUFX.cfg"
        "BDArmory.dll"     = "$Ksp\GameData\BDArmory\Plugins\BDArmory.dll"
        "shot_model.cfg"   = "$Ksp\GameData\BDArmory\PluginData\shot_model.cfg"
        "pre_settings.cfg" = "$Ksp\GameData\PhysicsRangeExtender\settings.cfg" # Spawning raises the PRE range for wide spawns.
    }
    foreach ($name in $script:HarnessFiles.Keys) {
        if (Test-Path $script:HarnessFiles[$name]) { Copy-Item $script:HarnessFiles[$name] "$Out\$name.before" -Force }
        else { Remove-Item "$Out\$name.before" -Force -ErrorAction SilentlyContinue; $script:HarnessRemove += $script:HarnessFiles[$name] }
    }
    Save-Pending
}

# Files the run creates in the game folder, to be removed when it restores.
function Add-HarnessCleanup([string[]]$Paths) {
    $script:HarnessRemove += $Paths
    Save-Pending
}

function Set-CfgValue([string]$Text, [string]$Key, [string]$Value) {
    if ($Text -match "(?m)^\s*$Key\s*=") { return $Text -replace "(?m)^(\s*$Key\s*=\s*).*?(\r?)$", "`${1}$Value`${2}" }
    return $Text -replace "(?m)^(\s*)(DMG_MULTIPLIER\s*=.*)$", "`${1}`${2}`n`${1}$Key = $Value"
}

function Set-HarnessGameSettings([string]$Ksp, [hashtable]$Bda) {
    $bdaPath = "$Ksp\GameData\BDArmory\PluginData\settings.cfg"
    $b = [IO.File]::ReadAllText($bdaPath)
    # The save's own missile multiplier (3x BDArmory's) and 2x its gun default. 40 was needed only while the proximity fuze fired at the
    # edge of its search sphere, 20-40 m out; with the fuze firing at the fragment belt (sec1), 40 made almost every hit a kill.
    # Battle damage turned up so that hits cost performance (thrust, lift, control surfaces, the pilot), not just hit points.
    $damage = @{ EXP_DMG_MOD_MISSILE = "20"; EXP_DMG_MOD_BALLISTIC_NEW = "1.1"; EXP_DMG_MOD_HEAT = "1"; BALLISTIC_DMG_FACTOR = "3"
        BD_DAMAGE_CHANCE = "20"; BD_PROP_DAM_RATE = "2"; BD_PROP_FLAMEOUT = "50"; BD_PROP_FLOOR = "10"; BD_LIFT_LOSS_RATE = "3"; BD_FIRE_DAMAGE = "10"; BD_TANK_LEAK_RATE = "2" }
    foreach ($k in $damage.Keys) { $b = Set-CfgValue $b $k $damage[$k] }
    foreach ($k in $Bda.Keys) { $b = Set-CfgValue $b $k $Bda[$k] }
    [IO.File]::WriteAllText($bdaPath, $b)
    $tweaks = "$Ksp\GameData\KSPTweaks\PluginData\KSPTweaks.cfg"
    if (Test-Path $tweaks) {
        $t = [IO.File]::ReadAllText($tweaks)
        if ($t -match '(?m)^\s*flightTufxProfile\s*=') { $t = $t -replace '(?m)^(\s*flightTufxProfile\s*=\s*).*?(\r?)$', '${1}Default-Empty${2}' }
        else { $t = $t -replace '(?m)^(\s*)(graphicsModes\s*=.*)$', "`${1}`${2}`n`${1}flightTufxProfile = Default-Empty" }
        # No volumetric clouds, so the fights can be watched.
        if ($t -match '(?m)^\s*volumetricClouds\s*=') { $t = $t -replace '(?m)^(\s*volumetricClouds\s*=\s*).*?(\r?)$', '${1}false${2}' }
        else { $t = $t -replace '(?m)^(\s*)(graphicsModes\s*=.*)$', "`${1}`${2}`n`${1}volumetricClouds = false" }
        [IO.File]::WriteAllText($tweaks, $t)
    }
}

function Restore-GameFiles([string]$Out) {
    foreach ($name in $script:HarnessFiles.Keys) {
        if (Test-Path "$Out\$name.before") { Copy-Item "$Out\$name.before" $script:HarnessFiles[$name] -Force }
    }
    foreach ($r in $script:HarnessRemove) { Remove-Item $r -Force -ErrorAction SilentlyContinue }
    Remove-Item (Get-PendingPath $script:HarnessKsp) -Force -ErrorAction SilentlyContinue
}

# Waits for this run's KSP. Stops it early, by its own handle, when:
#  - the first $ValidateAfter shot results are in and a pattern in $Expect has no match in the log (the run is set up wrong), or
#  - every word in $ArmWords (or the log as a whole, without any) has $StopAfterShots shot results from craft named with it, or
#  - it is still running 90 s after the benchmark driver logged its quit (it can hang tearing down the flight scene).
function Wait-HarnessRun($Process, [string]$Ksp, [int]$Duration, [int]$StopAfterShots = 0, [string[]]$ArmWords = @(), [string[]]$Expect = @(), [int]$ValidateAfter = 10) {
    $log = "$Ksp\KSP.log"
    $deadline = (Get-Date).AddSeconds($Duration + 900); $fightEnd = (Get-Date).AddSeconds($Duration); $quitSeen = $null; $validated = $Expect.Count -eq 0
    while (-not $Process.HasExited -and (Get-Date) -lt $deadline) {
        if ($Process.WaitForExit(20000)) { break }
        if (($StopAfterShots -gt 0 -or -not $validated) -and (Test-Path $log)) {
            # Guided shots only: stores a craft drops (tanks) are logged as shots without guidance.
            $shots = @(Select-String -Path $log -Pattern 'MissileShot .* decoys=' | ForEach-Object { $_.Line } | Where-Object { $_ -notmatch 'guidance=None' })
            if (-not $validated -and $shots.Count -ge $ValidateAfter) {
                $missing = @($Expect | Where-Object { -not (Select-String -Path $log -Pattern $_ -Quiet) })
                if ($missing.Count) { "INVALID RUN, stopping it: no log line matches $($missing -join ' | ')"; $Process.Kill(); $Process.WaitForExit(30000) | Out-Null; break }
                $validated = $true; "valid after $($shots.Count) shots"
            }
            if ($StopAfterShots -gt 0) {
                $counts = if ($ArmWords.Count) { $ArmWords | ForEach-Object { $w = $_; @($shots | Where-Object { $_ -match "from=\S*$w" }).Count } } else { $shots.Count }
                if (($counts | Measure-Object -Minimum).Minimum -ge $StopAfterShots) { "shot count reached ($($counts -join ' / ')); stopping KSP"; $Process.Kill(); $Process.WaitForExit(30000) | Out-Null; break }
            }
        }
        if ((Get-Date) -lt $fightEnd) { continue }
        if (-not $quitSeen -and (Select-String -Path $log -Pattern 'Benchmark done, quitting' -SimpleMatch -Quiet)) { $quitSeen = Get-Date }
        if ($quitSeen -and ((Get-Date) - $quitSeen).TotalSeconds -gt 90) { "KSP hung after quitting; stopping it"; $Process.Kill(); $Process.WaitForExit(30000) | Out-Null; break }
    }
    if (-not $Process.HasExited) { "KSP did not exit by itself within the limit; left running" }
}
