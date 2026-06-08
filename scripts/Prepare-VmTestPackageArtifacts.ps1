param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot ".."),
    [string]$PackageVersion = "",
    [string]$PackageTestRoot = "",
    [string]$CertificateSubject = ""
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

$repoRootPath = Resolve-AbsolutePath -Path $RepoRoot
$manifestPath = Join-Path $repoRootPath 'AudioScript.Package\Package.appxmanifest'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Package manifest not found at '$manifestPath'."
}

[xml]$manifestXml = Get-Content -LiteralPath $manifestPath
$manifestVersion = $manifestXml.Package.Identity.Version
$manifestPublisher = $manifestXml.Package.Identity.Publisher

if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    $PackageVersion = $manifestVersion
}

if ([string]::IsNullOrWhiteSpace($CertificateSubject)) {
    $CertificateSubject = $manifestPublisher
}

if ([string]::IsNullOrWhiteSpace($PackageTestRoot)) {
    $PackageTestRoot = Join-Path $repoRootPath ("AudioScript.Package\AppPackages\AudioScript.Package_{0}_Test" -f $PackageVersion)
}

$PackageTestRoot = Resolve-AbsolutePath -Path $PackageTestRoot
$bundlePath = Join-Path $PackageTestRoot ("AudioScript.Package_{0}_x64.msixbundle" -f $PackageVersion)
if (-not (Test-Path -LiteralPath $bundlePath)) {
    throw "VM-test bundle not found at '$bundlePath'."
}

$runtimeArchiveSourcePath = Join-Path $repoRootPath 'AudioScript.Package\AppPackages\AudioScript.PyannotePythonRuntime.win-x64.zip'
if (-not (Test-Path -LiteralPath $runtimeArchiveSourcePath)) {
    throw "VM-test pyannote runtime archive not found at '$runtimeArchiveSourcePath'."
}

$runtimeArchiveFileName = 'pyannote-python-x64.zip'
$runtimeArchivePackagePath = Join-Path $PackageTestRoot $runtimeArchiveFileName
Copy-Item -LiteralPath $runtimeArchiveSourcePath -Destination $runtimeArchivePackagePath -Force

$signToolPath = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if ([string]::IsNullOrWhiteSpace($signToolPath)) {
    throw "signtool.exe was not found under the Windows SDK installation."
}

$certificatePath = Join-Path $PackageTestRoot 'validation.cer'
 $temporaryPfxPath = Join-Path $env:TEMP ("AudioScript.Validation-{0}.pfx" -f ([guid]::NewGuid().ToString('N')))
 $pfxPassword = ([guid]::NewGuid().ToString('N') + ([guid]::NewGuid().ToString('N')))

 $rsa = [System.Security.Cryptography.RSA]::Create(2048)
 try {
     $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
         $CertificateSubject,
         $rsa,
         [System.Security.Cryptography.HashAlgorithmName]::SHA256,
         [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

     $request.CertificateExtensions.Add(
         [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
     $request.CertificateExtensions.Add(
         [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
             [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
             $true))

     $ekuOids = [System.Security.Cryptography.OidCollection]::new()
     [void]$ekuOids.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3', 'Code Signing'))
     $request.CertificateExtensions.Add(
         [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($ekuOids, $false))

     $cert = $request.CreateSelfSigned(
         [DateTimeOffset]::UtcNow.AddDays(-1),
         [DateTimeOffset]::UtcNow.AddYears(3))

     [System.IO.File]::WriteAllBytes($certificatePath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
     [System.IO.File]::WriteAllBytes($temporaryPfxPath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $pfxPassword))
 }
 finally {
     $rsa.Dispose()
 }

& $signToolPath sign /fd SHA256 /f $temporaryPfxPath /p $pfxPassword $bundlePath
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $temporaryPfxPath -Force -ErrorAction SilentlyContinue
    throw "signtool failed while signing '$bundlePath'. Exit code: $LASTEXITCODE"
}

Remove-Item -LiteralPath $temporaryPfxPath -Force -ErrorAction SilentlyContinue

$installerScriptPath = Join-Path $PackageTestRoot 'Install.ps1'
$installerScript = @"
param(
    [string]`$PackageFolder = `$PSScriptRoot
)

Set-StrictMode -Version Latest
`$ErrorActionPreference = 'Stop'

function Resolve-FirstMatch {
    param(
        [Parameter(Mandatory = `$true)]
        [string]`$Folder,
        [Parameter(Mandatory = `$true)]
        [string]`$Filter,
        [Parameter(Mandatory = `$true)]
        [string]`$Label
    )

    `$item = Get-ChildItem -LiteralPath `$Folder -Filter `$Filter -File | Select-Object -First 1
    if (`$null -eq `$item) {
        throw "Expected `$Label was not found in `'`$Folder`'."
    }

    return `$item.FullName
}

`$PackageFolder = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath `$PackageFolder).Path)
`$certificatePath = Resolve-FirstMatch -Folder `$PackageFolder -Filter '*.cer' -Label 'test certificate'
`$bundlePath = Resolve-FirstMatch -Folder `$PackageFolder -Filter '*.msixbundle' -Label 'test package bundle'
`$expectedSubject = '$CertificateSubject'

`$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(`$certificatePath)
if (`$certificate.Subject -ne `$expectedSubject) {
    throw "Certificate subject mismatch. Expected '`$expectedSubject' but found '`$(`$certificate.Subject)'."
}

Import-Certificate -FilePath `$certificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Import-Certificate -FilePath `$certificatePath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null

Add-AppxPackage -Path `$bundlePath -ForceApplicationShutdown

`$installedPackage = Get-AppxPackage -Name 'JustinTagardaSoftware.AudioScript' | Select-Object -First 1
if (`$null -eq `$installedPackage) {
    throw 'Installed AudioScript package could not be resolved after Add-AppxPackage.'
}

`$localStatePath = Join-Path `$env:LOCALAPPDATA ('Packages\' + `$installedPackage.PackageFamilyName + '\LocalState')
`$sourceCachePath = Join-Path `$localStatePath 'Provisioning\source-cache'
New-Item -ItemType Directory -Path `$sourceCachePath -Force | Out-Null
Copy-Item -LiteralPath (Join-Path `$PackageFolder '$runtimeArchiveFileName') -Destination (Join-Path `$sourceCachePath '$runtimeArchiveFileName') -Force
"@
Set-Content -LiteralPath $installerScriptPath -Value $installerScript -Encoding UTF8

Write-Host "VM-test certificate written to: $certificatePath"
Write-Host "VM-test bundle signed at: $bundlePath"
Write-Host "VM-test pyannote runtime sidecar written to: $runtimeArchivePackagePath"
Write-Host "VM-test installer written to: $installerScriptPath"
