# Starts a harness run (run_fight.ps1 / run_range.ps1) in its own hidden PowerShell, so that it finishes and restores the game files
# whatever happens to the shell that started it. Output: <OutRoot>\<Tag>\harness.log; the harness's process id: harness.pid.
#   run_detached.ps1 -Script fight\run_fight.ps1 -Tag t1 -OutRoot D:\out -Arguments '-Duration 900 -Folder AIBFM'
param(
    [Parameter(Mandatory = $true)][string]$Script,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$OutRoot,
    [string]$Arguments = ""
)
$out = Join-Path $OutRoot $Tag
New-Item -ItemType Directory -Force $out | Out-Null
$line = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot $Script)`" -Tag $Tag -OutRoot `"$OutRoot`" $Arguments"
$p = Start-Process pwsh -ArgumentList $line -WindowStyle Hidden -PassThru -RedirectStandardOutput "$out\harness.log" -RedirectStandardError "$out\harness.err"
Set-Content "$out\harness.pid" $p.Id
"harness $($p.Id) started; log: $out\harness.log"
