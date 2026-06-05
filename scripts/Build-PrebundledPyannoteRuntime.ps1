param(
    [Parameter(Mandatory = $true)]
    [string]$WhisperSmallModelPath,

    [Parameter(Mandatory = $true)]
    [string]$WhisperCliRuntimePath,

    [Parameter(Mandatory = $true)]
    [string]$PyannoteModelPath,

    [string]$PyannoteRunnerScriptPath = "",

    [Parameter(Mandatory = $true)]
    [string]$PythonRuntimePath,

    [string]$DestinationRoot = (Join-Path $PSScriptRoot "..\\assets\\prebuilt"),

    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-ExistingPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "$Label path does not exist: $Path"
    }

    return $resolved.Path
}

function Copy-PayloadItem {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $destinationParent = Split-Path -Parent $DestinationPath
    if (-not (Test-Path -LiteralPath $destinationParent)) {
        New-Item -ItemType Directory -Path $destinationParent | Out-Null
    }

    if (Test-Path -LiteralPath $SourcePath -PathType Container) {
        Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Recurse -Force
        return
    }

    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
}

function Assert-CriticalFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Expected $Label at '$Path'."
    }
}

function Remove-IfPresent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Assert-NotPresent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (Test-Path -LiteralPath $Path) {
        throw "Unexpected redundant $Label found at '$Path'."
    }
}

function Remove-DirectoriesByName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string[]]$Names
    )

    foreach ($name in $Names) {
        Get-ChildItem -LiteralPath $RootPath -Directory -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ieq $name } |
            ForEach-Object { Remove-IfPresent -Path $_.FullName }
    }
}

$whisperSmallModelPath = Resolve-ExistingPath -Path $WhisperSmallModelPath -Label "Whisper small model"
$whisperCliRuntimePath = Resolve-ExistingPath -Path $WhisperCliRuntimePath -Label "Whisper CLI runtime"
$pyannoteModelPath = Resolve-ExistingPath -Path $PyannoteModelPath -Label "Pyannote model"
$resolvedPyannoteRunnerScriptPath = $null
if ([string]::IsNullOrWhiteSpace($PyannoteRunnerScriptPath)) {
    $inferredRunnerScriptPath = Join-Path (Split-Path -Parent $pyannoteModelPath) "run_community_diarization.py"
    $resolvedPyannoteRunnerScriptPath = Resolve-ExistingPath -Path $inferredRunnerScriptPath -Label "Pyannote runner script"
}
else {
    $resolvedPyannoteRunnerScriptPath = Resolve-ExistingPath -Path $PyannoteRunnerScriptPath -Label "Pyannote runner script"
}
$pythonRuntimePath = Resolve-ExistingPath -Path $PythonRuntimePath -Label "Python runtime"
if ([System.IO.Path]::IsPathRooted($DestinationRoot)) {
    $destinationRoot = [System.IO.Path]::GetFullPath($DestinationRoot)
}
else {
    $destinationRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $DestinationRoot))
}

if ($Clean -and (Test-Path -LiteralPath $destinationRoot)) {
    Remove-Item -LiteralPath $destinationRoot -Recurse -Force
}

$payloadMap = @(
    @{
        Source = $whisperSmallModelPath
        Destination = (Join-Path $destinationRoot "models\\ggml-small.bin")
    },
    @{
        Source = $whisperCliRuntimePath
        Destination = (Join-Path $destinationRoot "tools\\whisper.cpp\\win-x64")
    },
    @{
        Source = $pyannoteModelPath
        Destination = (Join-Path $destinationRoot "pyannote\\speaker-diarization-community-1")
    },
    @{
        Source = $pythonRuntimePath
        Destination = (Join-Path $destinationRoot "python\\win-x64")
    }
)

foreach ($item in $payloadMap) {
    Copy-PayloadItem -SourcePath $item.Source -DestinationPath $item.Destination
}

$packagedPyannoteRoot = Join-Path $destinationRoot "pyannote\\speaker-diarization-community-1"
$packagedPythonRoot = Join-Path $destinationRoot "python\\win-x64"
$packagedWhisperRoot = Join-Path $destinationRoot "tools\\whisper.cpp\\win-x64"

Copy-PayloadItem `
    -SourcePath $resolvedPyannoteRunnerScriptPath `
    -DestinationPath (Join-Path $packagedPyannoteRoot "run_community_diarization.py")

# Strip obvious non-runtime Python packaging payload to avoid unnecessary package size.
& (Join-Path $PSScriptRoot "Cleanup-BundledPythonRuntime.ps1") -PythonRuntimeRoot $packagedPythonRoot

# Strip non-runtime development payload to avoid package path-length failures and unnecessary package size.
Remove-IfPresent -Path (Join-Path $packagedPythonRoot "Include")
Remove-IfPresent -Path (Join-Path $packagedPythonRoot "share\\man")
Remove-IfPresent -Path (Join-Path $packagedPythonRoot "Lib\\site-packages\\torch\\include")
Remove-IfPresent -Path (Join-Path $packagedPyannoteRoot "speaker-diarization-community-1")
Remove-IfPresent -Path (Join-Path $packagedWhisperRoot "win-x64")

$criticalFiles = @(
    @{
        Path = (Join-Path $destinationRoot "models\\ggml-small.bin")
        Label = "bundled whisper-small model"
    },
    @{
        Path = (Join-Path $destinationRoot "tools\\whisper.cpp\\win-x64\\Release\\whisper-cli.exe")
        Label = "bundled whisper.cpp CLI executable"
    },
    @{
        Path = (Join-Path $destinationRoot "pyannote\\speaker-diarization-community-1\\run_community_diarization.py")
        Label = "bundled pyannote runner script"
    },
    @{
        Path = (Join-Path $destinationRoot "python\\win-x64\\python.exe")
        Label = "bundled Python runtime executable"
    }
)

foreach ($criticalFile in $criticalFiles) {
    Assert-CriticalFile -Path $criticalFile.Path -Label $criticalFile.Label
}

Assert-NotPresent -Path (Join-Path $packagedPyannoteRoot "speaker-diarization-community-1") -Label "nested pyannote mirror directory"
Assert-NotPresent -Path (Join-Path $packagedWhisperRoot "win-x64") -Label "nested whisper.cpp mirror directory"

Write-Host "Bundled engine payload staged successfully."
Write-Host "Destination: $destinationRoot"
