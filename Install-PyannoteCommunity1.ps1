[CmdletBinding()]
param(
    [string]$Token = "",
    [string]$PythonPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$targetDir = Join-Path $repoRoot "assets\pyannote\speaker-diarization-community-1"
$tempDir = Join-Path $repoRoot ("artifacts\pyannote-community1-" + [Guid]::NewGuid().ToString("N"))

if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    $bundledPython = Join-Path $repoRoot "assets\python\win-x64\python.exe"
    if (Test-Path $bundledPython) {
        $PythonPath = $bundledPython
    }
    else {
        $PythonPath = "py -3.12"
    }
}

$resolvedToken = $Token
if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
    $resolvedToken = $env:HF_TOKEN
}

if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
    $resolvedToken = $env:HUGGINGFACE_HUB_TOKEN
}

if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
    throw "Set HF_TOKEN or HUGGINGFACE_HUB_TOKEN to a token that has accepted access to pyannote/speaker-diarization-community-1."
}

if (Test-Path $targetDir) {
    Remove-Item -LiteralPath $targetDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetDir), (Split-Path -Parent $tempDir) | Out-Null

$pythonCommand = @"
from huggingface_hub import snapshot_download
snapshot_download(
    repo_id='pyannote/speaker-diarization-community-1',
    token=r'''$resolvedToken''',
    local_dir=r'''$tempDir''')
print(r'''$tempDir''')
"@

if ($PythonPath -eq "py -3.12") {
    $pythonCommand | py -3.12 -
}
else {
    $pythonCommand | & $PythonPath -
}

Move-Item -LiteralPath $tempDir -Destination $targetDir
Write-Host "Installed Community-1 to $targetDir"
