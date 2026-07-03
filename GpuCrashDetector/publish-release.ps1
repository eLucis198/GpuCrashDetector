param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "",
    [string]$ReleaseRoot = "",
    [switch]$SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($SelfContained.IsPresent -and [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    throw "SelfContained publish requires a RuntimeIdentifier."
}

$projectRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $projectRoot
$targetFramework = "net10.0-windows"
$releaseDirectory = if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        Join-Path $repoRoot (Join-Path "release" "windows")
    }
    else {
        Join-Path $repoRoot (Join-Path "release" $RuntimeIdentifier)
    }
}
else {
    [System.IO.Path]::GetFullPath($ReleaseRoot)
}

$appDataPath = Join-Path $projectRoot ".appdata"
$userProfilePath = Join-Path $projectRoot ".userprofile"
$nuGetPackagesPath = Join-Path $projectRoot ".nuget-packages"

New-Item -ItemType Directory -Force -Path $appDataPath | Out-Null
New-Item -ItemType Directory -Force -Path $userProfilePath | Out-Null
New-Item -ItemType Directory -Force -Path $nuGetPackagesPath | Out-Null
New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null

$env:APPDATA = $appDataPath
$env:USERPROFILE = $userProfilePath
$env:HOME = $userProfilePath
$env:NUGET_PACKAGES = $nuGetPackagesPath

Push-Location $projectRoot
try {
    dotnet restore --configfile .\NuGet.Config

    $publishArguments = @(
        "publish",
        "-c", $Configuration,
        "--no-restore"
    )

    if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        $publishArguments += @("-r", $RuntimeIdentifier)
    }

    if ($SelfContained.IsPresent) {
        $publishArguments += @("--self-contained", "true")
    }

    & dotnet @publishArguments

    $publishDirectory = if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        Join-Path $projectRoot "bin\$Configuration\$targetFramework\publish"
    }
    else {
        Join-Path $projectRoot "bin\$Configuration\$targetFramework\$RuntimeIdentifier\publish"
    }
    $executablePath = Join-Path $publishDirectory "GpuCrashDetector.exe"

    if (-not (Test-Path -LiteralPath $executablePath)) {
        throw "Published executable was not found at '$executablePath'."
    }

    Get-ChildItem -LiteralPath $releaseDirectory -Force | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $releaseDirectory -Recurse -Force

    Write-Host "Release published to '$releaseDirectory'."
    Write-Host "Executable: $(Join-Path $releaseDirectory 'GpuCrashDetector.exe')"
}
finally {
    Pop-Location
}
