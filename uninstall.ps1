[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$installDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SlackTrayHours'
$executable = Join-Path $installDirectory 'SlackTrayHours.exe'
$taskName = "SlackTrayHours-$sid"
$taskSource = 'SlackTrayHours.v1'
$mutex = $null
$lockHeld = $false
$success = $false

try {
    $mutex = New-Object Threading.Mutex($false, "Global\SlackTrayHours.Install.$sid")
    try { $lockHeld = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $lockHeld = $true }
    if (-not $lockHeld) { throw 'Another Slack Tray Hours installer or uninstaller is running for this Windows account. Close it and retry.' }
    $manifestPath = Join-Path $installDirectory 'installation.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "No matching installation marker was found at $manifestPath. No tasks, files, or tray preferences were changed." }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.appId -ne $taskSource -or $manifest.userSid -ne $sid -or $manifest.taskName -ne $taskName -or
        -not [string]::Equals($manifest.installPath, $installDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The installation marker does not match this Windows account and folder. No changes were made.'
    }
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The installed executable is missing. Reinstall from the project download, then uninstall, so your saved tray preferences can be restored.' }
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $taskFolder = $scheduler.GetFolder('\')
    $task = $null
    foreach ($candidate in $taskFolder.GetTasks(1)) {
        if ([string]::Equals($candidate.Name, $taskName, [StringComparison]::OrdinalIgnoreCase)) { $task = $candidate; break }
    }
    if ($null -ne $task) {
        $definition = $task.Definition
        $principal = $definition.Principal.UserId
        if ($principal -ne $sid) {
            try { $principal = (New-Object Security.Principal.NTAccount($principal)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { }
        }
        if ($definition.RegistrationInfo.Source -ne $taskSource -or $principal -ne $sid -or
            $definition.Actions.Count -ne 1 -or $definition.Actions.Item(1).Type -ne 0 -or
            -not [string]::Equals($definition.Actions.Item(1).Path, $executable, [StringComparison]::OrdinalIgnoreCase) -or
            [string]$definition.Actions.Item(1).Arguments -ne '') {
            throw "A task named '$taskName' exists but does not match this installation. It was not changed."
        }
        # Disable before stopping so the watchdog cannot race preference restoration.
        $task.Enabled = $false
    }
    foreach ($argument in @('--stop', '--restore')) {
        $process = Start-Process -FilePath $executable -ArgumentList $argument -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "$argument failed (exit code $($process.ExitCode)). The task remains disabled. Installed files and saved preferences are retained; see $installDirectory\runtime.log, then rerun Uninstall.cmd." }
    }
    # Only remove the task and program after all original preferences are restored.
    if ($null -ne $task) { $taskFolder.DeleteTask($taskName, 0) }
    foreach ($file in @('SlackTrayHours.exe', 'uninstall.ps1', 'Uninstall.cmd', 'status.ps1', 'Status.cmd', 'installation.json', 'config.json', 'runtime.log', 'runtime.log.1', 'status.txt')) {
        $path = Join-Path $installDirectory $file
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
    if ((Test-Path -LiteralPath $installDirectory) -and @(Get-ChildItem -LiteralPath $installDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $installDirectory -Force
    }
    $success = $true
    Write-Host 'Uninstalled. Your saved Slack tray preferences have been restored.'
}
catch { Write-Host "Uninstall stopped: $($_.Exception.Message)" -ForegroundColor Red }
finally {
    if ($lockHeld -and $mutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
if (-not $success) { exit 1 }
