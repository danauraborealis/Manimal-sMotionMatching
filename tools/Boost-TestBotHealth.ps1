[CmdletBinding()]
param([string]$GameRoot = 'D:\SPT41AStar', [ValidateRange(100, 100000)][single]$HealthPerPart = 10000)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SptAiBridge.ps1')
Connect-SptAiBridge -GameRoot $GameRoot
$type = Invoke-SptBridge 'type' @{ name = 'Manimal.MotionMatching.Plugin, Manimal.MotionMatching' }
$plugin = Read-SptMember $type.handle 'Instance'
$bot = Read-SptMember $plugin.handle 'Selected'
if (!$bot.handle) { throw 'Select a live test bot first.' }
if (!(Read-SptMember $bot.handle 'IsAI').value) { throw 'Selected player is not an AI bot.' }
$health = Read-SptMember $bot.handle 'HealthController'
if (!(Read-SptMember $health.handle 'IsAlive').value) { throw 'Selected bot is dead.' }
$state = Read-SptMember $health.handle 'BodyState'
foreach ($part in @('Head', 'Chest', 'Stomach', 'LeftArm', 'RightArm', 'LeftLeg', 'RightLeg')) {
    $body = Invoke-SptMember $state.handle 'get_Item' @($part)
    $value = Read-SptMember $body.handle 'Health'
    # Native initializer updates the live value's current/min/max together. No damage multiplier changes.
    Invoke-SptMember $value.handle 'Init' @($HealthPerPart, [single]0, $HealthPerPart) | Out-Null
    $maximum = (Read-SptMember $value.handle 'Maximum').value
    $current = (Read-SptMember $value.handle 'Current').value
    if ($maximum -ne $HealthPerPart -or $current -ne $HealthPerPart) { throw "Health verification failed for $part" }
    Write-Output "$part health: $current / $maximum"
}
