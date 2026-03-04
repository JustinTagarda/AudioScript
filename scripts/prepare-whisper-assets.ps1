param(
    [string]$ReleaseTag = 'v1.8.3',
    [string]$ModelName = 'ggml-base.en.bin'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$whisperRoot = Join-Path $projectRoot 'assets\whisper'
$modelsRoot = Join-Path $whisperRoot 'models'

New-Item -ItemType Directory -Force $whisperRoot | Out-Null
New-Item -ItemType Directory -Force $modelsRoot | Out-Null

$binUrl = "https://github.com/ggerganov/whisper.cpp/releases/download/$ReleaseTag/whisper-bin-x64.zip"
$modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$ModelName"

$zipPath = Join-Path $env:TEMP "whisper-bin-x64-$ReleaseTag.zip"
$modelPath = Join-Path $modelsRoot $ModelName

Write-Host "Downloading whisper runtime: $binUrl"
Invoke-WebRequest -Uri $binUrl -OutFile $zipPath

Write-Host "Extracting runtime into $whisperRoot"
Expand-Archive -Path $zipPath -DestinationPath $whisperRoot -Force

$cliPath = Join-Path $whisperRoot 'whisper-cli.exe'
if (-not (Test-Path $cliPath)) {
    $alt = Get-ChildItem -Path $whisperRoot -Filter 'whisper-cli.exe' -Recurse -File | Select-Object -First 1

    if (-not $alt) {
        throw "whisper-cli.exe was not found after extraction."
    }

    Copy-Item $alt.FullName $cliPath -Force
}

if (-not (Test-Path $modelPath)) {
    Write-Host "Downloading model: $modelUrl"
    Invoke-WebRequest -Uri $modelUrl -OutFile $modelPath
} else {
    Write-Host "Model already exists: $modelPath"
}

Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Write-Host 'Whisper assets prepared successfully.'
