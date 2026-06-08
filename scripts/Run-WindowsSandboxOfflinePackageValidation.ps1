param(
    [string]$PackageVersion = "",
    [string]$PackageTestRoot = "",
    [string]$CertificatePath = "",
    [switch]$KeepSandboxOpen,
    [int]$LaunchWaitSeconds = 45,
    [int]$TimeoutSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Escape-Xml {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    return [System.Security.SecurityElement]::Escape($Value)
}

$sandboxExe = "C:\Windows\System32\WindowsSandbox.exe"
if (-not (Test-Path -LiteralPath $sandboxExe)) {
    throw "Windows Sandbox is not available on this machine."
}

$manifestPath = Join-Path $PSScriptRoot "..\AudioScript.Package\Package.appxmanifest"
if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Package manifest not found at '$manifestPath'."
    }

    [xml]$manifestXml = Get-Content -LiteralPath $manifestPath
    $PackageVersion = $manifestXml.Package.Identity.Version
}

$packageTestRoot = $PackageTestRoot
if ([string]::IsNullOrWhiteSpace($packageTestRoot)) {
    $packageTestRoot = Join-Path $PSScriptRoot ("..\\AudioScript.Package\\AppPackages\\AudioScript.Package_{0}_Test" -f $PackageVersion)
}

$packageVersionPattern = '^\d+\.\d+\.\d+\.\d+$'
if (-not [System.Text.RegularExpressions.Regex]::IsMatch($PackageVersion, $packageVersionPattern)) {
    throw "PackageVersion must use Major.Minor.Build.Revision format. Current value: '$PackageVersion'."
}

$packageTestRoot = Resolve-AbsolutePath -Path $packageTestRoot
$bundlePath = Get-ChildItem -LiteralPath $packageTestRoot -Filter "*.msixbundle" -File | Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($bundlePath)) {
    throw "Package bundle was not found under '$packageTestRoot'."
}

if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $candidateCertificate = Get-ChildItem -LiteralPath $packageTestRoot -Filter "*.cer" -File | Select-Object -First 1 -ExpandProperty FullName
    if (-not [string]::IsNullOrWhiteSpace($candidateCertificate)) {
        $CertificatePath = $candidateCertificate
    }
}

if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = Resolve-AbsolutePath -Path $CertificatePath
}

if (-not (Test-Path -LiteralPath $bundlePath)) {
    throw "Package bundle was not found at '$bundlePath'."
}

$validationRoot = Join-Path $PSScriptRoot "..\\artifacts\\sandbox-offline-validation"
if (Test-Path -LiteralPath $validationRoot) {
    Remove-Item -LiteralPath $validationRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $validationRoot | Out-Null
$validationRoot = Resolve-AbsolutePath -Path $validationRoot
$resultPath = Join-Path $validationRoot "result.json"
$stdoutPath = Join-Path $validationRoot "sandbox-stdout.log"
$stderrPath = Join-Path $validationRoot "sandbox-stderr.log"
$statusPath = Join-Path $validationRoot "sandbox-status.log"
$hostScriptPath = Join-Path $validationRoot "run-validation.ps1"
$wsbPath = Join-Path $validationRoot "offline-validation.wsb"

$keepSandboxOpenLiteral = if ($KeepSandboxOpen.IsPresent) { '$true' } else { '$false' }

$innerScript = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'

function Write-Result([hashtable]`$payload) {
    `$payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath 'C:\ValidationHost\result.json' -Encoding UTF8
}

function Write-Status([string]`$message) {
    `$timestamp = (Get-Date).ToString('o')
    Add-Content -LiteralPath 'C:\ValidationHost\sandbox-status.log' -Value "[`$timestamp] `$message"
}

`$result = [ordered]@{
    installSucceeded = `$false
    launchSucceeded = `$false
    packageFamilyName = `$null
    installLocation = `$null
    processId = `$null
    logsPath = `$null
    logFiles = @()
    errors = @()
    launchWaitSeconds = $LaunchWaitSeconds
    keepSandboxOpen = $keepSandboxOpenLiteral
}

try {
    Write-Status 'Sandbox script started.'
    if (Test-Path -LiteralPath 'C:\AudioScriptPackage\validation.cer') {
        Write-Status 'Importing validation certificate.'
        Import-Certificate -FilePath 'C:\AudioScriptPackage\validation.cer' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        Import-Certificate -FilePath 'C:\AudioScriptPackage\validation.cer' -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    }

    Write-Status 'Starting package install.'
    Add-AppxPackage -Path ('C:\AudioScriptPackage\AudioScript.Package_{0}_x64.msixbundle' -f $PackageVersion)
    Write-Status 'Package install completed.'
    `$pkg = Get-AppxPackage -Name 'JustinTagardaSoftware.AudioScript' | Select-Object -First 1
    if (`$null -eq `$pkg) {
        throw 'Package install did not return an installed AppX package.'
    }

    `$result.installSucceeded = `$true
    `$result.packageFamilyName = `$pkg.PackageFamilyName
    `$result.installLocation = `$pkg.InstallLocation

    `$appUserModelId = `$pkg.PackageFamilyName + '!App'
    Write-Status "Launching app via AUMID `$appUserModelId."
    Start-Process 'explorer.exe' "shell:AppsFolder\`$appUserModelId" | Out-Null
    Start-Sleep -Seconds $LaunchWaitSeconds

    `$stillRunning = Get-Process -Name 'AudioScript' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (`$stillRunning) {
        `$result.processId = `$stillRunning.Id
    }
    `$result.launchSucceeded = `$null -ne `$stillRunning
    Write-Status ("Launch probe result: " + `$result.launchSucceeded)

    `$localStatePath = Join-Path `$env:LOCALAPPDATA ('Packages\' + `$pkg.PackageFamilyName + '\LocalState')
    `$logsPath = Join-Path `$localStatePath 'Logs'
    `$result.logsPath = `$logsPath
    if (Test-Path -LiteralPath `$logsPath) {
        `$result.logFiles = @(Get-ChildItem -LiteralPath `$logsPath -File | Select-Object -ExpandProperty Name)
    }

    if (`$stillRunning -and -not $KeepSandboxOpen) {
        Stop-Process -Id `$stillRunning.Id -Force
    }
}
catch {
    Write-Status ("Error: " + `$_.Exception.Message)
    `$result.errors += `$_.Exception.ToString()
}
finally {
    Write-Status 'Writing result file.'
    Write-Result -payload `$result
    if (-not $KeepSandboxOpen) {
        Write-Status 'Shutting down sandbox.'
        shutdown.exe /s /t 0 | Out-Null
    }
}
"@

Set-Content -LiteralPath $hostScriptPath -Value $innerScript -Encoding UTF8

$wsbContent = @"
<Configuration>
  <Networking>Disable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$(Escape-Xml $packageTestRoot)</HostFolder>
      <SandboxFolder>C:\AudioScriptPackage</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$(Escape-Xml $validationRoot)</HostFolder>
      <SandboxFolder>C:\ValidationHost</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -ExecutionPolicy Bypass -File C:\ValidationHost\run-validation.ps1</Command>
  </LogonCommand>
</Configuration>
"@

Set-Content -LiteralPath $wsbPath -Value $wsbContent -Encoding UTF8

if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    Copy-Item -LiteralPath $CertificatePath -Destination (Join-Path $packageTestRoot "validation.cer") -Force
}

$sandboxProcess = Start-Process -FilePath $sandboxExe -ArgumentList "`"$wsbPath`"" -PassThru -WindowStyle Normal
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)

while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $resultPath) {
        break
    }

    if ($sandboxProcess.HasExited) {
        break
    }

    Start-Sleep -Seconds 5
    $sandboxProcess.Refresh()
}

if (-not (Test-Path -LiteralPath $resultPath)) {
    if (-not $sandboxProcess.HasExited) {
        Stop-Process -Id $sandboxProcess.Id -Force
    }

    throw "Sandbox validation did not produce a result file within $TimeoutSeconds seconds."
}

Get-Content -LiteralPath $resultPath -Raw
