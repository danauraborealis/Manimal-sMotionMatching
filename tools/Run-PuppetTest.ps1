[CmdletBinding()]
param(
    [string]$GameRoot = 'D:\SPT41AStar',
    [string]$ProfileId = '6aa4ffc07fe49a692cdc90d5',
    [string]$LocationId = 'factory4_day',
    # bypass tools/test-area.json and use the viewer's current position
    [switch]$UseCurrentArea,
    [switch]$OpenArea,
    # pick the raid time slot that is daytime (offline raids offer now or now+12h)
    [switch]$Daytime,
    # AI acceleration cap in Player.Speed units per second; 0 = EFT's own ramp (use for baselines); -1 leaves the config value
    [double]$AccelerationLimit = -1,
    [ValidateSet('Keep', 'On', 'Off')][string]$SprintInertia = 'Keep',
    [ValidateSet('Keep', 'On', 'Off')][string]$AnimatorPhaseLock = 'Keep',
    [ValidateSet('Keep', 'On', 'Off')][string]$LocomotionRefinement = 'Keep',
    [ValidateSet('Keep', 'AsOnline', 'NoBots', 'Low', 'Medium', 'High', 'Horde')][string]$BotAmount = 'Low',
    [ValidateSet('Keep', 'Enabled', 'Disabled')][string]$Bosses = 'Disabled',
    [string]$Scenario = 'sweep',
    [ValidateSet('off', 'on', 'ab')][string]$FootLock = 'off',
    # procedural torso weight shift
    [switch]$BodyLean,
    # clip name prefix from the local pose database; empty disables playback
    [string]$PoseClip = '',
    # run pose playback off then on in the same raid (FootLock applies to both runs)
    [switch]$PoseAb,
    [string]$PoseDatabase = '',
    [ValidateSet('Off', 'Shots', 'Synthetic')][string]$HitReactions = 'Off',
    [string]$ReactionDatabase = '',
    [switch]$ManualGallery,
    # screenshots taken back to back while this step runs, for visual review of legs
    [string]$BurstStep = 'move:0.3:7',
    [ValidateRange(0, 12)][int]$BurstCount = 3,
    # screenshots stall the game for seconds each (a 5-shot burst took 30 s), corrupting frame timing of the step
    # they land in; leave off for measurement runs
    [switch]$Screenshots,
    # enter the raid, write the nearest bot's skeleton json, and skip the puppet run
    [switch]$DumpSkeleton,
    [switch]$Launch,
    # Enter a normal raid, then return without selecting, moving, or driving any bot/player.
    [switch]$RaidOnly,
    [switch]$LeaveRaid,
    [ValidateRange(60, 900)][int]$PuppetTimeoutSeconds = 420,
    # raid admission floor from the ManimalAStar memory-exhaustion incident; not lowered here
    [double]$MinimumFreeGiBForRaid = 10
)
$ErrorActionPreference = 'Stop'
if ($ManualGallery) {
    $HitReactions = 'Shots'
    $Scenario = 'stop:10;' + (('move:0.625:8;stop:1;turn:180:120;stop:1;' * 20).TrimEnd(';'))
    if (!$PoseClip) { $PoseClip = 'startstop' }
}
$repoRoot = Split-Path $PSScriptRoot -Parent
$identityPath = Join-Path $repoRoot 'Directory.Build.props'
if (!(Test-Path -LiteralPath $identityPath -PathType Leaf)) { throw "Mod identity file not found: $identityPath" }
[xml]$identity = Get-Content -LiteralPath $identityPath -Raw
$identityGroup = @($identity.Project.PropertyGroup) | Select-Object -First 1
$modAuthor = [string]$identityGroup.ModAuthor
$modName = [string]$identityGroup.ModName
$modDisplayName = $modAuthor + '-' + $modName
$modAssemblyName = $modAuthor + '.' + $modName + '.dll'
if ([string]::IsNullOrWhiteSpace($modAuthor) -or [string]::IsNullOrWhiteSpace($modName)) { throw 'Directory.Build.props is missing the mod identity.' }
. (Join-Path $PSScriptRoot 'SptAiBridge.ps1')
Connect-SptAiBridge -GameRoot $GameRoot
$script:MotionMatchingTestApiHandle = $null

$runDirectory = Join-Path $repoRoot ('artifacts\puppet\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
function Log([string]$Text) { $line = '[' + [DateTime]::Now.ToString('HH:mm:ss') + '] ' + $Text; Write-Output $line; Add-Content -LiteralPath (Join-Path $runDirectory 'run.log') -Value $line }
function FreeGiB { [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1) }

function Get-ProcessState {
    $data = (Invoke-RestMethod "$script:BridgeApi/status" -Headers $script:BridgeHeaders).Data
    return @{
        Server = ($data.processes | Where-Object target -eq 'server').running
        Client = ($data.processes | Where-Object target -eq 'client').running
        ClientReady = $data.client.ready
        ServerReady = $data.server.ready
    }
}

function Invoke-Api([string]$Method, [object[]]$Values = @()) {
    # Resolve once per run. Each reflection round trip stalls the game's main thread;
    # repeating the type lookup at every status poll added avoidable capture hitches.
    if (!$script:MotionMatchingTestApiHandle) {
        $type = Invoke-SptBridge 'type' @{ name = 'Manimal.MotionMatching.MotionMatchingTestApi, Manimal.MotionMatching' }
        if (!$type.handle) { throw 'Manimal.MotionMatching test API is not loaded in the client.' }
        $script:MotionMatchingTestApiHandle = $type.handle
    }
    try { $reply = Invoke-SptMember $script:MotionMatchingTestApiHandle $Method $Values }
    catch { throw "Test API $Method ($($Values.Count) arguments): $($_.Exception.Message)" }
    if ($reply.truncated) { throw "Test API reply was truncated: $Method" }
    return $reply.value | ConvertFrom-Json
}

function Press-Button($Screen, [string]$Field) {
    if (!(Read-SptMember $Screen.handle 'isActiveAndEnabled').value) { throw "Screen is inactive: $($Screen.type)" }
    $button = Read-SptMember $Screen.handle $Field
    if (!(Invoke-SptBridge 'read' @{ handle = $button.handle; member = 'EFT.UI.DefaultUIButton::Interactable[]' }).value) { throw "Button is disabled: $Field" }
    $click = Read-SptMember $button.handle 'OnClick'
    Invoke-SptBridge 'invoke' @{ handle = $click.handle; member = 'UnityEngine.Events.UnityEvent::Invoke()'; arguments = @() } | Out-Null
}

function Get-GameStatus {
    $singleton = Invoke-SptBridge 'type' @{ name = 'Comfort.Common.Singleton`1[[EFT.AbstractGame, Assembly-CSharp]], Comfort' }
    $game = Read-SptMember $singleton.handle 'Instance'
    if (!$game.handle) { return $null }
    return @{ Handle = $game.handle; Status = (Read-SptMember $game.handle 'Status').value }
}

function Invoke-PlacementArtifacts([string]$Mode, [string]$CapturePath, [string]$ModeDirectory) {
    if ([string]::IsNullOrWhiteSpace($CapturePath) -or !(Test-Path -LiteralPath $CapturePath -PathType Leaf)) {
        throw "[$Mode] Capture path is missing or does not exist: $CapturePath"
    }
    $diagnostics = Join-Path $PSScriptRoot 'placement_diagnostics.py'
    $viewer = Join-Path $PSScriptRoot 'placement_viewer.py'
    $report = Join-Path $ModeDirectory 'placement-report.json'
    $replay = Join-Path $ModeDirectory 'placement-replay.html'

    # These tools read the capture once each, in sequence. Keep their stdout out of Invoke-PuppetRun's summary-path
    # pipeline while retaining a useful failure message if a capture lacks the geometry required by the viewer.
    $reportOutput = & python $diagnostics $CapturePath '--output' $report 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($reportOutput | Out-String).Trim()
        throw "[$Mode] Placement report failed (exit $LASTEXITCODE): $detail"
    }
    Log "[$Mode] Placement report: $report"

    $replayOutput = & python $viewer $CapturePath '--output' $replay 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($replayOutput | Out-String).Trim()
        throw "[$Mode] Placement replay failed (exit $LASTEXITCODE): $detail"
    }
    Log "[$Mode] Placement replay: $replay"
    $diagnosticNames = @('arm_sync_report', 'refinement_report')
    if ([IO.Path]::GetExtension($CapturePath) -eq '.json') { $diagnosticNames += 'jump_report' }
    foreach ($diagnostic in $diagnosticNames) {
        $destination = Join-Path $ModeDirectory ($diagnostic.Replace('_', '-') + '.json')
        $diagnosticOutput = & python (Join-Path $PSScriptRoot ($diagnostic + '.py')) $CapturePath '--output' $destination 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "[$Mode] $diagnostic failed: $(($diagnosticOutput | Out-String).Trim())"
        }
        Log "[$Mode] Diagnostic: $destination"
    }
}

# 1. server + client
$state = Get-ProcessState
if (!$state.Client) {
    if (!$Launch) { throw 'Client is not running. Re-run with -Launch to start server and client through the bridge.' }
    $built = Join-Path $repoRoot ('src\MotionMatching\bin\Release\netstandard2.1\' + $modAssemblyName)
    $installed = Join-Path $GameRoot ('BepInEx\plugins\' + $modDisplayName + '\' + $modAssemblyName)
    $legacyInstalled = Join-Path $GameRoot 'BepInEx\plugins\Manimal-MotionMatching\Manimal.MotionMatching.dll'
    if (Test-Path -LiteralPath $legacyInstalled -PathType Leaf) {
        throw "Legacy Manimal-MotionMatching is still installed at $legacyInstalled. Run tools\\Install-MotionMagic.ps1 before launching a puppet test."
    }
    if ($ReactionDatabase) {
        New-Item -ItemType Directory -Force -Path (Split-Path $installed) | Out-Null
        Copy-Item -LiteralPath $ReactionDatabase -Destination (Join-Path (Split-Path $installed) 'reaction_posedb.json') -Force
    }
    if ($PoseDatabase) {
        # local Valve-derived data: only ever copied into the local test install
        Copy-Item $PoseDatabase (Join-Path (Split-Path $installed) 'alyx_posedb.json') -Force
        Log "Deployed pose database from $PoseDatabase"
    }
    if (!(Test-Path $installed) -or (Get-FileHash $built).Hash -ne (Get-FileHash $installed).Hash) {
        New-Item -ItemType Directory -Force -Path (Split-Path $installed) | Out-Null
        Copy-Item $built $installed -Force
        Log "Deployed plugin to $installed"
    }
    if (!$state.Server) {
        Log 'Starting server'
        $op = Invoke-SptCommand 'start' 'server'
        Wait-SptOperation $op.operationId 240 | Out-Null
    }
    Log "Launching client (free RAM $(FreeGiB) GiB)"
    $op = Invoke-SptCommand 'launch' 'client' @{ profileId = $ProfileId }
    Wait-SptOperation $op.operationId 420 | Out-Null
    Log 'Client at main menu'
}

$status = Invoke-SptBridge 'status'
if ($status.profileId -ne $ProfileId) { throw "Client profile $($status.profileId) is not the expected test profile." }

# 2. raid
$game = Get-GameStatus
if (!$game -or $game.Status -ne 'Started') {
    if (!$status.mainMenuReady) { throw 'Client is neither at the main menu nor in a started raid.' }
    $free = FreeGiB
    if ($free -lt $MinimumFreeGiBForRaid) { throw "Raid entry blocked: $free GiB free is below the $MinimumFreeGiBForRaid GiB floor. Close memory-heavy apps first." }
    Log "Entering $LocationId with bot amount $BotAmount (free RAM $free GiB)"
    $app = Invoke-SptBridge 'root' @{ name = 'application' }
    $operation = Read-SptMember $app.handle '_menuOperation'
    Invoke-SptMember $operation.handle 'ShowScreen' @('Play', $true) | Out-Null
    $menu = Invoke-SptBridge 'root' @{ name = 'menu' }
    $side = Read-SptMember $menu.handle 'MatchMakerSideSelectionScreen'
    Invoke-SptMember $side.handle 'SetSelectedSide' @('Pmc') | Out-Null
    Press-Button $side '_nextButton'
    $raid = Read-SptMember $app.handle '_raidSettings'
    $locationSettings = Read-SptMember $raid.handle '_locationSettings'
    $locations = Read-SptMember $locationSettings.handle 'locations'
    $values = Read-SptMember $locations.handle 'Values'
    $location = $null
    foreach ($value in @(Invoke-SptBridge 'collection' @{ handle = $values.handle; limit = 100 })) {
        if ((Read-SptMember $value.handle 'Id').value -eq $LocationId) { $location = $value; break }
    }
    if (!$location) { throw "Location not found: $LocationId" }
    $selection = Read-SptMember $menu.handle 'MatchMakerSelectionLocationScreen'
    Invoke-SptMember $selection.handle 'OnLocationSelected' @(@{ '$handle' = $location.handle }) | Out-Null
    if ($LocationId -in @('factory4_day', 'factory4_night')) {
        # factory remaps day/night from the selected time, so pick the matching one
        Invoke-SptMember $selection.handle 'UpdateSelectedTime' @($(if ($LocationId -eq 'factory4_night') { 'PAST' } else { 'CURR' })) | Out-Null
    } elseif ($Daytime) {
        $hour = [DateTime]::Now.Hour
        $slot = if ($hour -ge 7 -and $hour -lt 18) { 'CURR' } else { 'PAST' }
        Invoke-SptMember $selection.handle 'UpdateSelectedTime' @($slot) | Out-Null
        Log "Selected raid time slot $slot for daytime (local hour $hour)"
    }
    $selected = Read-SptMember $selection.handle 'SelectedLocation'
    if ((Read-SptMember $selected.handle 'Id').value -ne $LocationId) { throw 'Native map selection did not match the requested location.' }
    Press-Button $selection '_acceptButton'
    $offline = Read-SptMember $menu.handle 'MatchmakerOfflineRaidScreen'
    $toggle = Read-SptMember $offline.handle '_offlineModeToggle'
    Write-SptMember $toggle.handle 'isOn' $true | Out-Null
    if ($BotAmount -ne 'Keep' -or $Bosses -ne 'Keep') {
        Invoke-SptMember $offline.handle 'ShowRaidSettingsWindow' | Out-Null
        $window = Read-SptMember $offline.handle '_raidSettingsWindow'
        if ($BotAmount -ne 'Keep') {
            $dropdown = Read-SptMember $window.handle '_aiAmountDropdown'
            $index = [Array]::IndexOf(@('AsOnline', 'NoBots', 'Low', 'Medium', 'High', 'Horde'), $BotAmount)
            Invoke-SptMember $dropdown.handle 'UpdateValue' @($index, $true, $null, $null) | Out-Null
            if ((Read-SptMember $dropdown.handle 'CurrentIndex').value -ne $index) { throw 'Bot amount dropdown did not accept the requested value.' }
        }
        if ($Bosses -ne 'Keep') {
            # same native toggle ManimalAStar's raid entry uses; verified through WavesSettings below
            $bossToggle = Read-SptMember $window.handle '_enableBosses'
            Invoke-SptMember $bossToggle.handle 'UpdateValue' @(($Bosses -eq 'Enabled'), $true, $null, $null) | Out-Null
            $offlineSettings = Read-SptMember $offline.handle '_offlineRaidSettings'
            $waves = Read-SptMember $offlineSettings.handle 'WavesSettings'
            if ((Read-SptMember $waves.handle 'IsBosses').value -ne ($Bosses -eq 'Enabled')) { throw 'Raid settings did not keep the requested boss setting.' }
        }
        Invoke-SptMember $window.handle 'Close' | Out-Null
    }
    Press-Button $offline '_nextButtonSpawner'
    Press-Button (Read-SptMember $menu.handle 'MatchmakerInsuranceScreen') '_nextButton'
    Press-Button (Read-SptMember $menu.handle 'MatchMakerAcceptScreen') '_acceptButton'

    $deadline = [DateTime]::UtcNow.AddSeconds(300)
    do {
        Start-Sleep -Seconds 3
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Raid did not reach Started state within 300 s.' }
        $game = try { Get-GameStatus } catch { $null }
    } while (!$game -or $game.Status -ne 'Started')
    Log 'Raid started'
}

if ($RaidOnly) {
    Log 'Normal raid ready; automatic player reporting owns the bots. No scripted test started.'
    return
}

# 3. wait for a bot to exist
$deadline = [DateTime]::UtcNow.AddSeconds(180)
do {
    $api = Invoke-Api 'Status'
    if ($api.InRaid -and $api.AliveBots -ge 1) { break }
    if ([DateTime]::UtcNow -gt $deadline) { throw "No active bot appeared within 180 s (InRaid=$($api.InRaid))." }
    Start-Sleep -Seconds 3
} while ($true)
Log "Active bots: $($api.AliveBots)"

if ($DumpSkeleton) {
    $dump = Invoke-Api 'DumpSkeleton'
    Log "DumpSkeleton: $($dump.Message)"
    if ($dump.Message -like '*.json') { Copy-Item $dump.Message (Join-Path $runDirectory 'skeleton.json') }
    return
}

# 4. puppet, once per mode; foot-lock "ab" or -PoseAb runs off then on in the same raid
function Invoke-PuppetRun([string]$Mode, [bool]$UseFootLock, [string]$Pose) {
    $modeDirectory = Join-Path $runDirectory $Mode
    New-Item -ItemType Directory -Force -Path $modeDirectory | Out-Null
    $api = Invoke-Api 'Status'
    # back-to-back runs found the previous capture still closing ("A capture is already running"); wait it out, then stop it
    $waited = 0
    while ($api.Capturing -and $waited -lt 20) { Start-Sleep -Seconds 2; $waited += 2; $api = Invoke-Api 'Status' }
    if ($api.Capturing) { Log "[$Mode] Stopping a lingering capture: $((Invoke-Api 'Stop').Message)"; Start-Sleep -Seconds 3; $api = Invoke-Api 'Status' }
    $previousCapture = $api.LastCapturePath
    $puppetScenario = $Scenario -notlike 'fleet*' -and $Scenario -notlike 'observe*'
    if ($puppetScenario) {
        $clearArea = Invoke-Api 'ClearPreferredTestArea'
        if (!$clearArea.Ok) { throw "Could not clear the preferred test area: $($clearArea.Message)" }
        if ($UseCurrentArea) {
            Log "[$Mode] Using the current viewer position; saved test area bypassed."
        } else {
            $testAreaPath = Join-Path $PSScriptRoot 'test-area.json'
            if (Test-Path -LiteralPath $testAreaPath -PathType Leaf) {
                try { $testArea = Get-Content -LiteralPath $testAreaPath -Raw | ConvertFrom-Json }
                catch { throw "Could not read saved test area '$testAreaPath': $($_.Exception.Message)" }
                if ($testArea.schema -ne 'manimal.motionmatching.test-area.v1') { throw "Unsupported saved test-area schema: $($testArea.schema)" }
                if ([string]::IsNullOrWhiteSpace([string]$testArea.locationId) -or $null -eq $testArea.position -or $null -eq $testArea.position.x -or $null -eq $testArea.position.y -or $null -eq $testArea.position.z -or $null -eq $testArea.rotation -or $null -eq $testArea.rotation.yaw) {
                    throw "Saved test area is missing its map id, position, or yaw: $testAreaPath"
                }
                if ($testArea.locationId -eq $LocationId) {
                    $setArea = Invoke-Api 'SetPreferredTestArea' @([string]$testArea.locationId, [single]$testArea.position.x, [single]$testArea.position.y, [single]$testArea.position.z, [single]$testArea.rotation.yaw)
                    if ($setArea.Ok) {
                        $areaPosition = '{0:F1}, {1:F1}, {2:F1}' -f $testArea.position.x, $testArea.position.y, $testArea.position.z
                        Log "[$Mode] Preferred test area applied: $($testArea.locationId) at ($areaPosition), yaw $('{0:F1}' -f $testArea.rotation.yaw)."
                    } elseif ($setArea.MapMismatch) {
                        Log "[$Mode] Saved test area skipped: expected map $($setArea.ExpectedLocationId), current map $($setArea.CurrentLocationId); using the current viewer position."
                    } else {
                        throw "Could not apply saved test area: $($setArea.Message)"
                    }
                } else {
                    Log "[$Mode] Saved test area is for $($testArea.locationId), not requested map $LocationId; using the current viewer position."
                }
            } else {
                Log "[$Mode] No saved test area found; using the current viewer position."
            }
        }
    }
    if ($OpenArea) {
        $open = Invoke-Api 'FindOpenTestArea' @([single]10)
        if (!$open.Ok) { throw "No suitable flat open test area: $($open.Message)" }
        Log "[$Mode] Open area: $($open | ConvertTo-Json -Compress -Depth 4)"
    }
    if ($AccelerationLimit -ge 0) { Log "[$Mode] AccelerationLimit: $((Invoke-Api 'SetAccelerationLimit' @([single]$AccelerationLimit)).Limit)" }
    if ($SprintInertia -ne 'Keep') { Log "[$Mode] SprintInertia: $((Invoke-Api 'SetSprintInertia' @($SprintInertia -eq 'On')).Enabled)" }
    if ($AnimatorPhaseLock -ne 'Keep') { Log "[$Mode] AnimatorPhaseLock: $((Invoke-Api 'SetAnimatorPhaseLock' @($AnimatorPhaseLock -eq 'On')).Enabled)" }
    if ($LocomotionRefinement -ne 'Keep') { Log "[$Mode] LocomotionRefinement: $((Invoke-Api 'SetLocomotionRefinement' @($LocomotionRefinement -eq 'On')).Enabled)" }
    $reaction = Invoke-Api 'SetHitReactions' @(($HitReactions -ne 'Off'), ($HitReactions -eq 'Synthetic'))
    if ($HitReactions -ne 'Off' -and !$reaction.Enabled) { throw "Reaction prototype unavailable: $($reaction.Note)" }
    Log "[$Mode] Hit reactions: $($reaction.Note)"
    # a capture from the previous run can still be flushing its json for tens of seconds; keep asking
    $attempts = 0
    do {
        if ($Scenario -like 'fleet*') {
            # fleet[:seconds[:maxBots]]: the clip set on every live bot, streamed to one JSONL file per raid
            $seconds = if ($Scenario -match ':(\d+)') { [single]$Matches[1] } else { 300 }
            $maxBots = if ($Scenario -match ':\d+:(\d+)') { [int]$Matches[1] } else { 12 }
            # fleet:seconds:maxBots:stock records Tarkov's own legs the same way (a control raid)
            $control = $Scenario -like '*:stock'
            $start = Invoke-Api 'StartFleet' @($seconds, $Pose, $maxBots, [bool]$control)
        } elseif ($Scenario -like 'observe*') {
            # observe[:seconds]: the nearest live bot under its own AI, with the clip set attached through the driver adapter
            $seconds = if ($Scenario -match ':(\d+)') { [single]$Matches[1] } else { 45 }
            $start = Invoke-Api 'StartObserve' @($seconds, $Pose, [bool]$BodyLean)
        } else {
            $start = Invoke-Api 'StartPuppet' @($Scenario, $true, $true, (!$ManualGallery), $UseFootLock, $Pose, [bool]$BodyLean)
        }
        if ($start.Ok -or $start.Message -notmatch 'still saving|already running') { break }
        $attempts++
        Start-Sleep -Seconds 3
    } while ($attempts -lt 30)
    Log "[$Mode] Start: $($start.Message) (bot role $((Invoke-Api 'Status').SelectedRole))"
    if (!$start.Ok) { throw "Run did not start: $($start.Message)" }
    if ($ManualGallery) {
        try {
            $healthRows = & (Join-Path $PSScriptRoot 'Boost-TestBotHealth.ps1') -GameRoot $GameRoot -HealthPerPart 10000
            foreach ($row in $healthRows) { Log "[$Mode] $row" }
        } catch {
            Invoke-Api 'Stop' | Out-Null
            throw
        }
    }
    if ($start.Lane) {
        $laneStart = $start.Lane.Start -join ','
        $laneEnd = $start.Lane.End -join ','
        Log "[$Mode] Selected lane: start=($laneStart), end=($laneEnd), yaw=$('{0:F1}' -f $start.Lane.Yaw), clear=$('{0:F1}' -f $start.Lane.ClearMeters) m"
    }

    $shotTaken = !$Screenshots
    $burstTaken = !$Screenshots -or $BurstCount -eq 0
    $started = [DateTime]::UtcNow
    $lastStep = -2
    do {
        # each bridge reflection call stalls the game's main thread ~83 ms (visible stutter at 700 ms polling)
        Start-Sleep -Milliseconds 2500
        $api = Invoke-Api 'Status'
        if ($api.Puppet -and $api.Puppet.StepIndex -ne $lastStep) {
            $lastStep = $api.Puppet.StepIndex
            Log "[$Mode] step $($api.Puppet.StepIndex + 1)/$($api.Puppet.StepCount): $($api.Puppet.CurrentStep)"
        }
        if (!$shotTaken -and ([DateTime]::UtcNow - $started).TotalSeconds -ge 8) {
            try {
                $shot = Invoke-SptBridge 'screenshot'
                Invoke-WebRequest ($script:BridgeBase + $shot.url) -Headers $script:BridgeHeaders -OutFile (Join-Path $modeDirectory 'puppet.png') -UseBasicParsing
                Log "[$Mode] Saved screenshot"
            } catch { Log "[$Mode] Screenshot failed: $($_.Exception.Message)" }
            $shotTaken = $true
        }
        if (!$burstTaken -and $api.Puppet -and $api.Puppet.CurrentStep -eq $BurstStep) {
            # bridge screenshots take a while each, so shoot straight away to stay inside the step
            for ($i = 1; $i -le $BurstCount; $i++) {
                try {
                    $shot = Invoke-SptBridge 'screenshot'
                    Invoke-WebRequest ($script:BridgeBase + $shot.url) -Headers $script:BridgeHeaders -OutFile (Join-Path $modeDirectory ("burst-{0}.png" -f $i)) -UseBasicParsing
                } catch { Log "[$Mode] Burst screenshot $i failed: $($_.Exception.Message)" }
            }
            Log "[$Mode] Saved $BurstCount burst screenshots during $BurstStep"
            $burstTaken = $true
        }
        if (([DateTime]::UtcNow - $started).TotalSeconds -gt $PuppetTimeoutSeconds) {
            Invoke-Api $(if ($Scenario -like 'fleet*') { 'StopFleet' } else { 'Stop' }) | Out-Null
            throw 'Puppet run exceeded its timeout; stopped it.'
        }
    } while ($api.Capturing -or $api.Saving -or $api.LastCapturePath -eq $previousCapture)

    Log "[$Mode] Ended: $($api.LastEndReason)"
    if ($HitReactions -ne 'Off') {
        Log "[$Mode] Reactions: $($api.Reactions.Starts) played / $($api.Reactions.Accepted) triggers; last result: $($api.Reactions.Result)"
    }
    Log "[$Mode] Capture: $($api.LastCapturePath)"
    $api | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $modeDirectory 'status.json')
    $summary = Join-Path $modeDirectory 'summary.json'
    if ($Scenario -like 'fleet*') {
        # a fleet stream is not a capture: the fleet report reads it directly
        & python (Join-Path $PSScriptRoot 'fleet_report.py') $api.LastCapturePath | Set-Content -LiteralPath $summary
    } else {
        & python (Join-Path $PSScriptRoot 'summarize_capture.py') $api.LastCapturePath | Set-Content -LiteralPath $summary
    }
    Log "[$Mode] Summary: $summary"
    Invoke-PlacementArtifacts $Mode ([string]$api.LastCapturePath) $modeDirectory
    if ($Pose -and $Scenario -notlike 'fleet*') {
        $recorded = Get-Content -LiteralPath $summary -Raw | ConvertFrom-Json
        if ($recorded.pose_playback.AppliedFrames -eq 0) {
            Log "[$Mode] WARNING: Playback applied zero frames. Replay saved, but this run does not validate the animation or foot placer; inspect movement/lane coverage."
        }
    }
    return $summary
}

if ($PoseAb -and $FootLock -eq 'ab') { throw 'Use either -PoseAb or -FootLock ab, not both.' }
if ($PoseAb -and !$PoseClip) { throw '-PoseAb needs -PoseClip.' }
$modes = if ($PoseAb) {
    @(@{ Name = 'off'; Lock = ($FootLock -eq 'on'); Pose = '' }, @{ Name = 'on'; Lock = ($FootLock -eq 'on'); Pose = $PoseClip })
} elseif ($FootLock -eq 'ab') {
    @(@{ Name = 'off'; Lock = $false; Pose = $PoseClip }, @{ Name = 'on'; Lock = $true; Pose = $PoseClip })
} else {
    @(@{ Name = $FootLock; Lock = ($FootLock -eq 'on'); Pose = $PoseClip })
}
$summaries = @{}
foreach ($mode in $modes) {
    # Log writes to the pipeline too, so the path is the last value out
    $output = @(Invoke-PuppetRun $mode.Name $mode.Lock $mode.Pose)
    $output | Select-Object -SkipLast 1 | ForEach-Object { Write-Output $_ }
    $summaries[$mode.Name] = $output[-1]
}
if ($modes.Count -eq 2) {
    $comparison = Join-Path $runDirectory 'comparison.json'
    & python (Join-Path $PSScriptRoot 'compare_puppet.py') $summaries['off'] $summaries['on'] | Set-Content -LiteralPath $comparison
    Log "Comparison: $comparison"
}

# 5. optional exit
if ($LeaveRaid) {
    $game = Get-GameStatus
    if ($game -and $game.Status -eq 'Started') {
        Invoke-SptBridge 'invoke' @{ handle = $game.Handle; member = 'EFT.LocalGame::Stop(System.String,EFT.ExitStatus,System.String,System.Single)'; arguments = @($ProfileId, 'Left', '', 0) } | Out-Null
        Log 'Requested raid exit'
    }
}
