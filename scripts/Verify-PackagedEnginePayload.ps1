param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot ".."),
    [string]$PackageArtifactRoot = "",
    [switch]$SkipPackageArtifactCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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

function Test-ArchiveEntryPattern {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    $normalizedPattern = $Pattern.Replace('\', '/')
    return [bool]($Archive.Entries | Where-Object {
        $_.FullName.Replace('\', '/') -like "*$normalizedPattern*"
    })
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
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("AudioScript-package-verify-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $outerArchive = [System.IO.Compression.ZipFile]::OpenRead($ArtifactPath)
    try {
        $bundleEntry = $outerArchive.Entries | Where-Object { $_.FullName -like "*.msixbundle" } | Select-Object -First 1
        if (-not $bundleEntry) {
            $Failures.Add("Package artifact '$ArtifactPath' does not contain an .msixbundle payload.")
            return
        }

        $bundlePath = Join-Path $tempRoot $bundleEntry.Name
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($bundleEntry, $bundlePath, $true)
        $bundleArchive = [System.IO.Compression.ZipFile]::OpenRead($bundlePath)
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

            $msixPath = Join-Path $tempRoot $msixEntry.Name
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($msixEntry, $msixPath, $true)
            $msixArchive = [System.IO.Compression.ZipFile]::OpenRead($msixPath)
            try {
                $forbiddenPaths = @(
                    "assets/prebuilt/pyannote",
                    "Assets/prebuilt/pyannote",
                    "assets/prebuilt/python",
                    "Assets/prebuilt/python",
                    "speaker-diarization-community-1/speaker-diarization-community-1",
                    "tools/whisper.cpp/win-x64/win-x64"
                )

                foreach ($forbiddenPath in $forbiddenPaths) {
                    if (Test-ArchiveEntryPattern -Archive $msixArchive -Pattern $forbiddenPath) {
                        $Failures.Add("Package artifact '$ArtifactPath' contains redundant or excluded payload path '$forbiddenPath'.")
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
                    if (Test-ArchiveEntryPattern -Archive $msixArchive -Pattern $pattern) {
                        $Failures.Add("Package artifact '$ArtifactPath' still contains removable Python payload matching '$pattern'.")
                    }
                }
            }
            finally {
                $msixArchive.Dispose()
            }
        }
        finally {
            $bundleArchive.Dispose()
        }
    }
    finally {
        $outerArchive.Dispose()
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
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
    (Join-Path $prebuiltRoot "tools\\whisper.cpp\\win-x64\\Release\\whisper-cli.exe")
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

if (-not $SkipPackageArtifactCheck) {
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
}

Write-Host "Packaged engine payload verification succeeded."
