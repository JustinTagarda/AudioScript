param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot ".."),
    [string]$PackageArtifactRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-PythonImports {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PythonPath
    )

    $probeScript = @"
import importlib
for module_name in ("torch", "torchaudio", "pyannote.audio"):
    importlib.import_module(module_name)
print("ok")
"@

    $output = $probeScript | & $PythonPath -
    if ($LASTEXITCODE -ne 0) {
        throw "Bundled Python import probe failed using '$PythonPath'."
    }

    if (($output | Out-String).Trim() -ne "ok") {
        throw "Bundled Python import probe returned unexpected output."
    }
}

function Resolve-LatestPackageArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if (-not (Test-Path -LiteralPath $RootPath)) {
        throw "Package artifact root not found at '$RootPath'."
    }

    $artifact = Get-ChildItem -LiteralPath $RootPath -File -Filter "*.msixupload" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $artifact) {
        throw "No .msixupload package artifact was found under '$RootPath'."
    }

    return $artifact.FullName
}

function Test-PackageArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArtifactPath,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]]$Failures
    )

    if (-not (Test-Path -LiteralPath $ArtifactPath)) {
        $Failures.Add("Package artifact was not found: '$ArtifactPath'.")
        return
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $outerArchive = [System.IO.Compression.ZipFile]::OpenRead($ArtifactPath)
    try {
        $bundleEntry = $outerArchive.Entries | Where-Object { $_.FullName -like "*.msixbundle" } | Select-Object -First 1
        if (-not $bundleEntry) {
            $Failures.Add("Package artifact '$ArtifactPath' does not contain an .msixbundle payload.")
            return
        }

        $bundleStream = $bundleEntry.Open()
        try {
            $bundleBytes = New-Object System.IO.MemoryStream
            try {
                $bundleStream.CopyTo($bundleBytes)
                $bundleBytes.Position = 0
                $bundleArchive = New-Object System.IO.Compression.ZipArchive($bundleBytes, [System.IO.Compression.ZipArchiveMode]::Read, $false)
                try {
                    $bundleManifestEntry = $bundleArchive.Entries | Where-Object { $_.FullName -eq "AppxMetadata/AppxBundleManifest.xml" } | Select-Object -First 1
                    if (-not $bundleManifestEntry) {
                        $Failures.Add("Package artifact '$ArtifactPath' is missing AppxMetadata/AppxBundleManifest.xml.")
                        return
                    }

                    $bundleReader = New-Object System.IO.StreamReader($bundleManifestEntry.Open())
                    try {
                        [xml]$bundleManifest = $bundleReader.ReadToEnd()
                    }
                    finally {
                        $bundleReader.Dispose()
                    }

                    $packages = @($bundleManifest.Bundle.Packages.Package)
                    if ($packages.Count -ne 1) {
                        $Failures.Add("Package artifact '$ArtifactPath' must contain exactly one package in the bundle. Found $($packages.Count).")
                    }
                    elseif ($packages[0].Architecture -ne "x64") {
                        $Failures.Add("Package artifact '$ArtifactPath' must contain only an x64 package. Found '$($packages[0].Architecture)'.")
                    }

                    $msixEntry = $bundleArchive.Entries | Where-Object { $_.FullName -like "*.msix" } | Select-Object -First 1
                    if (-not $msixEntry) {
                        $Failures.Add("Package artifact '$ArtifactPath' does not contain an inner .msix payload.")
                        return
                    }

                    $msixStream = $msixEntry.Open()
                    try {
                        $msixBytes = New-Object System.IO.MemoryStream
                        try {
                            $msixStream.CopyTo($msixBytes)
                            $msixBytes.Position = 0
                            $msixArchive = New-Object System.IO.Compression.ZipArchive($msixBytes, [System.IO.Compression.ZipArchiveMode]::Read, $false)
                            try {
                                $forbiddenPaths = @(
                                    "speaker-diarization-community-1/speaker-diarization-community-1",
                                    "tools/whisper.cpp/win-x64/win-x64"
                                )

                                foreach ($forbiddenPath in $forbiddenPaths) {
                                    if ($msixArchive.Entries | Where-Object { $_.FullName -like "*$forbiddenPath*" }) {
                                        $Failures.Add("Package artifact '$ArtifactPath' contains redundant nested payload path '$forbiddenPath'.")
                                    }
                                }

                                $safeCleanupPatterns = @(
                                    "Lib/site-packages/__pycache__/*",
                                    "Lib/site-packages/*/__pycache__/*",
                                    "Lib/site-packages/*/tests/*",
                                    "Lib/site-packages/*/test/*",
                                    "Lib/site-packages/*/docs/*",
                                    "Lib/site-packages/*/doc/*",
                                    "Lib/site-packages/*/examples/*",
                                    "Lib/site-packages/*/sample/*",
                                    "Lib/site-packages/*/samples/*",
                                    "Lib/site-packages/pip/*",
                                    "Lib/site-packages/pip-*.dist-info/*",
                                    "Scripts/pip*.exe",
                                    "get-pip.py",
                                    "*.pyc",
                                    "*.pyo"
                                )

                                foreach ($pattern in $safeCleanupPatterns) {
                                    if ($msixArchive.Entries | Where-Object { $_.FullName -like "*$pattern*" }) {
                                        $Failures.Add("Package artifact '$ArtifactPath' still contains removable Python payload matching '$pattern'.")
                                    }
                                }
                            }
                            finally {
                                $msixArchive.Dispose()
                            }
                        }
                        finally {
                            $msixBytes.Dispose()
                        }
                    }
                    finally {
                        $msixStream.Dispose()
                    }
                }
                finally {
                    $bundleArchive.Dispose()
                }
            }
            finally {
                $bundleBytes.Dispose()
            }
        }
        finally {
            $bundleStream.Dispose()
        }
    }
    finally {
        $outerArchive.Dispose()
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RepoRoot).Path)
$manifestPath = Join-Path $repoRoot "assets\\bootstrap\\asset-manifest.json"
$prebuiltRoot = Join-Path $repoRoot "assets\\prebuilt"

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Asset manifest not found at '$manifestPath'."
}

if (-not (Test-Path -LiteralPath $prebuiltRoot)) {
    throw "Prebuilt asset root not found at '$prebuiltRoot'."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$packagedAssets = @(
    $manifest.assets | Where-Object {
        $_.PSObject.Properties["deliveryMode"] -and $_.deliveryMode -eq "PackagedRequired"
    }
)

if ($packagedAssets.Count -eq 0) {
    throw "No PackagedRequired assets are defined in the asset manifest."
}

$failures = New-Object System.Collections.Generic.List[string]

foreach ($asset in $packagedAssets) {
    if ([string]::IsNullOrWhiteSpace($asset.packagedSourceRelativePath)) {
        $failures.Add("Asset '$($asset.id)' is missing packagedSourceRelativePath.")
        continue
    }

    $expectedPath = Join-Path $repoRoot $asset.packagedSourceRelativePath
    if (-not (Test-Path -LiteralPath $expectedPath)) {
        $failures.Add("Packaged asset '$($asset.id)' is missing at '$expectedPath'.")
    }
}

$criticalPaths = @(
    (Join-Path $prebuiltRoot "models\\ggml-small.bin"),
    (Join-Path $prebuiltRoot "tools\\whisper.cpp\\win-x64\\Release\\whisper-cli.exe"),
    (Join-Path $prebuiltRoot "pyannote\\speaker-diarization-community-1\\run_community_diarization.py"),
    (Join-Path $prebuiltRoot "python\\win-x64\\python.exe")
)

$forbiddenPaths = @(
    (Join-Path $prebuiltRoot "pyannote\\speaker-diarization-community-1\\speaker-diarization-community-1"),
    (Join-Path $prebuiltRoot "tools\\whisper.cpp\\win-x64\\win-x64")
)

foreach ($criticalPath in $criticalPaths) {
    if (-not (Test-Path -LiteralPath $criticalPath)) {
        $failures.Add("Critical packaged runtime file is missing: '$criticalPath'.")
    }
}

foreach ($forbiddenPath in $forbiddenPaths) {
    if (Test-Path -LiteralPath $forbiddenPath) {
        $failures.Add("Redundant nested payload directory must not exist: '$forbiddenPath'.")
    }
}

if ($failures.Count -gt 0) {
    $message = ($failures | ForEach-Object { "- $_" }) -join [Environment]::NewLine
    throw "Packaged engine payload verification failed:$([Environment]::NewLine)$message"
}

$packageArtifactRoot = if ([string]::IsNullOrWhiteSpace($PackageArtifactRoot)) {
    Join-Path $repoRoot "AudioScript.Package\\AppPackages"
}
else {
    [System.IO.Path]::GetFullPath($PackageArtifactRoot)
}

Test-PackageArtifact -ArtifactPath (Resolve-LatestPackageArtifact -RootPath $packageArtifactRoot) -Failures (,$failures)

if ($failures.Count -gt 0) {
    $message = ($failures | ForEach-Object { "- $_" }) -join [Environment]::NewLine
    throw "Packaged engine payload verification failed:$([Environment]::NewLine)$message"
}

Test-PythonImports -PythonPath (Join-Path $prebuiltRoot "python\\win-x64\\python.exe")

Write-Host "Packaged engine payload verification succeeded."
