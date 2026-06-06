[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ManifestPath = "assets/bootstrap/asset-manifest.json",
    [int]$TimeoutSec = 30,
    [switch]$IncludeReleaseRequiredAssets
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

function New-EndpointProbeClient {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
    return $client
}

function Invoke-RangeProbe {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)]
        [string]$Url
    )

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Url)
    $request.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new(0, 1023)
    $response = $Client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    return $response
}

function Get-JsonContent {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)]
        [string]$Url
    )

    return $Client.GetStringAsync($Url).GetAwaiter().GetResult()
}

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Manifest not found: $ManifestPath"
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($null -eq $manifest.assets -or $manifest.assets.Count -eq 0) {
    throw "Manifest has no assets."
}

$failures = New-Object System.Collections.Generic.List[string]
$httpClient = New-EndpointProbeClient

try {
    foreach ($asset in $manifest.assets) {
        $isRequired = $asset.required -eq $true
        $isReleaseRequired = $IncludeReleaseRequiredAssets.IsPresent -and $asset.releaseRequired -eq $true
        if (-not $isRequired -and -not $isReleaseRequired) {
            continue
        }

        $sources = @($asset.downloadSources | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $minimumSources = 0
        if ($null -ne $asset.minimumDownloadSources) {
            $minimumSources = [int]$asset.minimumDownloadSources
        }
        elseif ($isRequired -and $asset.deliveryMode -ne "PackagedRequired") {
            $minimumSources = 3
        }

        if ($minimumSources -gt 0 -and $sources.Count -lt $minimumSources) {
            $failures.Add("asset '$($asset.id)': expected at least $minimumSources download source(s), found $($sources.Count).")
            continue
        }

        foreach ($url in $sources) {
            if ([string]::IsNullOrWhiteSpace($url)) {
                $failures.Add("asset '$($asset.id)': blank download source.")
                continue
            }

            if ($url -match '^https://(huggingface\.co|hf-mirror\.com)/api/models/.+/.+$') {
                try {
                    $repo = (Get-JsonContent -Client $httpClient -Url $url) | ConvertFrom-Json
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
                    }
                }
                catch {
                    $failures.Add("asset '$($asset.id)': HF API content probe failed for $url ($($_.Exception.Message)).")
                }

                continue
            }

            try {
                $probe = Invoke-RangeProbe -Client $httpClient -Url $url
                if ([int]$probe.StatusCode -lt 200 -or [int]$probe.StatusCode -ge 400) {
                    $failures.Add("asset '$($asset.id)': range GET $url returned status $([int]$probe.StatusCode).")
                }
            }
            catch {
                $failures.Add("asset '$($asset.id)': range GET $url failed ($($_.Exception.Message)).")
            }
        }
    }
}
finally {
    $httpClient.Dispose()
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host $_ }
    throw "Asset endpoint verification failed with $($failures.Count) issue(s)."
}

Write-Host "Asset endpoint verification passed."
