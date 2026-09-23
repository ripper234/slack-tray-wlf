[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$installDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SlackTrayHours'
$executable = Join-Path $installDirectory 'SlackTrayHours.exe'
$taskName = "SlackTrayHours-$sid"
$statusPath = $null
$success = $false
try {
    Write-Host "Installation: $installDirectory"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Slack Tray Hours is not installed for this Windows account. Run Install.cmd from the project download.' }
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    try {
        $task = $scheduler.GetFolder('\').GetTask($taskName)
        $stateNames = @('Unknown', 'Disabled', 'Queued', 'Ready', 'Running')
        Write-Host "Scheduled task: $taskName"
        Write-Host "Enabled: $($task.Enabled) | State: $($stateNames[[int]$task.State]) | Last result: $($task.LastTaskResult)"
        Write-Host "Last start: $($task.LastRunTime) | Next watchdog: $($task.NextRunTime)"
    }
    catch { Write-Host "Scheduled task could not be read: $($_.Exception.Message)" -ForegroundColor Yellow }
    $statusPath = Join-Path ([IO.Path]::GetTempPath()) ('SlackTrayHours-status-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $arguments = '--status-file "' + $statusPath + '"'
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if (Test-Path -LiteralPath $statusPath -PathType Leaf) { Write-Host (Get-Content -LiteralPath $statusPath -Raw) }
    if ($process.ExitCode -ne 0) { throw "Status check failed (exit code $($process.ExitCode))." }
    Write-Host "Log: $installDirectory\runtime.log"
    $success = $true
}
catch { Write-Host "Status error: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    if ($statusPath -and (Test-Path -LiteralPath $statusPath)) { Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue }
}
if (-not $success) { exit 1 }
