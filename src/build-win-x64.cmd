@echo off
setlocal EnableExtensions

set "AITRANSLATOR_BUILD_PUBLISH_ROOT=%~f1"
if /I "%~1"=="--no-pause" set "AITRANSLATOR_BUILD_PUBLISH_ROOT="
set "AITRANSLATOR_BUILD_NO_PAUSE=0"
for %%A in (%*) do (
    if /I "%%~A"=="--no-pause" set "AITRANSLATOR_BUILD_NO_PAUSE=1"
)

set "AITRANSLATOR_BUILD_SCRIPT=%~f0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$path = $env:AITRANSLATOR_BUILD_SCRIPT; $content = [IO.File]::ReadAllText($path); $marker = '#__' + 'POWERSHELL__'; $index = $content.IndexOf($marker, [StringComparison]::Ordinal); if ($index -lt 0) { throw 'Embedded PowerShell section not found.' }; & ([ScriptBlock]::Create($content.Substring($index + $marker.Length)))"
set "BUILD_EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %BUILD_EXIT_CODE%

#__POWERSHELL__
$NoPause = $env:AITRANSLATOR_BUILD_NO_PAUSE -eq "1"
$publishRootArgument = $env:AITRANSLATOR_BUILD_PUBLISH_ROOT

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sourceDirectory = [IO.Path]::GetDirectoryName($env:AITRANSLATOR_BUILD_SCRIPT)
$rootDirectory = [IO.Path]::GetFullPath((Join-Path $sourceDirectory ".."))
$projectFile = Join-Path $sourceDirectory "AITranslator.csproj"
$launcherSource = Join-Path $sourceDirectory "Launcher\Program.cs"
$launcherIcon = Join-Path $sourceDirectory "Assets\AITranslator.ico"
$launcherManifest = Join-Path $sourceDirectory "app.manifest"
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$platformDirectory = Join-Path $rootDirectory "dist\win-x64"
$distDirectory = Join-Path $platformDirectory "AITranslator"
$userDataDirectory = Join-Path $distDirectory "UserData"
$logsDirectory = Join-Path $distDirectory "Logs"
$legacyDataDirectory = Join-Path $distDirectory "Data"
$legacyPlatformUserDataDirectory = Join-Path $platformDirectory "UserData"
$legacyPlatformLogsDirectory = Join-Path $platformDirectory "Logs"
$legacyPlatformDataDirectory = Join-Path $platformDirectory "Data"
$sourceDictionaryDirectory = Join-Path $rootDirectory "dictionary"
$legacyCacheDirectory = Join-Path $rootDirectory "cache"
$buildId = (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + $PID
$backupDirectory = Join-Path $env:TEMP ("AITranslator-build-backup-" + $buildId)
$backupUserDataDirectory = Join-Path $backupDirectory "UserData"
$backupLogsDirectory = Join-Path $backupDirectory "Logs"
$backupPublishedUserDataDirectory = Join-Path $backupDirectory "Published\UserData"
$backupPublishedLogsDirectory = Join-Path $backupDirectory "Published\Logs"
$stagingDirectory = Join-Path $env:TEMP ("AITranslator-publish-" + $buildId)
$stagingApplicationDirectory = Join-Path $stagingDirectory "Dependencies\App"
$stagingLauncher = Join-Path $stagingDirectory "AITranslator.exe"
$publishRootDirectory = $null
$publishedDirectory = $null
$publishStagingDirectory = $null
$publishPreviousDirectory = $null
$shouldPublish = $false

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class ShellChangeNotifier
{
    private const uint UpdateItem = 0x00002000;
    private const uint PathUnicodeAndFlush = 0x00001005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);

    public static void NotifyItemUpdated(string path)
    {
        SHChangeNotify(UpdateItem, PathUnicodeAndFlush, path, IntPtr.Zero);
    }
}
"@

function Assert-FileExists([string] $path, [string] $description)
{
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Missing ${description}: $path"
    }
}

function Get-FileManifest([string] $directory)
{
    $manifest = @{}
    $prefix = $directory.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse -Force)
    {
        $relativePath = $file.FullName.Substring($prefix.Length)
        $manifest[$relativePath] = Get-Sha256 $file.FullName
    }

    return $manifest
}

function Get-Sha256([string] $path)
{
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try
    {
        return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace("-", "")
    }
    finally
    {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

function Assert-ManifestsEqual([hashtable] $expected, [hashtable] $actual)
{
    if ($expected.Count -ne $actual.Count)
    {
        throw "File count differs: backup $($expected.Count), restored $($actual.Count)."
    }

    foreach ($relativePath in $expected.Keys)
    {
        if (-not $actual.ContainsKey($relativePath) -or $actual[$relativePath] -ne $expected[$relativePath])
        {
            throw "File verification failed: $relativePath"
        }
    }
}

function Copy-CacheFiles([string] $databasePath, [string] $destinationDirectory)
{
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    foreach ($suffix in @("", "-wal", "-shm"))
    {
        $sourcePath = $databasePath + $suffix
        if (Test-Path -LiteralPath $sourcePath -PathType Leaf)
        {
            Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $destinationDirectory ("cache.db" + $suffix)) -Force
        }
    }
}

function Resolve-PublishRootDirectory([string] $path)
{
    $path = $path.Trim()
    if ($path.Length -ge 2 -and $path.StartsWith('"') -and $path.EndsWith('"'))
    {
        $path = $path.Substring(1, $path.Length - 2)
    }

    try
    {
        $fullPath = [IO.Path]::GetFullPath($path)
    }
    catch
    {
        throw "Invalid publish directory: $path"
    }

    if (-not (Test-Path -LiteralPath $fullPath -PathType Container))
    {
        throw "Publish directory does not exist: $fullPath"
    }

    return (Get-Item -LiteralPath $fullPath -Force).FullName
}

function Test-DirectoryContains([string] $directory, [string] $candidate)
{
    $directoryPath = [IO.Path]::GetFullPath($directory).TrimEnd('\')
    $candidatePath = [IO.Path]::GetFullPath($candidate).TrimEnd('\')
    return $candidatePath.Equals($directoryPath, [StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith($directoryPath + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-DirectoryWritable([string] $directory)
{
    $probeDirectory = Join-Path $directory (".AITranslator-write-test-" + [Guid]::NewGuid().ToString("N"))
    try
    {
        New-Item -ItemType Directory -Path $probeDirectory | Out-Null
    }
    finally
    {
        if (Test-Path -LiteralPath $probeDirectory)
        {
            Remove-Item -LiteralPath $probeDirectory -Recurse -Force
        }
    }
}

function Complete-Build([string] $message, [string] $publishedPath)
{
    Write-Host $message
    Write-Host "Backup: $backupDirectory"
    if (-not [string]::IsNullOrWhiteSpace($publishedPath))
    {
        Write-Host "Published: $publishedPath"
    }

    if (-not $NoPause)
    {
        $null = Read-Host "Press Enter to exit"
    }
}

function Stop-AITranslator
{
    foreach ($process in Get-Process -Name "AITranslator" -ErrorAction SilentlyContinue)
    {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000))
        {
            $process.Kill()
            $process.WaitForExit(5000)
        }
    }

    if (Get-Process -Name "AITranslator" -ErrorAction SilentlyContinue)
    {
        throw "Unable to stop the running AITranslator process."
    }
}

try
{
    if ([string]::IsNullOrWhiteSpace($publishRootArgument) -and -not $NoPause)
    {
        $publishRootArgument = Read-Host "构建后复制到目标路径中(可空)"
    }

    if (-not [string]::IsNullOrWhiteSpace($publishRootArgument))
    {
        $shouldPublish = $true
        $publishRootDirectory = Resolve-PublishRootDirectory $publishRootArgument
        $publishedDirectory = Join-Path $publishRootDirectory "AITranslator"
        if ((Test-DirectoryContains $distDirectory $publishedDirectory) -or
            (Test-DirectoryContains $publishedDirectory $distDirectory))
        {
            throw "Publish directory overlaps the local build directory: $publishedDirectory"
        }

        if (Test-Path -LiteralPath $publishedDirectory -PathType Leaf)
        {
            throw "Publish target is a file: $publishedDirectory"
        }

        Assert-DirectoryWritable $publishRootDirectory
    }

    $totalSteps = if ($shouldPublish) { 7 } else { 6 }
    Assert-FileExists $projectFile "project file"
    Assert-FileExists $launcherSource "launcher source"
    Assert-FileExists $launcherIcon "application icon"
    Assert-FileExists $launcherManifest "application manifest"
    Assert-FileExists $compiler "Windows C# compiler"
    Assert-FileExists (Join-Path $sourceDictionaryDirectory "ecdict.db") "source dictionary"
    Assert-FileExists (Join-Path $sourceDictionaryDirectory "ECDICT-LICENSE.txt") "dictionary license"
    Write-Host "[1/$totalSteps] Stopping AITranslator..."
    Stop-AITranslator

    Write-Host "[2/$totalSteps] Backing up local data..."
    New-Item -ItemType Directory -Path $backupUserDataDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $backupLogsDirectory -Force | Out-Null
    $excludedDataEntries = @(
        "cache.db",
        "cache.db-wal",
        "cache.db-shm",
        "ecdict.db",
        "ECDICT-LICENSE.txt",
        "Logs"
    )
    $existingUserDataDirectory = $null
    if (Test-Path -LiteralPath $userDataDirectory -PathType Container)
    {
        $existingUserDataDirectory = $userDataDirectory
    }
    elseif (Test-Path -LiteralPath $legacyDataDirectory -PathType Container)
    {
        $existingUserDataDirectory = $legacyDataDirectory
    }
    elseif (Test-Path -LiteralPath $legacyPlatformUserDataDirectory -PathType Container)
    {
        $existingUserDataDirectory = $legacyPlatformUserDataDirectory
    }
    elseif (Test-Path -LiteralPath $legacyPlatformDataDirectory -PathType Container)
    {
        $existingUserDataDirectory = $legacyPlatformDataDirectory
    }

    if ($null -ne $existingUserDataDirectory)
    {
        foreach ($item in Get-ChildItem -LiteralPath $existingUserDataDirectory -Force)
        {
            if ($excludedDataEntries -notcontains $item.Name)
            {
                Copy-Item -LiteralPath $item.FullName -Destination $backupUserDataDirectory -Recurse -Force
            }
        }
    }

    $backupCacheDatabase = Join-Path $backupUserDataDirectory "cache\cache.db"
    if (-not (Test-Path -LiteralPath $backupCacheDatabase -PathType Leaf))
    {
        $cacheCandidates = @(
            (Join-Path $userDataDirectory "cache.db"),
            (Join-Path $legacyDataDirectory "cache.db"),
            (Join-Path $legacyPlatformUserDataDirectory "cache.db"),
            (Join-Path $legacyPlatformDataDirectory "cache.db"),
            (Join-Path $legacyCacheDirectory "cache.db")
        )
        foreach ($cacheCandidate in $cacheCandidates)
        {
            if (Test-Path -LiteralPath $cacheCandidate -PathType Leaf)
            {
                Copy-CacheFiles $cacheCandidate (Join-Path $backupUserDataDirectory "cache")
                break
            }
        }
    }

    $existingLogsDirectory = $logsDirectory
    if (-not (Test-Path -LiteralPath $existingLogsDirectory -PathType Container) -and
        (Test-Path -LiteralPath $legacyPlatformLogsDirectory -PathType Container))
    {
        $existingLogsDirectory = $legacyPlatformLogsDirectory
    }
    elseif (-not (Test-Path -LiteralPath $existingLogsDirectory -PathType Container) -and
        $null -ne $existingUserDataDirectory)
    {
        $existingLogsDirectory = Join-Path $existingUserDataDirectory "Logs"
    }

    if (Test-Path -LiteralPath $existingLogsDirectory -PathType Container)
    {
        foreach ($item in Get-ChildItem -LiteralPath $existingLogsDirectory -Force)
        {
            Copy-Item -LiteralPath $item.FullName -Destination $backupLogsDirectory -Recurse -Force
        }
    }

    $backupUserDataManifest = Get-FileManifest $backupUserDataDirectory
    $backupLogsManifest = Get-FileManifest $backupLogsDirectory

    Write-Host "[3/$totalSteps] Publishing the framework-dependent win-x64 application..."
    New-Item -ItemType Directory -Path $stagingApplicationDirectory -Force | Out-Null
    & dotnet publish $projectFile -c Release -r win-x64 --self-contained false -p:WindowsAppSDKSelfContained=false -o $stagingApplicationDirectory
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Assert-FileExists (Join-Path $stagingApplicationDirectory "AITranslator.exe") "internal executable"
    Assert-FileExists (Join-Path $stagingApplicationDirectory "AITranslator.dll") "application assembly"
    Assert-FileExists (Join-Path $stagingApplicationDirectory "AITranslator.runtimeconfig.json") "runtime configuration"
    Assert-FileExists (Join-Path $stagingApplicationDirectory "Microsoft.WindowsAppRuntime.Bootstrap.dll") "Windows App Runtime bootstrapper"
    foreach ($runtimeFileName in @(
        "clrjit.dll",
        "coreclr.dll",
        "createdump.exe",
        "hostfxr.dll",
        "hostpolicy.dll",
        "System.Private.CoreLib.dll"
    ))
    {
        if (Test-Path -LiteralPath (Join-Path $stagingApplicationDirectory $runtimeFileName) -PathType Leaf)
        {
            throw "Framework-dependent publish contains bundled .NET runtime file: $runtimeFileName"
        }
    }

    foreach ($runtimeDirectoryName in @("host", "shared"))
    {
        if (Test-Path -LiteralPath (Join-Path $stagingApplicationDirectory $runtimeDirectoryName) -PathType Container)
        {
            throw "Framework-dependent publish contains bundled .NET runtime directory: $runtimeDirectoryName"
        }
    }

    foreach ($windowsAppRuntimeFileName in @("DWriteCore.dll", "Microsoft.ui.xaml.dll", "Microsoft.WindowsAppRuntime.dll", "MRM.dll"))
    {
        if (Test-Path -LiteralPath (Join-Path $stagingApplicationDirectory $windowsAppRuntimeFileName) -PathType Leaf)
        {
            throw "Framework-dependent publish contains bundled Windows App Runtime file: $windowsAppRuntimeFileName"
        }
    }

    foreach ($unusedSdkFileName in @("DirectML.dll", "Microsoft.Windows.Widgets.Projection.dll", "onnxruntime.dll"))
    {
        if (Test-Path -LiteralPath (Join-Path $stagingApplicationDirectory $unusedSdkFileName) -PathType Leaf)
        {
            throw "Publish contains an unused Windows App SDK component: $unusedSdkFileName"
        }
    }

    Write-Host "[4/$totalSteps] Building the root launcher and deploying the framework-dependent layout..."
    & $compiler /nologo /target:winexe /optimize+ /platform:anycpu "/win32icon:$launcherIcon" "/win32manifest:$launcherManifest" "/out:$stagingLauncher" $launcherSource
    if ($LASTEXITCODE -ne 0)
    {
        throw "Root launcher compilation failed with exit code $LASTEXITCODE."
    }

    Assert-FileExists $stagingLauncher "root launcher"
    if (Test-Path -LiteralPath $platformDirectory)
    {
        Remove-Item -LiteralPath $platformDirectory -Recurse -Force
    }

    $dependenciesDirectory = Join-Path $distDirectory "Dependencies"
    $applicationDirectory = Join-Path $dependenciesDirectory "App"
    $distDictionaryDirectory = Join-Path $distDirectory "Dictionary"
    New-Item -ItemType Directory -Path $dependenciesDirectory -Force | Out-Null
    Move-Item -LiteralPath $stagingApplicationDirectory -Destination $applicationDirectory
    Copy-Item -LiteralPath $stagingLauncher -Destination (Join-Path $distDirectory "AITranslator.exe") -Force
    New-Item -ItemType Directory -Path $distDictionaryDirectory -Force | Out-Null
    New-Item -ItemType HardLink -Path (Join-Path $distDictionaryDirectory "ecdict.db") -Target (Join-Path $sourceDictionaryDirectory "ecdict.db") | Out-Null
    New-Item -ItemType HardLink -Path (Join-Path $distDictionaryDirectory "ECDICT-LICENSE.txt") -Target (Join-Path $sourceDictionaryDirectory "ECDICT-LICENSE.txt") | Out-Null
    New-Item -ItemType Directory -Path $userDataDirectory -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $backupUserDataDirectory -Force)
    {
        Copy-Item -LiteralPath $item.FullName -Destination $userDataDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $logsDirectory -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $backupLogsDirectory -Force)
    {
        Copy-Item -LiteralPath $item.FullName -Destination $logsDirectory -Recurse -Force
    }

    Write-Host "[5/$totalSteps] Verifying the layout, local data, and hard links..."
    $expectedPlatformEntries = @("AITranslator")
    $actualPlatformEntries = @(Get-ChildItem -LiteralPath $platformDirectory -Force | ForEach-Object Name)
    if (@(Compare-Object $expectedPlatformEntries $actualPlatformEntries).Count -ne 0)
    {
        throw "Unexpected platform root entries: $($actualPlatformEntries -join ', ')"
    }

    $expectedRootEntries = @("Dependencies", "Dictionary", "Logs", "UserData", "AITranslator.exe") | Sort-Object
    $actualRootEntries = @(Get-ChildItem -LiteralPath $distDirectory -Force | ForEach-Object Name | Sort-Object)
    if (@(Compare-Object $expectedRootEntries $actualRootEntries).Count -ne 0)
    {
        throw "Unexpected publish root entries: $($actualRootEntries -join ', ')"
    }

    $restoredUserDataManifest = Get-FileManifest $userDataDirectory
    Assert-ManifestsEqual $backupUserDataManifest $restoredUserDataManifest
    $restoredLogsManifest = Get-FileManifest $logsDirectory
    Assert-ManifestsEqual $backupLogsManifest $restoredLogsManifest
    foreach ($name in @("ecdict.db", "ECDICT-LICENSE.txt"))
    {
        $sourcePath = Join-Path $sourceDictionaryDirectory $name
        $distPath = Join-Path $distDictionaryDirectory $name
        if ((Get-Item -LiteralPath $distPath).LinkType -ne "HardLink")
        {
            throw "Published dictionary file is not a hard link: $distPath"
        }

        if ((Get-Sha256 $sourcePath) -ne (Get-Sha256 $distPath))
        {
            throw "Published dictionary file hash differs: $distPath"
        }
    }

    $publishedAssetsDirectory = Join-Path $applicationDirectory "Assets"
    foreach ($name in @("AITranslator.ico", "AITranslator.png", "AITranslator.svg"))
    {
        $sourcePath = Join-Path $sourceDirectory "Assets\$name"
        $publishedPath = Join-Path $publishedAssetsDirectory $name
        Assert-FileExists $publishedPath "published icon asset"
        if ((Get-Sha256 $sourcePath) -ne (Get-Sha256 $publishedPath))
        {
            throw "Published icon asset hash differs: $publishedPath"
        }
    }

    foreach ($executablePath in @((Join-Path $distDirectory "AITranslator.exe"), (Join-Path $applicationDirectory "AITranslator.exe")))
    {
        [ShellChangeNotifier]::NotifyItemUpdated($executablePath)
    }

    if (Test-Path -LiteralPath $legacyCacheDirectory)
    {
        Remove-Item -LiteralPath $legacyCacheDirectory -Recurse -Force
    }

    if (Test-Path -LiteralPath $stagingDirectory)
    {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }

    if (-not $shouldPublish)
    {
        Complete-Build "[6/6] Build completed." $null
        exit 0
    }

    Write-Host "[6/7] Publishing to $publishedDirectory..."
    $publishStagingDirectory = Join-Path $publishRootDirectory (".AITranslator-publish-" + $buildId)
    $publishPreviousDirectory = Join-Path $publishRootDirectory (".AITranslator-previous-" + $buildId)
    if ((Test-Path -LiteralPath $publishStagingDirectory) -or
        (Test-Path -LiteralPath $publishPreviousDirectory))
    {
        throw "Publish staging path already exists."
    }

    $publishedUserDataWasBackedUp = $false
    $publishedLogsWereBackedUp = $false
    $publishedUserDataManifest = @{}
    $publishedLogsManifest = @{}
    if (Test-Path -LiteralPath $publishedDirectory -PathType Container)
    {
        New-Item -ItemType Directory -Path $backupPublishedUserDataDirectory -Force | Out-Null
        New-Item -ItemType Directory -Path $backupPublishedLogsDirectory -Force | Out-Null

        $publishedUserDataDirectory = Join-Path $publishedDirectory "UserData"
        $publishedLegacyDataDirectory = Join-Path $publishedDirectory "Data"
        $existingPublishedUserDataDirectory = $null
        if (Test-Path -LiteralPath $publishedUserDataDirectory -PathType Container)
        {
            $existingPublishedUserDataDirectory = $publishedUserDataDirectory
        }
        elseif (Test-Path -LiteralPath $publishedLegacyDataDirectory -PathType Container)
        {
            $existingPublishedUserDataDirectory = $publishedLegacyDataDirectory
        }

        if ($null -ne $existingPublishedUserDataDirectory)
        {
            $publishedUserDataWasBackedUp = $true
            foreach ($item in Get-ChildItem -LiteralPath $existingPublishedUserDataDirectory -Force)
            {
                if ($excludedDataEntries -notcontains $item.Name)
                {
                    Copy-Item -LiteralPath $item.FullName -Destination $backupPublishedUserDataDirectory -Recurse -Force
                }
            }

            $backupPublishedCacheDatabase = Join-Path $backupPublishedUserDataDirectory "cache\cache.db"
            if (-not (Test-Path -LiteralPath $backupPublishedCacheDatabase -PathType Leaf))
            {
                foreach ($cacheCandidate in @(
                    (Join-Path $publishedUserDataDirectory "cache.db"),
                    (Join-Path $publishedLegacyDataDirectory "cache.db")
                ))
                {
                    if (Test-Path -LiteralPath $cacheCandidate -PathType Leaf)
                    {
                        Copy-CacheFiles $cacheCandidate (Join-Path $backupPublishedUserDataDirectory "cache")
                        break
                    }
                }
            }
        }

        $existingPublishedLogsDirectory = Join-Path $publishedDirectory "Logs"
        if (-not (Test-Path -LiteralPath $existingPublishedLogsDirectory -PathType Container) -and
            $null -ne $existingPublishedUserDataDirectory)
        {
            $existingPublishedLogsDirectory = Join-Path $existingPublishedUserDataDirectory "Logs"
        }

        if (Test-Path -LiteralPath $existingPublishedLogsDirectory -PathType Container)
        {
            $publishedLogsWereBackedUp = $true
            foreach ($item in Get-ChildItem -LiteralPath $existingPublishedLogsDirectory -Force)
            {
                Copy-Item -LiteralPath $item.FullName -Destination $backupPublishedLogsDirectory -Recurse -Force
            }
        }

        $publishedUserDataManifest = Get-FileManifest $backupPublishedUserDataDirectory
        $publishedLogsManifest = Get-FileManifest $backupPublishedLogsDirectory
    }

    New-Item -ItemType Directory -Path $publishStagingDirectory | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $distDirectory -Force)
    {
        Copy-Item -LiteralPath $item.FullName -Destination $publishStagingDirectory -Recurse -Force
    }

    $stagedPublishedUserDataDirectory = Join-Path $publishStagingDirectory "UserData"
    if ($publishedUserDataWasBackedUp)
    {
        if (Test-Path -LiteralPath $stagedPublishedUserDataDirectory)
        {
            Remove-Item -LiteralPath $stagedPublishedUserDataDirectory -Recurse -Force
        }

        New-Item -ItemType Directory -Path $stagedPublishedUserDataDirectory | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $backupPublishedUserDataDirectory -Force)
        {
            Copy-Item -LiteralPath $item.FullName -Destination $stagedPublishedUserDataDirectory -Recurse -Force
        }

        Assert-ManifestsEqual $publishedUserDataManifest (Get-FileManifest $stagedPublishedUserDataDirectory)
    }

    $stagedPublishedLogsDirectory = Join-Path $publishStagingDirectory "Logs"
    if ($publishedLogsWereBackedUp)
    {
        if (Test-Path -LiteralPath $stagedPublishedLogsDirectory)
        {
            Remove-Item -LiteralPath $stagedPublishedLogsDirectory -Recurse -Force
        }

        New-Item -ItemType Directory -Path $stagedPublishedLogsDirectory | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $backupPublishedLogsDirectory -Force)
        {
            Copy-Item -LiteralPath $item.FullName -Destination $stagedPublishedLogsDirectory -Recurse -Force
        }

        Assert-ManifestsEqual $publishedLogsManifest (Get-FileManifest $stagedPublishedLogsDirectory)
    }

    Assert-FileExists (Join-Path $publishStagingDirectory "AITranslator.exe") "staged published launcher"
    Assert-FileExists (Join-Path $publishStagingDirectory "Dependencies\App\AITranslator.dll") "staged published application assembly"
    $hadPublishedDirectory = Test-Path -LiteralPath $publishedDirectory -PathType Container
    if ($hadPublishedDirectory)
    {
        Move-Item -LiteralPath $publishedDirectory -Destination $publishPreviousDirectory
    }

    try
    {
        Move-Item -LiteralPath $publishStagingDirectory -Destination $publishedDirectory
        if ($publishedUserDataWasBackedUp)
        {
            Assert-ManifestsEqual $publishedUserDataManifest (Get-FileManifest (Join-Path $publishedDirectory "UserData"))
        }

        if ($publishedLogsWereBackedUp)
        {
            Assert-ManifestsEqual $publishedLogsManifest (Get-FileManifest (Join-Path $publishedDirectory "Logs"))
        }
    }
    catch
    {
        if (Test-Path -LiteralPath $publishedDirectory)
        {
            Remove-Item -LiteralPath $publishedDirectory -Recurse -Force
        }

        if ($hadPublishedDirectory -and (Test-Path -LiteralPath $publishPreviousDirectory -PathType Container))
        {
            Move-Item -LiteralPath $publishPreviousDirectory -Destination $publishedDirectory
        }

        throw
    }

    if ($hadPublishedDirectory)
    {
        Remove-Item -LiteralPath $publishPreviousDirectory -Recurse -Force
    }

    Complete-Build "[7/7] Build and publish completed." $publishedDirectory
    exit 0
}
catch
{
    Write-Host "[FAILED] $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Local data backup: $backupDirectory"
    Write-Host "Publish staging: $stagingDirectory"
    if (-not $NoPause)
    {
        $null = Read-Host "Press Enter to exit"
    }

    exit 1
}
