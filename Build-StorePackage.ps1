[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$VsMsBuildPath = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
    [string]$WindowsSdkBin = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64",
    [string]$OutputRoot = "AudioScript.Package\AppPackages\store-bundle-self-contained"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "AudioScript.csproj"
$manifestPath = Join-Path $repoRoot "AudioScript.Package\Package.appxmanifest"
$packageAssetsPath = Join-Path $repoRoot "AudioScript.Package\assets"
$makeAppxPath = Join-Path $WindowsSdkBin "makeappx.exe"

if (-not (Test-Path $projectPath)) {
    throw "Project file not found: $projectPath"
}

if (-not (Test-Path $manifestPath)) {
    throw "Package manifest not found: $manifestPath"
}

if (-not (Test-Path $makeAppxPath)) {
    throw "makeappx.exe not found: $makeAppxPath"
}

if (-not (Test-Path $VsMsBuildPath)) {
    throw "Visual Studio 2026 MSBuild not found: $VsMsBuildPath"
}

[xml]$projectXml = Get-Content $projectPath
$version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version not found in $projectPath"
}

$resolvedOutputRoot = Join-Path $repoRoot $OutputRoot
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$workingRoot = Join-Path $resolvedOutputRoot ("v{0}-{1}" -f $version, $timestamp)
$publishX64 = Join-Path $workingRoot "publish-x64"
$publishArm64 = Join-Path $workingRoot "publish-arm64"
$layoutX64 = Join-Path $workingRoot "layout-x64"
$layoutArm64 = Join-Path $workingRoot "layout-arm64"
$bundleInput = Join-Path $workingRoot "bundle-input"
$packagesDir = Join-Path $workingRoot "packages"

New-Item -ItemType Directory -Force -Path $publishX64, $publishArm64, $layoutX64, $layoutArm64, $bundleInput, $packagesDir | Out-Null

function Invoke-StorePublish {
    param(
        [Parameter(Mandatory = $true)] [string]$RuntimeIdentifier,
        [Parameter(Mandatory = $true)] [string]$OutputPath
    )

    $normalizedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    if (-not $normalizedOutputPath.EndsWith('\')) {
        $normalizedOutputPath += '\'
    }

    & $VsMsBuildPath $projectPath `
        /t:Publish `
        /p:Configuration=$Configuration `
        /p:RuntimeIdentifier=$RuntimeIdentifier `
        /p:PublishDir=$normalizedOutputPath `
        /p:SelfContained=true `
        /p:StoreSelfContained=true `
        /p:DebugType=None `
        /p:DebugSymbols=false `
        /m

    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild publish failed for runtime '$RuntimeIdentifier' with exit code $LASTEXITCODE"
    }
}

function Set-ManifestArchitecture {
    param(
        [Parameter(Mandatory = $true)] [string]$SourceManifestPath,
        [Parameter(Mandatory = $true)] [string]$DestinationManifestPath,
        [Parameter(Mandatory = $true)] [string]$Architecture
    )

    [xml]$manifest = Get-Content $SourceManifestPath
    $namespaceManager = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
    $namespaceManager.AddNamespace("appx", $manifest.DocumentElement.NamespaceURI)
    $identity = $manifest.SelectSingleNode("//appx:Identity", $namespaceManager)

    if ($null -eq $identity) {
        throw "Identity element not found in package manifest."
    }

    $identity.SetAttribute("ProcessorArchitecture", $Architecture)
    $manifest.Save($DestinationManifestPath)
}

function Copy-DirectoryRobust {
    param(
        [Parameter(Mandatory = $true)] [string]$SourcePath,
        [Parameter(Mandatory = $true)] [string]$DestinationPath
    )

    New-Item -ItemType Directory -Force -Path $DestinationPath | Out-Null
    & robocopy $SourcePath $DestinationPath /E /NFL /NDL /NJH /NJS /NP | Out-Null
    $exitCode = $LASTEXITCODE
    if ($exitCode -ge 8) {
        throw "robocopy failed from '$SourcePath' to '$DestinationPath' with exit code $exitCode"
    }
}

function Remove-FilesByPatterns {
    param(
        [Parameter(Mandatory = $true)] [string]$RootPath,
        [Parameter(Mandatory = $true)] [string[]]$Patterns
    )

    foreach ($pattern in $Patterns) {
        Get-ChildItem -Path $RootPath -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
            ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
            }
    }
}

function Remove-NonRuntimeFiles {
    param([Parameter(Mandatory = $true)] [string]$LayoutRoot)

    Remove-FilesByPatterns -RootPath $LayoutRoot -Patterns @("*.pdb", "*.xml")
}

function Remove-UnsupportedRuntimePayload {
    param(
        [Parameter(Mandatory = $true)] [string]$RootPath,
        [Parameter(Mandatory = $true)] [string]$RuntimeIdentifier
    )

    $runtimesRoot = Join-Path $RootPath "runtimes"
    if (-not (Test-Path -LiteralPath $runtimesRoot)) {
        return
    }

    $disallowedArchNames = switch ($RuntimeIdentifier) {
        "win-x64" { @("win-arm64", "win-x86", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64") }
        "win-arm64" { @("win-x64", "win-x86", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64") }
        default { @("win-x86", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64") }
    }

    Get-ChildItem -Path $runtimesRoot -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $disallowedArchNames -contains $_.Name } |
        Sort-Object FullName -Descending |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
}

function Assert-BootstrapManifestPresent {
    param([Parameter(Mandatory = $true)] [string]$RootPath)

    $bootstrapManifestPath = Join-Path $RootPath "assets\bootstrap\asset-manifest.json"
    if (-not (Test-Path -LiteralPath $bootstrapManifestPath)) {
        throw "Bootstrap asset manifest is missing from '$RootPath'."
    }
}

function Assert-HeavyAssetsAbsent {
    param([Parameter(Mandatory = $true)] [string]$RootPath)

    $forbiddenPaths = @(
        (Join-Path $RootPath "assets\models\ggml-small.bin"),
        (Join-Path $RootPath "assets\pyannote"),
        (Join-Path $RootPath "assets\python")
    )

    foreach ($path in $forbiddenPaths) {
        if (Test-Path -LiteralPath $path) {
            throw "Heavy provisioned asset content must not be packaged. Found forbidden path: $path"
        }
    }
}

function Assert-UnsupportedRuntimePayloadAbsent {
    param(
        [Parameter(Mandatory = $true)] [string]$RootPath,
        [Parameter(Mandatory = $true)] [string]$RuntimeIdentifier
    )

    $runtimesRoot = Join-Path $RootPath "runtimes"
    if (-not (Test-Path -LiteralPath $runtimesRoot)) {
        return
    }

    $forbiddenNames = switch ($RuntimeIdentifier) {
        "win-x64" { @("win-arm64", "win-x86", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64") }
        "win-arm64" { @("win-x64", "win-x86", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64") }
        default { @("win-x86", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64") }
    }

    $matches = Get-ChildItem -Path $runtimesRoot -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $forbiddenNames -contains $_.Name } |
        Select-Object -ExpandProperty FullName

    if ($matches) {
        throw "Unsupported runtime payload was found in '$RootPath': $($matches -join ', ')"
    }
}

function New-MsixUploadFromBundle {
    param(
        [Parameter(Mandatory = $true)] [string]$BundlePath,
        [Parameter(Mandatory = $true)] [string]$UploadPath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression

    $zipPath = [System.IO.Path]::ChangeExtension($UploadPath, ".zip")
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    $zipArchive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entryName = [System.IO.Path]::GetFileName($BundlePath)
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zipArchive,
            $BundlePath,
            $entryName,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    finally {
        $zipArchive.Dispose()
    }

    if (Test-Path -LiteralPath $UploadPath) {
        Remove-Item -LiteralPath $UploadPath -Force
    }

    Move-Item -LiteralPath $zipPath -Destination $UploadPath -Force
}

Write-Host "Publishing self-contained x64 output..."
Invoke-StorePublish -RuntimeIdentifier "win-x64" -OutputPath $publishX64
Remove-UnsupportedRuntimePayload -RootPath $publishX64 -RuntimeIdentifier "win-x64"
Assert-BootstrapManifestPresent -RootPath $publishX64
Assert-HeavyAssetsAbsent -RootPath $publishX64
Assert-UnsupportedRuntimePayloadAbsent -RootPath $publishX64 -RuntimeIdentifier "win-x64"

Write-Host "Publishing self-contained arm64 output..."
Invoke-StorePublish -RuntimeIdentifier "win-arm64" -OutputPath $publishArm64
Remove-UnsupportedRuntimePayload -RootPath $publishArm64 -RuntimeIdentifier "win-arm64"
Assert-BootstrapManifestPresent -RootPath $publishArm64
Assert-HeavyAssetsAbsent -RootPath $publishArm64
Assert-UnsupportedRuntimePayloadAbsent -RootPath $publishArm64 -RuntimeIdentifier "win-arm64"

Write-Host "Preparing package layouts..."
Copy-DirectoryRobust -SourcePath $publishX64 -DestinationPath $layoutX64
Copy-DirectoryRobust -SourcePath $publishArm64 -DestinationPath $layoutArm64

New-Item -ItemType Directory -Force -Path (Join-Path $layoutX64 "assets"), (Join-Path $layoutArm64 "assets") | Out-Null
Copy-DirectoryRobust -SourcePath $packageAssetsPath -DestinationPath (Join-Path $layoutX64 "assets")
Copy-DirectoryRobust -SourcePath $packageAssetsPath -DestinationPath (Join-Path $layoutArm64 "assets")

Remove-NonRuntimeFiles -LayoutRoot $layoutX64
Remove-NonRuntimeFiles -LayoutRoot $layoutArm64
Remove-UnsupportedRuntimePayload -RootPath $layoutX64 -RuntimeIdentifier "win-x64"
Remove-UnsupportedRuntimePayload -RootPath $layoutArm64 -RuntimeIdentifier "win-arm64"

Assert-BootstrapManifestPresent -RootPath $layoutX64
Assert-BootstrapManifestPresent -RootPath $layoutArm64
Assert-HeavyAssetsAbsent -RootPath $layoutX64
Assert-HeavyAssetsAbsent -RootPath $layoutArm64
Assert-UnsupportedRuntimePayloadAbsent -RootPath $layoutX64 -RuntimeIdentifier "win-x64"
Assert-UnsupportedRuntimePayloadAbsent -RootPath $layoutArm64 -RuntimeIdentifier "win-arm64"

Set-ManifestArchitecture -SourceManifestPath $manifestPath -DestinationManifestPath (Join-Path $layoutX64 "AppxManifest.xml") -Architecture "x64"
Set-ManifestArchitecture -SourceManifestPath $manifestPath -DestinationManifestPath (Join-Path $layoutArm64 "AppxManifest.xml") -Architecture "arm64"

$x64Msix = Join-Path $packagesDir ("AudioScript_{0}_x64_sc.msix" -f $version)
$arm64Msix = Join-Path $packagesDir ("AudioScript_{0}_arm64_sc.msix" -f $version)
$bundlePath = Join-Path $packagesDir ("AudioScript_{0}_x64_arm64_sc.msixbundle" -f $version)
$uploadPath = Join-Path $packagesDir ("AudioScript_{0}_x64_arm64_sc.msixupload" -f $version)

Write-Host "Packing architecture-specific MSIX files..."
& $makeAppxPath pack /d $layoutX64 /p $x64Msix /o | Out-Null
& $makeAppxPath pack /d $layoutArm64 /p $arm64Msix /o | Out-Null

Copy-Item -LiteralPath $x64Msix -Destination (Join-Path $bundleInput ([System.IO.Path]::GetFileName($x64Msix))) -Force
Copy-Item -LiteralPath $arm64Msix -Destination (Join-Path $bundleInput ([System.IO.Path]::GetFileName($arm64Msix))) -Force

Write-Host "Bundling Store package..."
& $makeAppxPath bundle /d $bundleInput /p $bundlePath /bv $version /o | Out-Null

Write-Host "Creating bundled MSIXUPLOAD..."
New-MsixUploadFromBundle -BundlePath $bundlePath -UploadPath $uploadPath

Write-Host "Store package created:"
Write-Host $uploadPath
