param(
    [string]$ProjectPath = "AudioTranscript.csproj",
    [string]$Configuration = "Release",
    [string]$RuntimeId = "win-x64",
    [string]$OutputRoot = "artifacts/velopack",
    [string]$Version = "",
    [string]$ReleaseRepoUrl = "",
    [string]$Token = "",
    [string]$TargetCommitish = "main",
    [switch]$PublishToGitHub,
    [switch]$PreRelease,
    [switch]$NoDownloadPrevious
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [string]$FailureMessage = "External command failed.",
        [switch]$AllowFailure
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "$FailureMessage Exit code: $exitCode"
    }

    return $exitCode
}

function Get-ProjectProperty {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$ProjectXml,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [string]$Fallback = ""
    )

    foreach ($propertyGroup in $ProjectXml.Project.PropertyGroup) {
        $value = $propertyGroup.$Name
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return [string]$value
        }
    }

    return $Fallback
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectFullPath = Join-Path $repoRoot $ProjectPath

if (-not (Test-Path $projectFullPath)) {
    throw "The project file '$ProjectPath' was not found."
}

[xml]$projectXml = Get-Content $projectFullPath

$packId = Get-ProjectProperty -ProjectXml $projectXml -Name "VelopackPackId"
$packTitle = Get-ProjectProperty -ProjectXml $projectXml -Name "VelopackPackTitle" -Fallback "AudioTranscript"
$packAuthors = Get-ProjectProperty -ProjectXml $projectXml -Name "VelopackPackAuthors"
$mainExe = Get-ProjectProperty -ProjectXml $projectXml -Name "VelopackMainExe" -Fallback "AudioTranscript.exe"
$channel = Get-ProjectProperty -ProjectXml $projectXml -Name "VelopackReleaseChannel" -Fallback "win"
$defaultRepoUrl = Get-ProjectProperty -ProjectXml $projectXml -Name "VelopackReleaseRepoUrl"
$iconPath = Join-Path $repoRoot "assets\AudioTranscript.ico"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectProperty -ProjectXml $projectXml -Name "Version"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "No <Version> property was found in '$ProjectPath'."
}

if ([string]::IsNullOrWhiteSpace($ReleaseRepoUrl)) {
    $ReleaseRepoUrl = $defaultRepoUrl
}

$hasReleaseRepo = -not [string]::IsNullOrWhiteSpace($ReleaseRepoUrl) -and -not $ReleaseRepoUrl.Contains("REPLACE_ME")

if ($PublishToGitHub) {
    if (-not $hasReleaseRepo) {
        throw "Publishing requires a public release repository URL. Set it with -ReleaseRepoUrl or the VELOPACK_RELEASE_REPO_URL Actions variable."
    }

    if ([string]::IsNullOrWhiteSpace($Token)) {
        throw "Publishing requires a GitHub token with access to the public release repository."
    }
}

$outputRootFullPath = Join-Path $repoRoot $OutputRoot
$publishDir = Join-Path $outputRootFullPath "publish"
$releaseDir = Join-Path $outputRootFullPath "Releases"

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

if (Test-Path $releaseDir) {
    Remove-Item $releaseDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

Push-Location $repoRoot
try {
    Invoke-ExternalCommand -FilePath "dotnet" -Arguments @("tool", "restore") -FailureMessage "Failed to restore the local dotnet tools."

    $publishArgs = @(
        "publish",
        $projectFullPath,
        "-c", $Configuration,
        "-r", $RuntimeId,
        "--self-contained", "true",
        "-p:PublishSingleFile=false",
        "-p:Version=$Version",
        "-o", $publishDir
    )

    if ($hasReleaseRepo) {
        $publishArgs += "-p:VelopackReleaseRepoUrl=$ReleaseRepoUrl"
    }

    Invoke-ExternalCommand -FilePath "dotnet" -Arguments $publishArgs -FailureMessage "dotnet publish failed."

    if ($hasReleaseRepo -and -not $NoDownloadPrevious) {
        $downloadArgs = @(
            "tool", "run", "vpk", "--",
            "download", "github",
            "--repoUrl", $ReleaseRepoUrl,
            "--channel", $channel,
            "--outputDir", $releaseDir
        )

        if (-not [string]::IsNullOrWhiteSpace($Token)) {
            $downloadArgs += @("--token", $Token)
        }

        $downloadExitCode = Invoke-ExternalCommand `
            -FilePath "dotnet" `
            -Arguments $downloadArgs `
            -FailureMessage "Failed to download the previous Velopack release." `
            -AllowFailure

        if ($downloadExitCode -ne 0) {
            Write-Host "No previous release was downloaded. Continuing with a full package build."
        }
    }

    $packArgs = @(
        "tool", "run", "vpk", "--",
        "pack",
        "--packId", $packId,
        "--packVersion", $Version,
        "--packDir", $publishDir,
        "--mainExe", $mainExe,
        "--packTitle", $packTitle,
        "--packAuthors", $packAuthors,
        "--channel", $channel,
        "--runtime", $RuntimeId,
        "--outputDir", $releaseDir,
        "--icon", $iconPath
    )

    Invoke-ExternalCommand -FilePath "dotnet" -Arguments $packArgs -FailureMessage "Velopack packaging failed."

    if ($PublishToGitHub) {
        $uploadArgs = @(
            "tool", "run", "vpk", "--",
            "upload", "github",
            "--repoUrl", $ReleaseRepoUrl,
            "--token", $Token,
            "--channel", $channel,
            "--outputDir", $releaseDir,
            "--merge",
            "--releaseName", "$packTitle $Version",
            "--tag", "v$Version",
            "--targetCommitish", $TargetCommitish
        )

        if ($PreRelease) {
            $uploadArgs += "--pre"
        }
        else {
            $uploadArgs += "--publish"
        }

        Invoke-ExternalCommand -FilePath "dotnet" -Arguments $uploadArgs -FailureMessage "Uploading the Velopack release to GitHub failed."
    }

    Write-Host ""
    Write-Host "Velopack release assets are ready in '$releaseDir'."
}
finally {
    Pop-Location
}
