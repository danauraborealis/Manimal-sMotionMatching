[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [string]$SptRoot = 'D:\SPT41Dev'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$legacyGuid = 'com.manimal.motionmatching'
$legacyDisplayName = 'Manimal-MotionMatching'
$legacyAssemblyName = 'Manimal.MotionMatching.dll'
$currentGuid = $null
$currentDisplayName = $null
$currentAssemblyName = $null
$runtimeDataNames = @('alyx_posedb.json', 'reaction_posedb.json')

function Resolve-SptTarget {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $candidate = [IO.Path]::GetFullPath([IO.Path]::Combine($script:SptRootFull, $RelativePath))
    if ($candidate -ne $script:SptRootFull -and -not $candidate.StartsWith($script:SptRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a target outside the SPT root: $RelativePath"
    }
    return $candidate
}

function Remove-StagingDirectory {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    foreach ($file in @(Get-ChildItem -LiteralPath $Path -File -Force)) {
        Remove-Item -LiteralPath $file.FullName -Force
    }
    if (@(Get-ChildItem -LiteralPath $Path -Force).Count -eq 0) {
        Remove-Item -LiteralPath $Path -Force
    }
}

function New-PackageStage {
    param([Parameter(Mandatory = $true)][string]$ArchivePath, [Parameter(Mandatory = $true)][string[]]$ExpectedEntries)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('MotionMagic-install-' + [Guid]::NewGuid().ToString('N'))
    $archive = $null
    try {
        [void](New-Item -ItemType Directory -Path $stage)
        $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
        $entries = @($archive.Entries)
        $entryNames = @($entries | ForEach-Object { $_.FullName })
        $unexpected = @($entryNames | Where-Object { $ExpectedEntries -notcontains $_ })
        $missing = @($ExpectedEntries | Where-Object { $entryNames -notcontains $_ })
        if ($unexpected.Count -gt 0) {
            throw "Install archive contains unexpected entries: $($unexpected -join ', ')"
        }
        if ($missing.Count -gt 0) {
            throw "Install archive is missing required entries: $($missing -join ', ')"
        }
        if ($entryNames.Count -ne $ExpectedEntries.Count) {
            throw 'Install archive contains duplicate or unsupported entries.'
        }

        foreach ($expected in $ExpectedEntries) {
            $entry = $null
            foreach ($candidate in $entries) {
                if ($candidate.FullName -ceq $expected) { $entry = $candidate; break }
            }
            if ($null -eq $entry -or $entry.Length -le 0) { throw "Install archive entry is empty: $expected" }
            $destination = Join-Path $stage ([IO.Path]::GetFileName($expected))
            $input = $null
            $output = $null
            try {
                $input = $entry.Open()
                $output = [IO.File]::Create($destination)
                $input.CopyTo($output)
            }
            finally {
                if ($null -ne $output) { $output.Dispose() }
                if ($null -ne $input) { $input.Dispose() }
            }
        }
        return $stage
    }
    catch {
        Remove-StagingDirectory -Path $stage
        throw
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
    }
}

function Get-DllFiles {
    param([Parameter(Mandatory = $true)][string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $Directory -File -Filter '*.dll' -Force)
}

function Backup-File {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Label,
        [switch]$CopyOnly
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { return $false }
    $destination = Join-Path $script:BackupRoot $Label
    [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination))
    if ($CopyOnly) { Copy-Item -LiteralPath $Source -Destination $destination -Force }
    else { Move-Item -LiteralPath $Source -Destination $destination -Force }
    return $true
}

function Test-ConfigConfigured {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    foreach ($line in @(Get-Content -LiteralPath $Path)) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#') -or $trimmed.StartsWith(';') -or ($trimmed.StartsWith('[') -and $trimmed.EndsWith(']'))) { continue }
        $equals = $trimmed.IndexOf('=')
        if ($equals -lt 0) { return $true }
        if ($trimmed.Substring($equals + 1).Trim().Length -gt 0) { return $true }
    }
    return $false
}

function Convert-LegacyConfig {
    param([Parameter(Mandatory = $true)][string]$Contents)

    $pathRewrites = @{}
    $pathRewrites['BepInEx/plugins/Manimal-MotionMatching/alyx_posedb.json'] = 'BepInEx/plugins/' + $currentDisplayName + '/alyx_posedb.json'
    $pathRewrites['BepInEx\plugins\Manimal-MotionMatching\alyx_posedb.json'] = 'BepInEx\plugins\' + $currentDisplayName + '\alyx_posedb.json'
    $pathRewrites['BepInEx/plugins/Manimal-MotionMatching/reaction_posedb.json'] = 'BepInEx/plugins/' + $currentDisplayName + '/reaction_posedb.json'
    $pathRewrites['BepInEx\plugins\Manimal-MotionMatching\reaction_posedb.json'] = 'BepInEx\plugins\' + $currentDisplayName + '\reaction_posedb.json'
    foreach ($fileName in $runtimeDataNames) {
        $oldAbsolute = [IO.Path]::Combine($script:SptRootFull, 'BepInEx', 'plugins', $legacyDisplayName, $fileName)
        $newAbsolute = [IO.Path]::Combine($script:SptRootFull, 'BepInEx', 'plugins', $currentDisplayName, $fileName)
        $pathRewrites[$oldAbsolute] = $newAbsolute
    }

    $newline = if ($Contents.Contains("`r`n")) { "`r`n" } else { "`n" }
    $normalized = $Contents.Replace("`r`n", "`n").Replace("`r", "`n")
    $lines = $normalized.Split([string[]]@("`n"), [System.StringSplitOptions]::None)
    for ($index = 0; $index -lt $lines.Length; $index++) {
        $line = $lines[$index]
        if ($line.StartsWith('## Settings file was created by plugin ', [StringComparison]::Ordinal)) {
            $lines[$index] = '## Settings file was created by plugin ' + $currentDisplayName
            continue
        }
        if ($line.StartsWith('## Plugin GUID: ', [StringComparison]::Ordinal)) {
            $lines[$index] = '## Plugin GUID: ' + $currentGuid
            continue
        }

        $equals = $line.IndexOf('=')
        if ($equals -lt 0) { continue }
        $valueWithWhitespace = $line.Substring($equals + 1)
        $value = $valueWithWhitespace.Trim()
        if (-not $pathRewrites.ContainsKey($value)) { continue }
        $leading = $valueWithWhitespace.Length - $valueWithWhitespace.TrimStart().Length
        $trailing = $valueWithWhitespace.Length - $valueWithWhitespace.TrimEnd().Length
        $lines[$index] = $line.Substring(0, $equals + 1) + ((' ' * $leading)) + [string]$pathRewrites[$value] + ((' ' * $trailing))
    }
    return [string]::Join($newline, $lines)
}

try {
    $propsPath = Join-Path $PSScriptRoot '..\Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) { throw "Identity file not found: $propsPath" }
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $propertyGroup = @($props.Project.PropertyGroup) | Select-Object -First 1
    $propsAuthor = [string]$propertyGroup.ModAuthor
    $propsName = [string]$propertyGroup.ModName
    $propsGuid = [string]$propertyGroup.ModGuid
    if ([string]::IsNullOrWhiteSpace($propsAuthor) -or [string]::IsNullOrWhiteSpace($propsName) -or [string]::IsNullOrWhiteSpace($propsGuid)) {
        throw 'Directory.Build.props is missing the mod identity.'
    }
    $currentGuid = $propsGuid
    $propsDisplayName = $propsAuthor + '-' + $propsName
    $propsAssemblyName = $propsAuthor + '.' + $propsName + '.dll'
    $currentDisplayName = $propsDisplayName
    $currentAssemblyName = $propsAssemblyName

    if (-not [IO.Path]::IsPathRooted($SptRoot)) { throw "SptRoot must be an absolute path: $SptRoot" }
    if (-not (Test-Path -LiteralPath $SptRoot -PathType Container)) { throw "SPT root does not exist: $SptRoot" }
    $script:SptRootFull = [IO.Path]::GetFullPath((Get-Item -LiteralPath $SptRoot).FullName)
    $script:SptRootPrefix = if ($script:SptRootFull.EndsWith('\')) { $script:SptRootFull } else { $script:SptRootFull + '\' }

    $pluginsRoot = Resolve-SptTarget 'BepInEx\plugins'
    $configRoot = Resolve-SptTarget 'BepInEx\config'
    if (-not (Test-Path -LiteralPath $pluginsRoot -PathType Container)) { throw "BepInEx plugins directory does not exist: $pluginsRoot" }
    if (-not (Test-Path -LiteralPath $configRoot -PathType Container)) { throw "BepInEx config directory does not exist: $configRoot" }

    $packageFull = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $PackagePath).Path)
    if ([IO.Path]::GetExtension($packageFull) -ine '.zip') { throw "Package must be a .zip archive: $PackagePath" }
    $running = @(foreach ($processName in @('EscapeFromTarkov', 'EscapeFromTarkov_BE')) {
        Get-Process -Name $processName -ErrorAction SilentlyContinue
    })
    if ($running.Count -gt 0) {
        throw "Close the game before installing MotionMagic. Running process(es): $((@($running | ForEach-Object { $_.ProcessName }) -join ', '))"
    }

    $newPluginDir = Resolve-SptTarget ('BepInEx\plugins\' + $currentDisplayName)
    $oldPluginDir = Resolve-SptTarget ('BepInEx\plugins\' + $legacyDisplayName)
    $newConfigPath = Resolve-SptTarget ('BepInEx\config\' + $currentGuid + '.cfg')
    $oldConfigPath = Resolve-SptTarget ('BepInEx\config\' + $legacyGuid + '.cfg')
    $newRuntimeFiles = @(
        (Join-Path $newPluginDir $currentAssemblyName),
        (Join-Path $newPluginDir 'alyx_posedb.json'),
        (Join-Path $newPluginDir 'reaction_posedb.json')
    )
    $oldDllPath = Join-Path $oldPluginDir $legacyAssemblyName

    foreach ($directory in @($newPluginDir, $oldPluginDir)) {
        if (Test-Path -LiteralPath $directory -PathType Leaf) { throw "Expected a plugin directory, found a file: $directory" }
    }
    $allKnownFiles = @($newRuntimeFiles) + @($oldDllPath, $newConfigPath, $oldConfigPath)
    foreach ($file in $allKnownFiles) {
        if (Test-Path -LiteralPath $file -PathType Container) { throw "Expected a runtime file, found a directory: $file" }
    }
    $unknownOldDlls = @(Get-DllFiles $oldPluginDir | Where-Object { $_.Name -ine $legacyAssemblyName })
    if ($unknownOldDlls.Count -gt 0) {
        throw "Old plugin directory contains an unknown DLL; refusing to risk double loading: $($unknownOldDlls.Name -join ', ')"
    }
    $unknownNewDlls = @(Get-DllFiles $newPluginDir | Where-Object { $_.Name -ine $currentAssemblyName })
    if ($unknownNewDlls.Count -gt 0) {
        throw "MotionMagic plugin directory contains an unknown DLL; refusing to overwrite it: $($unknownNewDlls.Name -join ', ')"
    }

    $expectedEntries = @(
        ('BepInEx/plugins/' + $currentDisplayName + '/' + $currentAssemblyName),
        ('BepInEx/plugins/' + $currentDisplayName + '/alyx_posedb.json'),
        ('BepInEx/plugins/' + $currentDisplayName + '/reaction_posedb.json')
    )
    $stage = New-PackageStage -ArchivePath $packageFull -ExpectedEntries $expectedEntries
    try {
        $oldConfigExists = Test-Path -LiteralPath $oldConfigPath -PathType Leaf
        $newConfigConfigured = Test-ConfigConfigured -Path $newConfigPath
        $importLegacyConfig = $oldConfigExists -and -not $newConfigConfigured
        $legacyConfigContents = if ($oldConfigExists) { [IO.File]::ReadAllText($oldConfigPath) } else { $null }

        $timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')
        $script:BackupRoot = Resolve-SptTarget ('BepInEx\MotionMagic-backups\' + $timestamp)
        [void](New-Item -ItemType Directory -Path $script:BackupRoot)

        $backedUp = @()
        foreach ($file in $newRuntimeFiles) {
            if (Backup-File -Source $file -Label ('previous-plugin\' + [IO.Path]::GetFileName($file))) { $backedUp += $file }
        }
        if (Backup-File -Source $oldDllPath -Label ('legacy-plugin\' + $legacyAssemblyName)) { $backedUp += $oldDllPath }
        if (Backup-File -Source $oldConfigPath -Label ('legacy-config\' + $legacyGuid + '.cfg') -CopyOnly) { $backedUp += $oldConfigPath }

        [void](New-Item -ItemType Directory -Force -Path $newPluginDir)
        foreach ($entry in $expectedEntries) {
            $source = Join-Path $stage ([IO.Path]::GetFileName($entry))
            $destination = Join-Path $newPluginDir ([IO.Path]::GetFileName($entry))
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }

        if ($importLegacyConfig) {
            $temporaryConfig = $newConfigPath + '.motionmagic-migration.tmp'
            [IO.File]::WriteAllText($temporaryConfig, (Convert-LegacyConfig -Contents $legacyConfigContents), [Text.UTF8Encoding]::new($false))
            Move-Item -LiteralPath $temporaryConfig -Destination $newConfigPath -Force
            Write-Output "Imported legacy settings into $newConfigPath"
        }
        elseif ($oldConfigExists) {
            Write-Output "Preserved existing new config; legacy config is backed up at $script:BackupRoot\legacy-config"
        }

        foreach ($entry in $expectedEntries) {
            $installed = Join-Path $newPluginDir ([IO.Path]::GetFileName($entry))
            if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) { throw "Installed runtime file is missing: $installed" }
            $staged = Join-Path $stage ([IO.Path]::GetFileName($entry))
            if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash) {
                throw "Installed runtime file failed hash verification: $installed"
            }
        }
        if (Test-Path -LiteralPath $oldDllPath -PathType Leaf) { throw "Legacy plugin DLL is still present: $oldDllPath" }
        if ((Test-Path -LiteralPath $oldPluginDir -PathType Container) -and (@(Get-ChildItem -LiteralPath $oldPluginDir -Force).Count -eq 0)) {
            Remove-Item -LiteralPath $oldPluginDir -Force
        }

        Write-Output "Installed $currentDisplayName ($currentAssemblyName) under $newPluginDir"
        if ($backedUp.Count -gt 0) { Write-Output "Backed up $($backedUp.Count) existing runtime file(s) at $script:BackupRoot" }
        else { Write-Output "Created rollback directory at $script:BackupRoot" }
        if (Test-Path -LiteralPath $oldPluginDir -PathType Container) {
            Write-Output "Left the legacy plugin folder in place without its DLL: $oldPluginDir"
        }
    }
    finally {
        Remove-StagingDirectory -Path $stage
    }
}
finally {
    # No recursive delete is used. A failed install leaves its rollback directory in the SPT tree.
}
