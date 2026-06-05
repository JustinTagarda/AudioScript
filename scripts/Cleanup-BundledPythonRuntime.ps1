param(
    [Parameter(Mandatory = $true)]
    [string]$PythonRuntimeRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Remove-IfPresent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Remove-DirectoriesByName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string[]]$Names
    )

    foreach ($name in $Names) {
        Get-ChildItem -LiteralPath $RootPath -Directory -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ieq $name } |
            ForEach-Object { Remove-IfPresent -Path $_.FullName }
    }
}

function Remove-FilesByExtension {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string[]]$Extensions
    )

    Get-ChildItem -LiteralPath $RootPath -File -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $Extensions -contains $_.Extension.ToLowerInvariant() } |
        ForEach-Object { Remove-IfPresent -Path $_.FullName }
}

function Assert-NotPresent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (Test-Path -LiteralPath $Path) {
        throw "Unexpected packaged Python $Label found at '$Path'."
    }
}

function Assert-NoMatchingEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $matches = Get-ChildItem -LiteralPath $RootPath -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like $Pattern }

    if ($matches) {
        throw "Unexpected packaged Python $Label still present under '$RootPath' matching '$Pattern'."
    }
}

if ([string]::IsNullOrWhiteSpace($PythonRuntimeRoot)) {
    throw "PythonRuntimeRoot is required."
}

$runtimeRoot = [System.IO.Path]::GetFullPath($PythonRuntimeRoot)
if (-not (Test-Path -LiteralPath $runtimeRoot)) {
    throw "Python runtime root not found at '$runtimeRoot'."
}

$sitePackagesRoot = Join-Path $runtimeRoot "Lib\site-packages"

# Remove only obvious non-runtime packaging payload.
Remove-IfPresent -Path (Join-Path $runtimeRoot "get-pip.py")
Get-ChildItem -LiteralPath (Join-Path $runtimeRoot "Scripts") -Filter "pip*.exe" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-IfPresent -Path $_.FullName }

Remove-IfPresent -Path (Join-Path $sitePackagesRoot "pip")
Get-ChildItem -LiteralPath $sitePackagesRoot -Directory -Filter "pip-*.dist-info" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-IfPresent -Path $_.FullName }

Remove-DirectoriesByName -RootPath $sitePackagesRoot -Names @("__pycache__", "tests", "test", "docs", "doc", "examples", "sample", "samples")
Remove-FilesByExtension -RootPath $sitePackagesRoot -Extensions @(".pyc", ".pyo")

Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*\__pycache__\*" -Label "__pycache__ directories"
Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*\tests\*" -Label "tests directories"
Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*\test\*" -Label "test directories"
Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*\docs\*" -Label "docs directories"
Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*\doc\*" -Label "doc directories"
Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*\examples\*" -Label "examples directories"
Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*\sample\*" -Label "sample directories"
Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*\samples\*" -Label "samples directories"
Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*.pyc" -Label "pyc files"
Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*.pyo" -Label "pyo files"
Assert-NotPresent -Path (Join-Path $sitePackagesRoot "pip") -Label "pip package directory"
Assert-NoMatchingEntries -RootPath $sitePackagesRoot -Pattern "*\pip-*.dist-info\*" -Label "pip dist-info"

Write-Host "Bundled Python runtime cleanup completed successfully."
Write-Host "Root: $runtimeRoot"
