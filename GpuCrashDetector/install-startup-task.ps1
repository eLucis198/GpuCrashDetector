param(
    [string]$TaskName = "GpuCrashDetector",
    [int]$IntervalSeconds = 5,
    [int]$ArtifactIntervalSeconds = 2,
    [int]$Days = 1,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "",
    [string]$ReleaseRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($IntervalSeconds -lt 5) {
    throw "IntervalSeconds must be 5 or greater."
}

if ($ArtifactIntervalSeconds -lt 2) {
    throw "ArtifactIntervalSeconds must be 2 or greater."
}

if ($Days -lt 1) {
    throw "Days must be 1 or greater."
}

$projectRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $projectRoot
$publishScriptPath = Join-Path $projectRoot "publish-release.ps1"

if (-not (Test-Path -LiteralPath $publishScriptPath)) {
    throw "Publish script was not found at '$publishScriptPath'."
}

Push-Location $projectRoot
try {
    $publishParameters = @{
        Configuration = $Configuration
    }

    if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        $publishParameters.RuntimeIdentifier = $RuntimeIdentifier
    }

    if (-not [string]::IsNullOrWhiteSpace($ReleaseRoot)) {
        $publishParameters.ReleaseRoot = $ReleaseRoot
    }

    & $publishScriptPath @publishParameters

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

    $publishDirectory = $releaseDirectory
    $executablePath = Join-Path $publishDirectory "GpuCrashDetector.exe"

    if (-not (Test-Path -LiteralPath $executablePath)) {
        throw "Published executable was not found at '$executablePath'."
    }

    $arguments = @(
        "--interval-seconds", $IntervalSeconds,
        "--artifact-interval-seconds", $ArtifactIntervalSeconds,
        "--days", $Days
    )

    $argumentText = [string]::Join(" ", $arguments)
    $currentUser = "{0}\{1}" -f $env:USERDOMAIN, $env:USERNAME

    $action = New-ScheduledTaskAction -Execute $executablePath -Argument $argumentText -WorkingDirectory $publishDirectory
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -StartWhenAvailable
    $principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited

    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $principal `
        -Description "Starts GpuCrashDetector automatically at Windows logon." `
        -Force | Out-Null

    Write-Host "Startup task '$TaskName' registered."
    Write-Host "Executable: $executablePath"
    Write-Host "Arguments: $argumentText"
}
finally {
    Pop-Location
}
