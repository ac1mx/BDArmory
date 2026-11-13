# Shared by the unattended test runs (dot-source it): back up the game files a run changes, apply the test settings, restore them.
# Test settings: lethal damage (well above both the save's and BDArmory's defaults, so that hits end fights), and TUFX's empty
# profile and no volumetric clouds in flight (through KSPTweaks at run time, so no ModuleManager config changes).

function Backup-GameFiles([string]$Ksp, [string]$Out) {
    $script:HarnessFiles = [ordered]@{
        "persistent.sfs"   = "$Ksp\saves\Modern Warfare 1\persistent.sfs"
        "BDAPerf.cfg"      = "$Ksp\GameData\BDAPerf\PluginData\BDAPerf.cfg"
        "bda_settings.cfg" = "$Ksp\GameData\BDArmory\PluginData\settings.cfg"
        "KSPTweaks.cfg"    = "$Ksp\GameData\KSPTweaks\PluginData\KSPTweaks.cfg"
        "TUFX.cfg"         = "$Ksp\GameData\TUFX\TUFX.cfg"
        "BDArmory.dll"     = "$Ksp\GameData\BDArmory\Plugins\BDArmory.dll"
    }
    foreach ($name in $script:HarnessFiles.Keys) {
        if (Test-Path $script:HarnessFiles[$name]) { Copy-Item $script:HarnessFiles[$name] "$Out\$name.before" -Force }
    }
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
}
