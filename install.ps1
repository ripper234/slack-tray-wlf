[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$installDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SlackTrayHours'
$executable = Join-Path $installDirectory 'SlackTrayHours.exe'
$taskName = "SlackTrayHours-$sid"
$taskSource = 'SlackTrayHours.v1'
$managedFiles = @('installation.json', 'config.json', 'uninstall.ps1', 'Uninstall.cmd', 'status.ps1', 'Status.cmd', 'SlackTrayHours.exe')
$mutex = $null
$lockHeld = $false
$stage = $null
$taskFolder = $null
$previousTaskXml = $null
$previousTaskEnabled = $true
$taskTouched = $false
$filesChanged = $false
$previousFiles = @()
$hadPreviousInstallation = $false
$keepStage = $false
$succeeded = $false

function Assert-OwnedTask($Task) {
    $definition = $Task.Definition
    $principal = $definition.Principal.UserId
    if ($principal -ne $sid) {
        try { $principal = (New-Object Security.Principal.NTAccount($principal)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { }
    }
    if ($definition.RegistrationInfo.Source -ne $taskSource -or $principal -ne $sid -or
        $definition.Actions.Count -ne 1 -or $definition.Actions.Item(1).Type -ne 0 -or
        -not [string]::Equals($definition.Actions.Item(1).Path, $executable, [StringComparison]::OrdinalIgnoreCase) -or
        [string]$definition.Actions.Item(1).Arguments -ne '') {
        throw "A task named '$taskName' exists but does not belong to this installation. It was not changed."
    }
}

function Get-ExistingTask {
    # COM exceptions are often wrapped by PowerShell, so avoid depending on the
    # outer exception's HRESULT to distinguish a missing task from access denial.
    foreach ($candidate in $taskFolder.GetTasks(1)) {
        if ([string]::Equals($candidate.Name, $taskName, [StringComparison]::OrdinalIgnoreCase)) { return $candidate }
    }
    return $null
}

function Invoke-App([string]$Path, [string]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Slack Tray Hours $Arguments failed (exit code $($process.ExitCode)). See $installDirectory\runtime.log." }
}

function Convert-TimeToMinutes([string]$Value) {
    if ($Value -cnotmatch '^([01][0-9]|2[0-3]):([0-5][0-9])$') {
        throw "Time '$Value' must be in 24-hour HH:mm format (for example, 08:00 or 18:00)."
    }
    return ([int]$Value.Substring(0, 2) * 60) + [int]$Value.Substring(3, 2)
}

function Get-InstalledSchedule([string]$Path) {
    $default = [ordered]@{ workWeekStart = 'Sunday'; startTime = '08:00'; endTime = '18:00' }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $default }
    try {
        $config = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
        $keys = @($config.PSObject.Properties | ForEach-Object { $_.Name })
        $required = @('schemaVersion', 'workWeekStart', 'startTime', 'endTime')
        if ($keys.Count -ne $required.Count -or @($required | Where-Object { $keys -cnotcontains $_ }).Count -ne 0) {
            throw 'Expected exactly schemaVersion, workWeekStart, startTime, and endTime.'
        }
        if (($config.schemaVersion -isnot [int] -and $config.schemaVersion -isnot [long]) -or $config.schemaVersion -ne 1) {
            throw 'schemaVersion must be the number 1.'
        }
        if ($config.workWeekStart -isnot [string] -or @('Sunday', 'Monday') -cnotcontains $config.workWeekStart) {
            throw 'workWeekStart must be Sunday or Monday.'
        }
        if ($config.startTime -isnot [string] -or $config.endTime -isnot [string]) {
            throw 'startTime and endTime must be text in 24-hour HH:mm format.'
        }
        $startMinutes = Convert-TimeToMinutes $config.startTime
        $endMinutes = Convert-TimeToMinutes $config.endTime
        if ($startMinutes -ge $endMinutes) { throw 'The end time must be later than the start time on the same day.' }
        return [ordered]@{ workWeekStart = $config.workWeekStart; startTime = $config.startTime; endTime = $config.endTime }
    }
    catch {
        throw "The installed schedule at $Path is invalid: $($_.Exception.Message) Repair or remove that file, then rerun Install.cmd."
    }
}

function Read-WeekStart([string]$Default) {
    while ($true) {
        $answer = (Read-Host "First workday (Sunday or Monday) [$Default]").Trim()
        if ($answer -eq '') { return $Default }
        if ($answer -ieq 'Sunday') { return 'Sunday' }
        if ($answer -ieq 'Monday') { return 'Monday' }
        Write-Host 'Please enter Sunday or Monday.' -ForegroundColor Yellow
    }
}

function Read-WorkTime([string]$Label, [string]$Default) {
    while ($true) {
        $answer = (Read-Host "$Label (24-hour HH:mm) [$Default]").Trim()
        if ($answer -eq '') { return $Default }
        try { $null = Convert-TimeToMinutes $answer; return $answer }
        catch { Write-Host $_.Exception.Message -ForegroundColor Yellow }
    }
}

try {
    $build = [int](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber).CurrentBuildNumber
    if ($build -lt 22621) { throw 'Slack Tray Hours requires Windows 11 22H2 or later (build 22621+). Windows 10 is not supported.' }
    $mutex = New-Object Threading.Mutex($false, "Global\SlackTrayHours.Install.$sid")
    try { $lockHeld = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $lockHeld = $true }
    if (-not $lockHeld) { throw 'Another Slack Tray Hours installer or uninstaller is running for this Windows account. Close it and retry.' }

    $manifestPath = Join-Path $installDirectory 'installation.json'
    if (Test-Path -LiteralPath $installDirectory) {
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "The folder $installDirectory already exists without an installation marker. Move it aside before installing; its contents were not changed."
        }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.appId -ne $taskSource -or $manifest.userSid -ne $sid -or $manifest.taskName -ne $taskName -or
            -not [string]::Equals($manifest.installPath, $installDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The existing installation marker does not match this Windows account and folder. No files were changed.'
        }
        $hadPreviousInstallation = $true
    }

    $currentSchedule = Get-InstalledSchedule (Join-Path $installDirectory 'config.json')
    Write-Host 'Slack Tray Hours: choose your five-day work week in your Windows local time.'
    Write-Host 'Press Enter to keep the value in brackets.'
    $workWeekStart = Read-WeekStart $currentSchedule.workWeekStart
    while ($true) {
        $startTime = Read-WorkTime 'Start time' $currentSchedule.startTime
        $endTime = Read-WorkTime 'End time' $currentSchedule.endTime
        if ((Convert-TimeToMinutes $startTime) -lt (Convert-TimeToMinutes $endTime)) { break }
        Write-Host 'End time must be later than start time on the same day. Please enter both times again.' -ForegroundColor Yellow
    }
    $workWeekEnd = if ($workWeekStart -eq 'Sunday') { 'Thursday' } else { 'Friday' }

    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $taskFolder = $scheduler.GetFolder('\')
    $existingTask = Get-ExistingTask
    if ($null -ne $existingTask) {
        Assert-OwnedTask $existingTask
        $previousTaskXml = $existingTask.Xml
        $previousTaskEnabled = $existingTask.Enabled
    }

    $stage = Join-Path ([IO.Path]::GetTempPath()) ('SlackTrayHours-install-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $stage
    & (Join-Path $PSScriptRoot 'Build.ps1') -OutputPath (Join-Path $stage 'SlackTrayHours.exe')
    foreach ($file in @('uninstall.ps1', 'Uninstall.cmd', 'status.ps1', 'Status.cmd')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stage $file)
    }
    [ordered]@{ appId = $taskSource; schemaVersion = 1; userSid = $sid; taskName = $taskName; installPath = $installDirectory } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'installation.json') -Encoding UTF8
    $scheduleJson = [ordered]@{ schemaVersion = 1; workWeekStart = $workWeekStart; startTime = $startTime; endTime = $endTime } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $stage 'config.json'), $scheduleJson + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
    $previousDirectory = Join-Path $stage 'previous'
    $null = New-Item -ItemType Directory -Path $previousDirectory
    foreach ($file in $managedFiles) {
        $path = Join-Path $installDirectory $file
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Copy-Item -LiteralPath $path -Destination (Join-Path $previousDirectory $file)
            $previousFiles += $file
        }
    }

    if ($null -ne $existingTask) { $taskTouched = $true; $existingTask.Enabled = $false }
    if (Test-Path -LiteralPath $executable -PathType Leaf) { Invoke-App $executable '--stop' }
    $filesChanged = $true
    $null = New-Item -ItemType Directory -Path $installDirectory -Force
    foreach ($file in $managedFiles) {
        Copy-Item -LiteralPath (Join-Path $stage $file) -Destination (Join-Path $installDirectory $file) -Force
    }

    $definition = $scheduler.NewTask(0)
    $definition.RegistrationInfo.Author = $sid
    $definition.RegistrationInfo.Source = $taskSource
    $definition.RegistrationInfo.Description = 'Moves only Slack notification-area icons according to the five-day schedule in config.json. Does not start Slack.'
    $definition.Principal.UserId = $sid
    $definition.Principal.LogonType = 3 # TASK_LOGON_INTERACTIVE_TOKEN, no stored password
    $definition.Principal.RunLevel = 0 # TASK_RUNLEVEL_LUA, current user only
    $settings = $definition.Settings
    $settings.Enabled = $true
    $settings.AllowDemandStart = $true
    $settings.StartWhenAvailable = $true
    $settings.DisallowStartIfOnBatteries = $false
    $settings.StopIfGoingOnBatteries = $false
    $settings.RunOnlyIfNetworkAvailable = $false
    $settings.WakeToRun = $false
    $settings.ExecutionTimeLimit = 'PT0S'
    $settings.MultipleInstances = 2 # TASK_INSTANCES_IGNORE_NEW
    $settings.RestartInterval = 'PT1M'
    $settings.RestartCount = 3
    $settings.IdleSettings.StopOnIdleEnd = $false
    $settings.IdleSettings.RestartOnIdle = $false
    $trigger = $definition.Triggers.Create(9) # TASK_TRIGGER_LOGON
    $trigger.UserId = $sid
    $trigger.Enabled = $true
    # A manual Run() does not arm a logon trigger's repetition. An independent
    # time trigger starts the watchdog now, including before the next sign-in.
    $watchdog = $definition.Triggers.Create(1) # TASK_TRIGGER_TIME
    $watchdog.StartBoundary = (Get-Date).AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss")
    $watchdog.Enabled = $true
    $watchdog.Repetition.Interval = 'PT5M'
    $watchdog.Repetition.StopAtDurationEnd = $false # Omitted Duration means repeat indefinitely.
    $action = $definition.Actions.Create(0) # TASK_ACTION_EXEC
    $action.Path = $executable
    $action.WorkingDirectory = $installDirectory
    $taskTouched = $true
    $task = $taskFolder.RegisterTaskDefinition($taskName, $definition, 6, $sid, $null, 3, $null)
    $null = $task.Run($null)
    $running = $false
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        Start-Sleep -Milliseconds 1000
        $task = $taskFolder.GetTask($taskName)
        if ($task.State -eq 4) { $running = $true; break }
    }
    if (-not $running) { throw "The task was registered but did not stay running (last result $($task.LastTaskResult)). See $installDirectory\runtime.log. Your organization may restrict scheduled tasks or locally compiled apps." }
    # Check for an immediate crash rather than declaring success after process creation.
    Start-Sleep -Milliseconds 1500
    $task = $taskFolder.GetTask($taskName)
    if ($task.State -ne 4) { throw "Slack Tray Hours exited during startup (last result $($task.LastTaskResult)). See $installDirectory\runtime.log." }
    # A live process alone cannot prove that registry reconciliation succeeds.
    Invoke-App $executable '--once'
    $succeeded = $true
    Write-Host "Installed and running. Slack is visible $workWeekStart-$workWeekEnd, $startTime to $endTime, in your Windows local time; hidden at all other times."
    Write-Host 'Starts again when you sign in. The task checks every five minutes that the background helper is still running.'
    Write-Host "Status:    $installDirectory\Status.cmd"
    Write-Host "Uninstall: $installDirectory\Uninstall.cmd"
}
catch {
    $failure = $_.Exception.Message
    Write-Host "Installation failed: $failure" -ForegroundColor Red
    if ($taskTouched -or $filesChanged) {
        try {
            $currentTask = Get-ExistingTask
            if ($null -ne $currentTask) { Assert-OwnedTask $currentTask; $currentTask.Enabled = $false }
            if ($filesChanged -and (Test-Path -LiteralPath $executable -PathType Leaf)) { Invoke-App $executable '--stop' }
            if ($filesChanged -and -not $hadPreviousInstallation) {
                # A daemon can change the tray before failing. Restore first,
                # while retaining the installed recovery files for a retry.
                Invoke-App $executable '--restore'
                Write-Host "Tray preferences restored. Recovery files remain in $installDirectory; rerun Install.cmd or Uninstall.cmd."
            }
            if ($filesChanged -and $hadPreviousInstallation) {
                foreach ($file in $managedFiles) {
                    $path = Join-Path $installDirectory $file
                    if ($previousFiles -contains $file) {
                        Copy-Item -LiteralPath (Join-Path $previousDirectory $file) -Destination $path -Force
                    } elseif (Test-Path -LiteralPath $path -PathType Leaf) {
                        Remove-Item -LiteralPath $path -Force
                    }
                }
            }
            if ($previousTaskXml) {
                $restoredTask = $taskFolder.RegisterTask($taskName, $previousTaskXml, 6, $sid, $null, 3, $null)
                $restoredTask.Enabled = $previousTaskEnabled
                if ($previousTaskEnabled) { $null = $restoredTask.Run($null) }
                Write-Host 'The previous installation was restored.'
            } elseif ($null -ne $currentTask) {
                $taskFolder.DeleteTask($taskName, 0)
            }
        }
        catch {
            $keepStage = $true
            Write-Host "Automatic rollback could not finish: $($_.Exception.Message). Original tray-preference backups were preserved. Recovery files: $stage" -ForegroundColor Yellow
        }
    }
}
finally {
    if (-not $keepStage -and $stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if ($lockHeld -and $mutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
if (-not $succeeded) { exit 1 }
