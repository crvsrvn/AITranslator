[CmdletBinding()]
param(
    [switch] $NoPause,
    [switch] $NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sourceDirectory = $PSScriptRoot
$rootDirectory = [IO.Path]::GetFullPath((Join-Path $sourceDirectory ".."))
$projectFile = Join-Path $sourceDirectory "AITranslator.csproj"
$launcherSource = Join-Path $sourceDirectory "Launcher\Program.cs"
$launcherIcon = Join-Path $sourceDirectory "Assets\AITranslator.ico"
$launcherManifest = Join-Path $sourceDirectory "app.manifest"
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$distDirectory = Join-Path $rootDirectory "dist\win-x64"
$userDataDirectory = Join-Path $distDirectory "UserData"
$logsDirectory = Join-Path $distDirectory "Logs"
$legacyDataDirectory = Join-Path $distDirectory "Data"
$sourceDictionaryDirectory = Join-Path $rootDirectory "dictionary"
$legacyCacheDirectory = Join-Path $rootDirectory "cache"
$buildId = (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + $PID
$backupDirectory = Join-Path $env:TEMP ("AITranslator-build-backup-" + $buildId)
$backupUserDataDirectory = Join-Path $backupDirectory "UserData"
$backupLogsDirectory = Join-Path $backupDirectory "Logs"
$stagingDirectory = Join-Path $env:TEMP ("AITranslator-publish-" + $buildId)
$stagingApplicationDirectory = Join-Path $stagingDirectory "Dependencies\App"
$stagingLauncher = Join-Path $stagingDirectory "AITranslator.exe"

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
    Assert-FileExists $projectFile "project file"
    Assert-FileExists $launcherSource "launcher source"
    Assert-FileExists $launcherIcon "application icon"
    Assert-FileExists $launcherManifest "application manifest"
    Assert-FileExists $compiler "Windows C# compiler"
    Assert-FileExists (Join-Path $sourceDictionaryDirectory "ecdict.db") "source dictionary"
    Assert-FileExists (Join-Path $sourceDictionaryDirectory "ECDICT-LICENSE.txt") "dictionary license"
    Write-Host "[1/6] Stopping AITranslator..."
    Stop-AITranslator

    Write-Host "[2/6] Backing up local data..."
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

    Write-Host "[3/6] Publishing the framework-dependent win-x64 application..."
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

    Write-Host "[4/6] Building the root launcher and deploying the framework-dependent layout..."
    & $compiler /nologo /target:winexe /optimize+ /platform:anycpu "/win32icon:$launcherIcon" "/win32manifest:$launcherManifest" "/out:$stagingLauncher" $launcherSource
    if ($LASTEXITCODE -ne 0)
    {
        throw "Root launcher compilation failed with exit code $LASTEXITCODE."
    }

    Assert-FileExists $stagingLauncher "root launcher"
    if (Test-Path -LiteralPath $distDirectory)
    {
        Remove-Item -LiteralPath $distDirectory -Recurse -Force
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

    Write-Host "[5/6] Verifying the layout, local data, and hard links..."
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

    if (Test-Path -LiteralPath $legacyCacheDirectory)
    {
        Remove-Item -LiteralPath $legacyCacheDirectory -Recurse -Force
    }

    if (Test-Path -LiteralPath $stagingDirectory)
    {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }

    Write-Host "[6/6] Build completed."
    if (-not $NoStart)
    {
        Start-Process -FilePath (Join-Path $distDirectory "AITranslator.exe") -WorkingDirectory $distDirectory
        Write-Host "Started: $(Join-Path $distDirectory 'AITranslator.exe')"
    }

    Write-Host "Backup: $backupDirectory"
    if (-not $NoPause)
    {
        $null = Read-Host "Press Enter to exit"
    }

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
