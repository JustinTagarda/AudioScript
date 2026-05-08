[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ManifestPath = "assets/bootstrap/asset-manifest.json",
    [int]$TimeoutSec = 30
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Manifest not found: $ManifestPath"
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($null -eq $manifest.assets -or $manifest.assets.Count -eq 0) {
    throw "Manifest has no assets."
}

$failures = New-Object System.Collections.Generic.List[string]

foreach ($asset in $manifest.assets) {
    if (-not $asset.required) {
        continue
    }

    foreach ($url in $asset.downloadSources) {
        if ([string]::IsNullOrWhiteSpace($url)) {
            $failures.Add("asset '$($asset.id)': blank download source.")
            continue
        }

        try {
            $head = Invoke-WebRequest -Uri $url -Method Head -TimeoutSec $TimeoutSec
            if ($head.StatusCode -lt 200 -or $head.StatusCode -ge 400) {
                $failures.Add("asset '$($asset.id)': HEAD $url returned status $($head.StatusCode).")
                continue
            }
        }
        catch {
            $failures.Add("asset '$($asset.id)': HEAD $url failed ($($_.Exception.Message)).")
            continue
        }

        if ($url -match '^https://(huggingface\.co|hf-mirror\.com)/api/models/.+/.+$') {
            try {
                $repoMeta = Invoke-WebRequest -Uri $url -TimeoutSec $TimeoutSec
                $repo = $repoMeta.Content | ConvertFrom-Json
                if ($null -eq $repo.siblings -or $repo.siblings.Count -eq 0) {
                    $failures.Add("asset '$($asset.id)': HF API source returned no siblings at $url.")
                    continue
                }

                $sample = $repo.siblings |
                    Where-Object { $_.rfilename -and -not $_.rfilename.EndsWith("/") } |
                    Where-Object { $_.rfilename -notlike "*.md" -and $_.rfilename -notlike "*.gif" -and $_.rfilename -ne ".gitattributes" } |
                    Select-Object -First 1

                if ($null -eq $sample) {
                    $failures.Add("asset '$($asset.id)': HF API source has no downloadable sample files at $url.")
                    continue
                }

                $repoId = $repo.id
                $encodedSegments = ($sample.rfilename -split "/") | ForEach-Object { [System.Uri]::EscapeDataString($_) }
                $encodedPath = [string]::Join("/", $encodedSegments)
                $resolveUrl = "https://$(([uri]$url).Host)/$repoId/resolve/main/$encodedPath?download=true"
                $sampleHead = Invoke-WebRequest -Uri $resolveUrl -Method Head -TimeoutSec $TimeoutSec
                if ($sampleHead.StatusCode -lt 200 -or $sampleHead.StatusCode -ge 400) {
                    $failures.Add("asset '$($asset.id)': HF sample file HEAD failed at $resolveUrl with status $($sampleHead.StatusCode).")
                }
            }
            catch {
                $failures.Add("asset '$($asset.id)': HF API content probe failed for $url ($($_.Exception.Message)).")
            }

            continue
        }

        try {
            $rangeHeaders = @{ Range = "bytes=0-1023" }
            $probe = Invoke-WebRequest -Uri $url -Headers $rangeHeaders -Method Get -TimeoutSec $TimeoutSec
            if ($probe.StatusCode -lt 200 -or $probe.StatusCode -ge 400) {
                $failures.Add("asset '$($asset.id)': range GET $url returned status $($probe.StatusCode).")
            }
        }
        catch {
            $failures.Add("asset '$($asset.id)': range GET $url failed ($($_.Exception.Message)).")
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host $_ }
    throw "Asset endpoint verification failed with $($failures.Count) issue(s)."
}

Write-Host "Asset endpoint verification passed."
