[CmdletBinding()]
param(
    [string]$GameRoot = 'D:\SPT41AStar',
    [string]$ProfileId = '6aa4ffc07fe49a692cdc90d5'
)
# leaves any running raid normally, then stops the client so a rebuilt DLL can be deployed
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SptAiBridge.ps1')
Connect-SptAiBridge -GameRoot $GameRoot
$status = (Invoke-RestMethod "$script:BridgeApi/status" -Headers $script:BridgeHeaders).Data
if (!($status.processes | Where-Object target -eq 'client').running) { Write-Output 'Client is not running.'; return }
if ($status.client.ready) {
    $singleton = Invoke-SptBridge 'type' @{ name = 'Comfort.Common.Singleton`1[[EFT.AbstractGame, Assembly-CSharp]], Comfort' }
    $game = Read-SptMember $singleton.handle 'Instance'
    if ($game.handle -and (Read-SptMember $game.handle 'Status').value -eq 'Started') {
        Invoke-SptBridge 'invoke' @{ handle = $game.handle; member = 'EFT.LocalGame::Stop(System.String,EFT.ExitStatus,System.String,System.Single)'; arguments = @($ProfileId, 'Left', '', 0) } | Out-Null
        Write-Output 'Requested raid exit'
        Start-Sleep -Seconds 12
    }
}
$op = Invoke-SptCommand 'stop' 'client' @{ force = $false }
Write-Output "Client stop: $((Wait-SptOperation $op.operationId 120).state)"
