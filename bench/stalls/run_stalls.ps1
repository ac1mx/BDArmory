# Spawn/removal stall benchmark: fair duel rounds cut short every -RoundSeconds, so both craft are removed and respawned over and
# over, with BDAPerf's sampler recording every stall frame. Summarise the run folder with stall_report.py.
#   run_stalls.ps1 -Tag s1 -OutRoot D:\out [-Folder AIDogfight] [-Duration 600] [-RoundSeconds 40]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$OutRoot,
    [string]$Folder = "AIDogfight",
    [int]$Duration = 600,
    [int]$RoundSeconds = 40,
    [string]$Dll = "$PSScriptRoot\..\..\BDArmory\bin\Release\BDArmory.dll"
)
& "$PSScriptRoot\..\fight\run_fight.ps1" -Tag $Tag -OutRoot $OutRoot -Folder $Folder -Duration $Duration -RoundSeconds $RoundSeconds -SpawnDistance 8000 -SpawnAltitude 6000 -Sample -Dll $Dll
python "$PSScriptRoot\stall_report.py" (Join-Path $OutRoot $Tag)
