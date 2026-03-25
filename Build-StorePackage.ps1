[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$WindowsSdkBin = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64",
    [string]$OutputRoot = "AudioScript.Package\AppPackages\store-selfcontained"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "AudioScript.csproj"
$manifestPath = Join-Path $repoRoot "AudioScript.Package\Package.appxmanifest"
$assetsPath = Join-Path $repoRoot "AudioScript.Package\assets"
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
$packagesDir = Join-Path $workingRoot "packages"

New-Item -ItemType Directory -Force -Path $publishX64, $publishArm64, $layoutX64, $layoutArm64, $packagesDir | Out-Null

function Invoke-StorePublish {
    param(
        [Parameter(Mandatory = $true)] [string]$RuntimeIdentifier,
        [Parameter(Mandatory = $true)] [string]$OutputPath
    )

    dotnet publish $projectPath `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:StoreSelfContained=true `
        -o $OutputPath
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

Write-Host "Publishing self-contained x64 output..."
Invoke-StorePublish -RuntimeIdentifier "win-x64" -OutputPath $publishX64

Write-Host "Publishing self-contained arm64 output..."
Invoke-StorePublish -RuntimeIdentifier "win-arm64" -OutputPath $publishArm64

Write-Host "Preparing package layouts..."
Copy-Item (Join-Path $publishX64 "*") $layoutX64 -Recurse -Force
Copy-Item (Join-Path $publishArm64 "*") $layoutArm64 -Recurse -Force

New-Item -ItemType Directory -Force -Path (Join-Path $layoutX64 "assets"), (Join-Path $layoutArm64 "assets") | Out-Null
Copy-Item (Join-Path $assetsPath "*.png") (Join-Path $layoutX64 "assets") -Force
Copy-Item (Join-Path $assetsPath "*.png") (Join-Path $layoutArm64 "assets") -Force

Set-ManifestArchitecture -SourceManifestPath $manifestPath -DestinationManifestPath (Join-Path $layoutX64 "AppxManifest.xml") -Architecture "x64"
Set-ManifestArchitecture -SourceManifestPath $manifestPath -DestinationManifestPath (Join-Path $layoutArm64 "AppxManifest.xml") -Architecture "arm64"

$x64Msix = Join-Path $packagesDir ("AudioScript_{0}_x64_sc.msix" -f $version)
$arm64Msix = Join-Path $packagesDir ("AudioScript_{0}_arm64_sc.msix" -f $version)
$bundle = Join-Path $packagesDir ("AudioScript_{0}_x64_arm64_sc.msixbundle" -f $version)
$uploadZip = Join-Path $packagesDir ("AudioScript_{0}_x64_arm64_sc.zip" -f $version)
$upload = Join-Path $packagesDir ("AudioScript_{0}_x64_arm64_sc.msixupload" -f $version)

Write-Host "Packing MSIX files..."
& $makeAppxPath pack /d $layoutX64 /p $x64Msix /o | Out-Null
& $makeAppxPath pack /d $layoutArm64 /p $arm64Msix /o | Out-Null

Write-Host "Bundling MSIX files..."
& $makeAppxPath bundle /d $packagesDir /p $bundle /o | Out-Null

Write-Host "Creating MSIXUPLOAD..."
Compress-Archive -Path $bundle -DestinationPath $uploadZip -Force
Move-Item -Force $uploadZip $upload

Write-Host "Store package created:"
Write-Host $upload