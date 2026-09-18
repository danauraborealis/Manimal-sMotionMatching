param(
    [string]$SptRoot = 'D:\SPT41Dev',
    [string]$MainPoseDatabasePath = '',
    [string]$ReactionPoseDatabasePath = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$expectedBones = @(
    'Base HumanPelvis', 'Base HumanLThigh1', 'Base HumanLCalf', 'Base HumanLFoot',
    'Base HumanRThigh1', 'Base HumanRCalf', 'Base HumanRFoot'
)
$reactionUpperBones = @(
    'Base HumanSpine1', 'Base HumanSpine2', 'Base HumanSpine3', 'Base HumanRibcage',
    'Base HumanLCollarbone', 'Base HumanLUpperarm', 'Base HumanLForearm1',
    'Base HumanRCollarbone', 'Base HumanRUpperarm', 'Base HumanRForearm1'
)
if ([string]::IsNullOrWhiteSpace($MainPoseDatabasePath)) {
    $MainPoseDatabasePath = Join-Path $projectRoot 'tmp\alyx\start_upper_posedb.json'
} elseif (-not [IO.Path]::IsPathRooted($MainPoseDatabasePath)) {
    $MainPoseDatabasePath = Join-Path $projectRoot $MainPoseDatabasePath
}
if ([string]::IsNullOrWhiteSpace($ReactionPoseDatabasePath)) {
    $ReactionPoseDatabasePath = Join-Path $projectRoot 'tmp\alyx\reaction_upper_posedb_expanded.json'
} elseif (-not [IO.Path]::IsPathRooted($ReactionPoseDatabasePath)) {
    $ReactionPoseDatabasePath = Join-Path $projectRoot $ReactionPoseDatabasePath
}

function Assert-PoseDatabase {
    param([string]$Path, [string]$Label, [switch]$Reaction)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label pose database not found: $Path"
    }
    try {
        $database = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $Path -Raw) -AsHashtable -Depth 100
    } catch {
        throw "$Label pose database is not valid JSON: $($_.Exception.Message)"
    }
    if ($database -isnot [System.Collections.IDictionary]) { throw "$Label pose database root must be an object." }
    if ($database.schema -cne 'manimal.motionmatching.posedb.v1') {
        throw "$Label pose database has an unsupported schema: $($database.schema)"
    }
    $bones = @($database.bones)
    if (($bones -join "`n") -cne ($expectedBones -join "`n")) {
        throw "$Label pose database bone order does not match the runtime layout."
    }
    if ($database.restLocal -isnot [System.Collections.IDictionary] -or $database.restLocal.Count -eq 0) {
        throw "$Label pose database is missing restLocal data."
    }
    if ($null -eq $database.clips) { throw "$Label pose database has no clips array." }
    $clips = @($database.clips)
    if ($clips.Count -eq 0) { throw "$Label pose database contains no clips." }
    foreach ($clip in $clips) {
        if ([string]::IsNullOrWhiteSpace([string]$clip.name)) { throw "$Label pose database has a clip without a name." }
        $frames = 0
        if (-not [int]::TryParse([string]$clip.frames, [ref]$frames) -or $frames -lt 1) {
            throw "$Label clip '$($clip.name)' has an invalid frame count."
        }
        $fps = 0.0
        if (-not [double]::TryParse([string]$clip.fps, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$fps) -or $fps -le 0 -or [double]::IsNaN($fps) -or [double]::IsInfinity($fps)) {
            throw "$Label clip '$($clip.name)' has an invalid frame rate."
        }
        if ($null -eq $clip.loop) { throw "$Label clip '$($clip.name)' is missing its loop flag." }
        if ($clip.rotations -isnot [System.Collections.IDictionary]) { throw "$Label clip '$($clip.name)' is missing rotations." }
        foreach ($bone in $expectedBones) {
            if ($null -eq $clip.rotations[$bone] -or @($clip.rotations[$bone]).Count -ne $frames) {
                throw "$Label clip '$($clip.name)' has an invalid '$bone' rotation track."
            }
        }
        if ($null -eq $clip.pelvisPosition -or @($clip.pelvisPosition).Count -ne $frames) {
            throw "$Label clip '$($clip.name)' has an invalid pelvisPosition track."
        }
        if ($null -ne $clip.rootSpeed -and @($clip.rootSpeed).Count -ne $frames) {
            throw "$Label clip '$($clip.name)' has an invalid rootSpeed track."
        }
        if ($null -ne $clip.upperBodyRotations) {
            if ($clip.upperBodyRotations -isnot [System.Collections.IDictionary]) { throw "$Label clip '$($clip.name)' has malformed upperBodyRotations." }
            foreach ($bone in $reactionUpperBones) {
                if ($null -eq $clip.upperBodyRotations[$bone] -or @($clip.upperBodyRotations[$bone]).Count -ne $frames) {
                    throw "$Label clip '$($clip.name)' has an invalid '$bone' upper-body track."
                }
            }
        }
    }

    if (-not $Reaction) {
        $hasStart = @($clips | Where-Object { @($_.roles) -contains 'start' -and $null -ne $_.rootSpeed }).Count -gt 0
        $hasStop = @($clips | Where-Object { @($_.roles) -contains 'stop' -and $null -ne $_.rootSpeed }).Count -gt 0
        if (-not $hasStart -or -not $hasStop) { throw 'Main pose database must contain rootSpeed-backed start and stop clips.' }
        foreach ($gait in @('walk', 'run', 'sprint')) {
            $prefix = "tarkov_$gait"
            $hasCycle = @($clips | Where-Object {
                $_.loop -and $_.gait -ceq $gait -and ([string]$_.name).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
            }).Count -gt 0
            if (-not $hasCycle) { throw "Main pose database is missing a looping Tarkov $gait cycle." }
        }
    } else {
        $expectedStumbles = @('stumble_n', 'stumble_s', 'stumble_e', 'stumble_w')
        $expectedHits = @('new_flinch_02_hitreact', 'new_flinch_35_hitreact', 'new_flinch_06_hitreact', 'new_flinch_20_hitreact', 'new_flinch_11_hitreact')
        foreach ($name in $expectedStumbles) {
            $clip = @($clips | Where-Object { $_.name -ceq $name -and @($_.roles) -contains 'reaction' } | Select-Object -First 1)
            if ($clip.Count -eq 0 -or $null -eq $clip[0].upperBodyRotations) { throw "Reaction database is missing the upper-body '$name' stumble." }
        }
        foreach ($name in $expectedHits) {
            $clip = @($clips | Where-Object { $_.name -ceq $name -and @($_.roles) -contains 'hitreact' } | Select-Object -First 1)
            if ($clip.Count -eq 0 -or $null -eq $clip[0].upperBodyRotations) { throw "Reaction database is missing the upper-body '$name' hit reaction." }
        }
    }
    return [PSCustomObject]@{ Path = (Resolve-Path -LiteralPath $Path).Path; Clips = $clips.Count }
}

$mainPoseDatabase = Assert-PoseDatabase -Path $MainPoseDatabasePath -Label 'Main'
$reactionPoseDatabase = Assert-PoseDatabase -Path $ReactionPoseDatabasePath -Label 'Reaction' -Reaction
$project = Join-Path $projectRoot 'src\MotionMatching\MotionMatching.csproj'
[xml]$identity = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props')
$version = [string]$identity.Project.PropertyGroup.ModVersion
$author = [string]$identity.Project.PropertyGroup.ModAuthor
$modName = [string]$identity.Project.PropertyGroup.ModName
$guid = [string]$identity.Project.PropertyGroup.ModGuid
$displayName = "$author-$modName"
$assemblyName = "$author.$modName.dll"
$originalAppData = $env:APPDATA
try {
    # Keep NuGet's configuration/cache lookup in the workspace for sandboxed builds.
    $env:APPDATA = Join-Path $projectRoot 'tmp\build-appdata'
    New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null
    & dotnet restore $project --configfile (Join-Path $projectRoot 'NuGet.Config') "-p:SptRoot=$SptRoot" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & dotnet build $project -c Release --no-restore "-p:SptRoot=$SptRoot" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally { $env:APPDATA = $originalAppData }

$dll = Join-Path $projectRoot "src\MotionMatching\bin\Release\netstandard2.1\$assemblyName"
Add-Type -Path (Join-Path $SptRoot 'BepInEx\core\Mono.Cecil.dll')
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory((Join-Path $SptRoot 'BepInEx\core'))
$resolver.AddSearchDirectory((Join-Path $SptRoot 'EscapeFromTarkov_Data\Managed'))
$resolver.AddSearchDirectory((Join-Path $SptRoot 'BepInEx\plugins\UnityToolkit'))
$readerParameters = [Mono.Cecil.ReaderParameters]::new()
$readerParameters.AssemblyResolver = $resolver
$compiled = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll, $readerParameters)
try {
    $plugin = @($compiled.MainModule.Types | Where-Object { $_.BaseType.FullName -eq 'BepInEx.BaseUnityPlugin' })
    if ($plugin.Count -ne 1) { throw 'Expected exactly one BaseUnityPlugin.' }
    $metadata = @($plugin[0].CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
    if ($metadata.Count -ne 1) { throw 'Missing BepInPlugin metadata.' }
    $values = @($metadata[0].ConstructorArguments | ForEach-Object { [string]$_.Value })
    if ($values[0] -ne $guid -or $values[1] -ne $displayName -or $values[2] -ne $version) { throw 'Compiled plugin identity mismatch.' }
    if ($compiled.Name.Version.ToString() -ne "$version.0") { throw 'Assembly version mismatch.' }
    $modInfo = $compiled.MainModule.GetType('Manimal.MotionMatching.ModInfo')
    $compiledRepository = @($modInfo.Fields | Where-Object { $_.Name -eq 'RepositoryUrl' })
    if ($compiledRepository.Count -ne 1 -or [string]$compiledRepository[0].Constant -cne [string]$identity.Project.PropertyGroup.ModRepositoryUrl) {
        throw 'Compiled source repository URL mismatch.'
    }
    $toolkit = @($plugin[0].CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' -and $_.ConstructorArguments[0].Value -eq 'com.arys.unitytoolkit' })
    if ($toolkit.Count -ne 1) { throw 'UnityToolkit dependency missing.' }
    $directPatches = @($compiled.MainModule.GetMemberReferences() | Where-Object { $_.DeclaringType.FullName -eq 'HarmonyLib.Harmony' -and $_.Name -match '^Patch' })
    if ($directPatches.Count -gt 0) { throw 'Direct Harmony registration is forbidden; use SPT ModulePatch.' }
} finally { $compiled.Dispose(); $resolver.Dispose() }

$releaseDirectory = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
$archivePath = Join-Path $releaseDirectory "$displayName-$version.zip"
$manifestPath = Join-Path $releaseDirectory "$displayName-$version.manifest.json"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageFiles = @(
    @{ Source = $dll; Entry = "BepInEx/plugins/$displayName/$assemblyName" },
    @{ Source = $mainPoseDatabase.Path; Entry = "BepInEx/plugins/$displayName/alyx_posedb.json" },
    @{ Source = $reactionPoseDatabase.Path; Entry = "BepInEx/plugins/$displayName/reaction_posedb.json" }
)
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $packageFiles) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.Source, $file.Entry) | Out-Null
    }
} finally { $archive.Dispose(); $stream.Dispose() }

$verify = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $expectedEntries = @($packageFiles | ForEach-Object { $_.Entry } | Sort-Object)
    $actualEntries = @($verify.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    if ($actualEntries.Count -ne $expectedEntries.Count -or (Compare-Object $expectedEntries $actualEntries)) {
        throw "Unexpected install archive contents: $($actualEntries -join ', ')"
    }
    $manifestFiles = @()
    foreach ($file in $packageFiles) {
        $entry = $verify.GetEntry($file.Entry)
        $entryStream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($entryStream)).Replace('-', '').ToLowerInvariant() }
        finally { $entryStream.Dispose(); $sha.Dispose() }
        $sourceHash = (Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $sourceHash) { throw "Packaged file hash mismatch: $($file.Entry)" }
        $manifestFiles += [PSCustomObject]@{
            path = $file.Entry
            bytes = [long](Get-Item -LiteralPath $file.Source).Length
            sha256 = $sourceHash
        }
    }
} finally { $verify.Dispose() }

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$repositoryUrl = [string]$identity.Project.PropertyGroup.ModRepositoryUrl
$manifest = [PSCustomObject]@{
    package = $displayName
    version = $version
    purpose = 'player-test-build'
    publicationCompliant = $false
    sourceRepositoryUrl = if ([string]::IsNullOrWhiteSpace($repositoryUrl)) { $null } else { $repositoryUrl }
    archive = [PSCustomObject]@{ file = [IO.Path]::GetFileName($archivePath); sha256 = $archiveHash }
    files = $manifestFiles
}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output "Verified player-test package and runtime databases ($($mainPoseDatabase.Clips) main clips, $($reactionPoseDatabase.Clips) reaction clips): $archivePath"
Write-Output "SHA-256 manifest (outside install archive): $manifestPath"
if ([string]::IsNullOrWhiteSpace($repositoryUrl)) {
    Write-Output 'Private development build: source repository metadata is not configured; this is not publication compliant.'
}
