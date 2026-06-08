param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot ".."),
    [string]$SourceRuntimePath = "",
    [string]$OutputPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRootPath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RepoRoot).Path)

if ([string]::IsNullOrWhiteSpace($SourceRuntimePath)) {
    $SourceRuntimePath = Join-Path $repoRootPath "assets\prebuilt\python\win-x64"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRootPath "AudioScript.Package\AppPackages\AudioScript.PyannotePythonRuntime.win-x64.zip"
}

$sourcePath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $SourceRuntimePath).Path)
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutputPath

if (-not (Test-Path -LiteralPath (Join-Path $sourcePath "python.exe"))) {
    throw "Pyannote Python runtime source is missing python.exe: '$sourcePath'."
}

if (-not (Test-Path -LiteralPath (Join-Path $sourcePath "Lib\site-packages\pyannote\audio"))) {
    throw "Pyannote Python runtime source is missing pyannote.audio: '$sourcePath'."
}

if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

if (Test-Path -LiteralPath $resolvedOutputPath) {
    Remove-Item -LiteralPath $resolvedOutputPath -Force
}

$stagingPath = Join-Path ([System.IO.Path]::GetTempPath()) ("AudioScript-diarization-runtime-" + [Guid]::NewGuid().ToString("N"))
$compressionLevel = [System.IO.Compression.CompressionLevel]::Optimal
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    Copy-Item -LiteralPath $sourcePath -Destination $stagingPath -Recurse

    $nativeCompatibilityFiles = @(
        @{
            Source = (Join-Path $stagingPath "Lib\site-packages\sklearn\.libs\msvcp140.dll")
            Destination = (Join-Path $stagingPath "msvcp140.dll")
        },
        @{
            Source = (Join-Path $stagingPath "Lib\site-packages\sklearn\.libs\vcomp140.dll")
            Destination = (Join-Path $stagingPath "vcomp140.dll")
        }
    )

    foreach ($file in $nativeCompatibilityFiles) {
        if (-not (Test-Path -LiteralPath $file.Source)) {
            throw "Pyannote Python runtime source is missing native compatibility file '$($file.Source)'."
        }

        Copy-Item -LiteralPath $file.Source -Destination $file.Destination -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingPath, $resolvedOutputPath, $compressionLevel, $false)
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}

Write-Host "Diarization runtime archive created."
Write-Host "Source: $sourcePath"
Write-Host "Output: $resolvedOutputPath"
