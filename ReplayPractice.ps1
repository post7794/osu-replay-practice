# Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
# See the LICENCE file in the repository root for full licence text.

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Build', 'Test', 'Run', 'Package')]
    [string]$Action = 'Build',
    [int]$DebugClientId = 4242
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tools = Join-Path $root '.tools'
$localDotnet = Join-Path $tools 'dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$logs = Join-Path $tools 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$nugetConfig = Join-Path $tools 'NuGet.Config'
if (!(Test-Path -LiteralPath $nugetConfig)) {
    '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>' |
        Set-Content -LiteralPath $nugetConfig -Encoding UTF8
}

$environment = @{
    DOTNET_CLI_HOME = Join-Path $tools 'dotnet-home'
    NUGET_PACKAGES = Join-Path $tools 'nuget'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_CLI_UI_LANGUAGE = 'en-US'
    VSLANG = '1033'
}
if ($dotnet -eq $localDotnet) { $environment.DOTNET_ROOT = Split-Path -Parent $localDotnet }
$previousEnvironment = @{}
foreach ($key in $environment.Keys) {
    $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $environment[$key], 'Process')
}

function Invoke-Dotnet {
    param([string[]]$Arguments, [string]$LogName)
    & $dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $logs $LogName)
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE. See .tools/logs/$LogName." }
}

function Invoke-ReplayPracticePackage {
    $runtime = 'win-x64'
    $packageName = 'osu-replay-practice-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $runtime
    $stage = Join-Path $tools ('packages\' + $packageName)
    $dist = Join-Path $root 'dist'
    $zip = Join-Path $dist ($packageName + '.zip')
    if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $zip)) {
        throw 'The package output already exists. Wait a second and run Package again.'
    }

    $project = 'osu.Desktop/osu.Desktop.csproj'
    $properties = @(
        '-p:ReplayPracticeTestBuild=true',
        '-p:PublishTrimmed=false',
        '-p:PublishSingleFile=false',
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        '-p:CopyOutputSymbolsToPublishDirectory=false',
        '-p:PublishDocumentationFile=false'
    )
    Invoke-Dotnet -Arguments (@(
        'restore', $project, '--runtime', $runtime, '--configfile', $nugetConfig,
        '--disable-parallel', '-p:Configuration=Release', '-p:SelfContained=true',
        '-m:1', '-nr:false', '--verbosity', 'minimal'
    ) + $properties) -LogName ($packageName + '-restore.log')
    Invoke-Dotnet -Arguments (@(
        'publish', $project, '--no-restore', '--configuration', 'Release',
        '--runtime', $runtime, '--self-contained', 'true', '--output', $stage,
        '-m:1', '-nr:false', '--verbosity', 'minimal'
    ) + $properties) -LogName ($packageName + '-publish.log')

    foreach ($required in @('osu!.exe', 'osu!.dll', 'osu!.deps.json', 'osu!.runtimeconfig.json',
        'osu.Game.dll', 'osu.Game.Rulesets.Osu.dll', 'coreclr.dll', 'hostfxr.dll',
        'hostpolicy.dll', 'System.Private.CoreLib.dll', 'realm-wrappers.dll', 'SDL3.dll', 'bass.dll',
        'zh\osu.Game.resources.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $stage $required))) {
            throw "The published bundle is missing required file: $required"
        }
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $stage 'osu!.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks) {
        throw 'The bundle unexpectedly requires a separately installed .NET runtime.'
    }

    Copy-Item -LiteralPath (Join-Path $root 'packaging\ReplayPracticeTest\README.txt') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'packaging\ReplayPracticeTest\Start-Replay-Practice.cmd') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'LICENCE') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'LAZER_SYNC.md') -Destination $stage

    # Preserve available dependency licence files and each runtime package's licence metadata.
    $thirdParty = Join-Path $stage 'THIRD-PARTY'
    New-Item -ItemType Directory -Path $thirdParty -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root 'packaging\ReplayPracticeTest\THIRD-PARTY') -File |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $thirdParty }
    $deps = Get-Content -LiteralPath (Join-Path $stage 'osu!.deps.json') -Raw | ConvertFrom-Json
    foreach ($library in $deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
        $parts = $library.Name.Split('/')
        if ($parts.Length -ne 2 -or $library.Name -notmatch '^[a-zA-Z0-9_.+-]+/[a-zA-Z0-9_.+-]+$') {
            throw "Unexpected dependency identifier: $($library.Name)"
        }
        $source = Join-Path $environment.NUGET_PACKAGES ($parts[0].ToLowerInvariant() + '\' + $parts[1].ToLowerInvariant())
        $nuspec = Join-Path $source ($parts[0].ToLowerInvariant() + '.nuspec')
        if (!(Test-Path -LiteralPath $nuspec)) { throw "Missing dependency metadata: $($library.Name)" }
        $destination = Join-Path $thirdParty ($parts[0] + '-' + $parts[1])
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath $nuspec -Destination $destination
        Get-ChildItem -LiteralPath $source -File | Where-Object {
            $_.Name -match '^(LICEN[CS]E|COPYING|NOTICE|THIRD[-_]PARTY[-_]NOTICES)([._-].*)?$'
        } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination }
        [xml]$metadata = Get-Content -LiteralPath $nuspec -Raw
        foreach ($license in $metadata.SelectNodes('//*[local-name()="license"][@type="file"]')) {
            $licensePath = [IO.Path]::GetFullPath((Join-Path $source $license.InnerText))
            $sourcePrefix = [IO.Path]::GetFullPath($source).TrimEnd('\') + '\'
            if (!$licensePath.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Dependency licence path escapes its package: $($library.Name)"
            }
            Copy-Item -LiteralPath $licensePath -Destination $destination
        }
    }
    $framework = $runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }
    if (!$framework) { throw 'Missing bundled .NET framework version.' }
    $runtimePackage = Join-Path $environment.NUGET_PACKAGES ('microsoft.netcore.app.runtime.' + $runtime + '\' + $framework.version)
    $runtimeNotices = Join-Path $thirdParty ('Microsoft.NETCore.App.Runtime.' + $runtime + '-' + $framework.version)
    New-Item -ItemType Directory -Path $runtimeNotices -Force | Out-Null
    foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        Copy-Item -LiteralPath (Join-Path $runtimePackage $notice) -Destination $runtimeNotices
    }

    $revision = & git -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the source revision.' }
    $changes = & git -C $root status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the source status.' }
    [ordered]@{
        package = $packageName
        builtAt = (Get-Date).ToString('o')
        sourceRevision = $revision
        includesUncommittedChanges = [bool]$changes
        configuration = 'Release'
        runtimeIdentifier = $runtime
        bundledDotnetVersion = $framework.version
        selfContained = $true
        testBuildIsolation = $true
        defaultDataDirectory = '%APPDATA%\osu-replay-practice-test'
        automaticUpdates = $false
        fileAssociationsInstalled = $false
        userBeatmapsReplaysAndConfigurationIncluded = $false
        features = @('osu-standard-replay-takeover', 'osu-standard-autoplay-takeover', 'osu-lazer-offline-two-way-beatmap-sync', 'osu-lazer-offline-two-way-replay-sync', 'skin-independent-replay-object-marker', 'hold-to-focus-replay-object')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'build-info.json') -Encoding UTF8

    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse)
    $forbidden = @($files | Where-Object {
        $_.Extension -in @('.osu', '.osz', '.osr', '.osk', '.realm', '.db', '.sqlite', '.sqlite3', '.cfg', '.pdb') -or
        $_.FullName.Substring($stage.Length + 1) -match '(^|\\)(Songs|Replays|logs|cache|files)(\\|$)'
    })
    if ($forbidden.Count -gt 0) {
        throw ('Unexpected user-data or debug file in bundle: ' + ($forbidden.Name -join ', '))
    }
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
    $hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
    ($hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip)) |
        Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
    Write-Host "Test bundle: $zip"
    Write-Host ('ZIP size: {0:N1} MiB; files: {1}' -f ((Get-Item -LiteralPath $zip).Length / 1MB), $files.Count)
    Write-Host "SHA256: $($hash.Hash)"
}

Push-Location -LiteralPath $root
try {
    if ($Action -eq 'Package') {
        Invoke-ReplayPracticePackage
    }
    elseif ($Action -eq 'Test') {
        $project = 'osu.Game.Tests/osu.Game.Tests.csproj'
        Invoke-Dotnet -Arguments @('restore', $project, '--configfile', $nugetConfig, '--disable-parallel', '-m:1', '-nr:false', '--verbosity', 'minimal') -LogName 'restore-tests.log'
        Invoke-Dotnet -Arguments @('build', $project, '--no-restore', '-m:1', '-nr:false', '--verbosity', 'minimal') -LogName 'build-tests.log'
        $filter = 'FullyQualifiedName~ReplayPracticeLocalisationTest|FullyQualifiedName~TestSceneSongProgress|FullyQualifiedName~ReplayPracticeSessionTest|FullyQualifiedName~ReplayTransportTest|FullyQualifiedName~TestSceneReplayPractice|FullyQualifiedName~TestSceneReplayPlayer|FullyQualifiedName~FramedReplayInputHandlerTest|FullyQualifiedName~TestSceneReplayRecorder|FullyQualifiedName~TestSceneReplayShortcuts|FullyQualifiedName~TestSceneKeyBindingPanel|FullyQualifiedName~TestScenePause|FullyQualifiedName~TestSceneSongSelect.TestAutoplay|FullyQualifiedName~StableBeatmapSync|FullyQualifiedName~LazerSyncLocalisationTest|FullyQualifiedName~LazerBeatmapSync|FullyQualifiedName~LazerReplaySync|FullyQualifiedName~BeatmapImporter|FullyQualifiedName~FileStoreTests|FullyQualifiedName~LegacyBeatmapImporterTest'
        Invoke-Dotnet -Arguments @('test', $project, '--no-build', '--no-restore', '-m:1', '-nr:false', '--filter', $filter, '--logger', 'trx;LogFileName=replay-regression.trx', '--results-directory', '.tools/test-results', '--verbosity', 'minimal') -LogName 'test-regression.log'
    }
    else {
        $project = 'osu.Desktop/osu.Desktop.csproj'
        Invoke-Dotnet -Arguments @('restore', $project, '--configfile', $nugetConfig, '--disable-parallel', '-m:1', '-nr:false', '--verbosity', 'minimal') -LogName 'restore-desktop.log'
        Invoke-Dotnet -Arguments @('build', $project, '--no-restore', '-m:1', '-nr:false', '--verbosity', 'minimal') -LogName 'build-desktop.log'
        if ($Action -eq 'Run') {
            Write-Host "Launching osu-development-$DebugClientId (separate from the normal osu! data directory)."
            & $dotnet run --project $project --no-build --no-restore --no-launch-profile -- "--debug-client-id=$DebugClientId"
            if ($LASTEXITCODE -ne 0) { throw "The development client exited with code $LASTEXITCODE." }
        }
    }
}
finally {
    Pop-Location
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }
}
