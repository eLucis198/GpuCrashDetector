param(
    [string]$TaskName = "GpuCrashDetector",
    [int]$IntervalSeconds = 5,
    [int]$ArtifactIntervalSeconds = 2,
    [int]$Days = 1,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
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
$appDataPath = Join-Path $projectRoot ".appdata"
$userProfilePath = Join-Path $projectRoot ".userprofile"
$nuGetPackagesPath = Join-Path $projectRoot ".nuget-packages"

New-Item -ItemType Directory -Force -Path $appDataPath | Out-Null
New-Item -ItemType Directory -Force -Path $userProfilePath | Out-Null
New-Item -ItemType Directory -Force -Path $nuGetPackagesPath | Out-Null

$env:APPDATA = $appDataPath
$env:USERPROFILE = $userProfilePath
$env:HOME = $userProfilePath
$env:NUGET_PACKAGES = $nuGetPackagesPath

Push-Location $projectRoot
try {
    dotnet publish -c $Configuration -r $RuntimeIdentifier --self-contained true

    $publishDirectory = Join-Path $projectRoot "bin\$Configuration\net10.0-windows\$RuntimeIdentifier\publish"
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
