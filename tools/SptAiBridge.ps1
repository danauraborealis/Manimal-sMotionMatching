# Dot-source for bridge access. Adapted from ManimalAStar/tools/SptAiBridge.ps1. Tokens are never logged.
function Connect-SptAiBridge {
    param([string]$GameRoot = 'D:\SPT41AStar')
    $port = 6971
    $settingsPath = Join-Path $GameRoot 'SPT_Runtime\user\spt-ai-bridge\bridge.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($null -ne $settings.Port) { $port = [int]$settings.Port }
    }
    if ($port -lt 1024 -or $port -gt 65535) { throw 'Invalid local bridge port' }
    $script:BridgeBase = 'http://127.0.0.1:' + $port
    $script:BridgeApi = $script:BridgeBase + '/api/v1'
    $script:BridgeHeaders = @{ Authorization = 'Bearer ' + [IO.File]::ReadAllText((Join-Path $GameRoot 'SPT_Runtime\user\spt-ai-bridge\auth.token')).Trim() }
    $script:BridgeMemberCache = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
}

function Invoke-SptCommand {
    param([string]$Command, [string]$Target, [hashtable]$Arguments = @{})
    $body = @{ command = $Command; args = $Arguments }
    if ($Target) { $body.target = $Target }
    $reply = Invoke-RestMethod "$script:BridgeApi/commands" -Headers $script:BridgeHeaders -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 8 -Compress)
    if (!$reply.Ok) { throw "$($reply.Code): $($reply.Error)" }
    return $reply.Data
}

function Wait-SptOperation {
    param([string]$OperationId, [int]$TimeoutSeconds = 300)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $reply = Invoke-RestMethod "$script:BridgeApi/operations/$OperationId" -Headers $script:BridgeHeaders
        if (!$reply.Ok) { throw $reply.Error }
        if ($reply.Data.state -notin @('queued', 'running')) { break }
        if ([DateTime]::UtcNow -gt $deadline) { throw "Operation still pending after $TimeoutSeconds s: $OperationId" }
        Start-Sleep -Milliseconds 500
    } while ($true)
    if ($reply.Data.state -ne 'completed' -or ($null -ne $reply.Data.result -and $reply.Data.result.Ok -eq $false)) {
        throw ($reply.Data | ConvertTo-Json -Depth 10 -Compress)
    }
    return $reply.Data
}

function Invoke-SptBridge {
    param([string]$Action, [hashtable]$Arguments = @{}, [string]$Target = 'client')
    $readOnly = $Action -in @('status', 'root', 'type', 'members', 'read', 'collection')
    for ($attempt = 0; $attempt -lt 11; $attempt++) {
        $body = @{ command = 'runtime'; target = $Target; args = @{ action = $Action; arguments = $Arguments; timeoutSeconds = 300 } }
        $reply = Invoke-RestMethod "$script:BridgeApi/commands" -Headers $script:BridgeHeaders -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 15 -Compress)
        if (!$reply.Ok) { throw "$($reply.Code): $($reply.Error)" }
        if ($null -ne $reply.Data -and $null -ne $reply.Data.PSObject.Properties['operationId']) {
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            while ($reply.Data.state -in @('queued', 'running')) {
                if ([DateTime]::UtcNow -gt $deadline) { throw "Operation still pending: $($reply.Data.operationId)" }
                Start-Sleep -Milliseconds 100
                $reply = Invoke-RestMethod "$script:BridgeApi/operations/$($reply.Data.operationId)" -Headers $script:BridgeHeaders
                if (!$reply.Ok) { throw $reply.Error }
            }
            if ($reply.Data.state -ne 'completed' -or !$reply.Data.result.Ok) {
                # reads are safe to retry; mutations are never replayed
                if ($readOnly -and $attempt -lt 10 -and $reply.Data.result.Code -in @('runtime_unavailable', 'backup_unavailable')) { Start-Sleep -Seconds 1; continue }
                throw ($reply | ConvertTo-Json -Depth 10 -Compress)
            }
            return $reply.Data.result.Data
        }
        return $reply.Data
    }
}

function Get-SptMember {
    param([string]$Handle, [string]$Name, [string]$Kind, [ValidateSet('client', 'server')][string]$Target = 'client')
    $key = "$Target`n$Handle`n$Name`n$Kind"
    if ($script:BridgeMemberCache.ContainsKey($key)) { return $script:BridgeMemberCache[$key] }
    $found = @()
    foreach ($member in @(Invoke-SptBridge 'members' @{ handle = $Handle; query = $Name; limit = 200 } -Target $Target)) {
        if ($member.name -ceq $Name -and (!$Kind -or $member.kind -eq $Kind)) { $found += $member }
    }
    if ($found.Count -ne 1) { throw "Expected one member '$Name', found $($found.Count). Signatures: $($found.signature -join '; ')" }
    $script:BridgeMemberCache[$key] = [string]$found[0].signature
    return $found[0].signature
}

function Read-SptMember {
    param([string]$Handle, [string]$Name, [ValidateSet('client', 'server')][string]$Target = 'client')
    return Invoke-SptBridge 'read' @{ handle = $Handle; member = (Get-SptMember $Handle $Name -Target $Target) } -Target $Target
}

function Write-SptMember {
    param([string]$Handle, [string]$Name, $Value, [ValidateSet('client', 'server')][string]$Target = 'client')
    return Invoke-SptBridge 'write' @{ handle = $Handle; member = (Get-SptMember $Handle $Name -Target $Target); value = $Value } -Target $Target
}

function Invoke-SptMember {
    param([string]$Handle, [string]$Name, [object[]]$Values = @(), [ValidateSet('client', 'server')][string]$Target = 'client')
    return Invoke-SptBridge 'invoke' @{ handle = $Handle; member = (Get-SptMember $Handle $Name 'Method' -Target $Target); arguments = $Values } -Target $Target
}
