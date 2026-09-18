<#
Several unattended raids with the Alyx layer on every live bot, alternating with control raids on Tarkov's own
legs, each recorded to a fleet stream and reported with tools/fleet_report.py.

    .\tools\Run-FleetRaids.ps1 -Maps factory4_day,bigmap -Seconds 300 -Repeats 2 -BotAmount Medium
    .\tools\Run-FleetRaids.ps1 -Maps factory4_day -Seconds 240 -Repeats 1 -NoControl

Each raid restarts the client (memory), waits for the free-RAM floor, runs `fleet:<seconds>:<maxBots>[:stock]`,
and writes the report next to the run. The manifest lists every raid with its stream path and termination.
#>
param(
    [string[]]$Maps = @('factory4_day'),
    [int]$Seconds = 300,
    [int]$Repeats = 1,
    [int]$MaxBots = 16,
    [ValidateSet('Keep', 'AsOnline', 'NoBots', 'Low', 'Medium', 'High', 'Horde')][string]$BotAmount = 'Medium',
    [switch]$NoControl,
    [string]$PoseClip = 'startstop',
    [string]$GameRoot = 'D:\SPT41AStar'
)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
. .\tools\SptAiBridge.ps1
Connect-SptAiBridge -GameRoot $GameRoot
$stamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
$outDir = Join-Path $root "artifacts\fleet\$stamp"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$manifest = @()
$log = Join-Path $outDir 'runner.log'
function Log([string]$text) { $line = '[' + [DateTime]::Now.ToString('HH:mm:ss') + '] ' + $text; Write-Output $line; Add-Content -LiteralPath $log -Value $line }

function Wait-Menu {
    $deadline = [DateTime]::UtcNow.AddMinutes(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        $menu = $false
        try { $menu = (Invoke-SptBridge 'status').mainMenuReady } catch { }
        $free = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1)
        if ($menu -and $free -ge 10.3) { return $true }
        Start-Sleep -Seconds 15
    }
    return $false
}

$plan = @()
for ($r = 0; $r -lt $Repeats; $r++) {
    foreach ($map in $Maps) {
        $plan += @{ Map = $map; Control = $false }
        if (!$NoControl) { $plan += @{ Map = $map; Control = $true } }
    }
}
$index = 0
foreach ($raid in $plan) {
    $index++
    $scenario = "fleet:$Seconds`:$MaxBots" + $(if ($raid.Control) { ':stock' } else { '' })
    Log "== raid $index/$($plan.Count): $($raid.Map) $scenario bots $BotAmount"
    Get-Process -Name EscapeFromTarkov -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 8
    $entry = @{ Index = $index; Map = $raid.Map; Control = $raid.Control; Scenario = $scenario; Started = [DateTime]::UtcNow.ToString('o') }
    try {
        $o = .\tools\Run-PuppetTest.ps1 -Launch -Daytime -LocationId $raid.Map -BotAmount $BotAmount -Scenario $scenario -PoseClip $PoseClip -BodyLean -AccelerationLimit 1 -PuppetTimeoutSeconds ([Math]::Min(900, $Seconds + 240)) *>&1
        $txt = $o | Out-String
        $entry.Output = ($o | Select-String 'Ended|Capture:|Error|did not start|exited|Start:|Fleet|Summary' | ForEach-Object { $_.Line.Substring(0, [Math]::Min(200, $_.Line.Length)) })
        $stream = ($txt | Select-String 'Capture: (.*\.jsonl)' | Select-Object -Last 1)
        if ($stream) { $entry.Stream = $stream.Matches[0].Groups[1].Value.Trim() }
        $summary = ($txt | Select-String 'Summary: (.*summary\.json)' | Select-Object -Last 1)
        if ($summary) { $entry.Report = $summary.Matches[0].Groups[1].Value.Trim() }
        if ($txt -match 'Game exited before reaching|Raid entry blocked') {
            Log 'launch report says the game exited; waiting for the menu and retrying once'
            if (Wait-Menu) {
                $o = .\tools\Run-PuppetTest.ps1 -Daytime -LocationId $raid.Map -BotAmount $BotAmount -Scenario $scenario -PoseClip $PoseClip -BodyLean -AccelerationLimit 1 -PuppetTimeoutSeconds ([Math]::Min(900, $Seconds + 240)) *>&1
                $txt = $o | Out-String
                $stream = ($txt | Select-String 'Capture: (.*\.jsonl)' | Select-Object -Last 1)
                if ($stream) { $entry.Stream = $stream.Matches[0].Groups[1].Value.Trim() }
                $summary = ($txt | Select-String 'Summary: (.*summary\.json)' | Select-Object -Last 1)
                if ($summary) { $entry.Report = $summary.Matches[0].Groups[1].Value.Trim() }
            }
        }
    } catch {
        $entry.Error = $_.Exception.Message.Substring(0, [Math]::Min(400, $_.Exception.Message.Length))
        Log "raid failed: $($entry.Error)"
    }
    $entry.Finished = [DateTime]::UtcNow.ToString('o')
    $manifest += $entry
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outDir 'manifest.json')
    Log "raid $index done: stream $($entry.Stream) report $($entry.Report)"
    Start-Sleep -Seconds 5
}
Log "all raids done; manifest at $outDir\manifest.json"
