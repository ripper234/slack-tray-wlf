# Slack Tray Hours

A tiny Windows background app that gives Slack's **system tray icon beside the clock** office hours. Slack can respect your notification schedule while its blue or red badge is still visible. This utility moves that icon into the hidden-icons menu outside your work hours.

At installation, choose exactly three things: a **Sunday or Monday** start to your five-day workweek, a **daily start time**, and a **daily end time**. The defaults are **Sunday through Thursday, 08:00–18:00**. For a Monday start, the workdays are Monday through Friday. The icon is visible during those hours and hidden in the overflow menu at every other time.

The same hours apply to all five workdays. Enter 24-hour times such as `08:00` and `18:00`; the start must be earlier than the end on the same day. The end time is when the icon becomes hidden. The app uses your computer's local time, including time-zone and daylight-saving changes. Changes normally apply within five seconds while Windows is awake.

**Requires Windows 11 22H2 or newer with the standard Windows taskbar. Windows 10 is not supported.**

## Install

1. Download this project's ZIP and choose **Extract All**.
2. Open the extracted folder and double-click **`Install.cmd`** as your normal Windows user.
3. Answer the three short questions. Press **Enter** to accept each suggested value.
4. Wait for the installation success message. Done. No reboot or administrator password is needed.

The installer builds a small executable using the C# compiler already included with Windows. No SDK, extra runtime, package manager, Slack credentials, or internet connection is needed during installation or operation. You can delete the extracted download after installation.

Windows may flag an unsigned downloaded script. Inspect the source before running it. If your organization blocks scripts, local compilation, or Task Scheduler, installation will report an error; it will not change organization policy or request elevation.

## What happens

- If Slack is running in the monitored Windows session and has a registered tray entry, its icon is promoted or moved into the hidden-icon overflow according to the schedule.
- If Slack is closed, nothing is launched. When you open Slack yourself, the next check applies the current schedule.
- The app starts when you sign in, survives reboots through a per-user scheduled task, and catches up after sleep or a time-zone change. It does not wake a sleeping computer.
- A five-minute scheduled watchdog restarts it if it has stopped. A mutex prevents duplicate background instances.
- Manually dragging Slack's icon is temporary: the schedule is enforced again within five seconds.

This only changes tray placement. It does **not** mute notifications, change Slack presence, close or minimize Slack, hide its window, or remove a pinned Slack taskbar button. Slack can continue showing notification toasts while its icon is hidden.

## Check or uninstall

Double-click **`Status.cmd`** in the download, or run `%LOCALAPPDATA%\SlackTrayHours\Status.cmd`. It reports the schedule, background task, and matching tray entries. A successful registry update is not a visual confirmation that Explorer displayed the icon.

Double-click **`Uninstall.cmd`**, or run `%LOCALAPPDATA%\SlackTrayHours\Uninstall.cmd`. The uninstaller stops automatic enforcement and restores each icon's original setting where it can safely identify the same entry. It preserves a later value that differs from the last value written by this app. If restoration fails, it keeps the recovery files and reports the error.

Run `Install.cmd` again to change those three settings or install a newer version. It offers your current settings as the defaults. Existing original-setting backups are retained.

## How it works

The app checks `HKCU\Control Panel\NotifyIconSettings` every five seconds. It matches only an `ExecutablePath` whose filename is exactly `slack.exe`, case-insensitively, and changes only that entry's `IsPromoted` DWORD: `1` means visible, `0` means overflow. It writes only when needed. Windows can retain old Slack entries, so all matching registered entries may be updated while Slack is running; no new tray entry is created.

Before its first change to an entry, it saves the original value and executable identity under `HKCU\Software\SlackTrayHours\Backup`. The executable, your three schedule settings, supporting scripts, and bounded local logs live in `%LOCALAPPDATA%\SlackTrayHours`. The scheduled task is named `SlackTrayHours-<your Windows SID>` and runs only with your existing interactive user token, at limited privilege. No password is stored.

There is no network code, telemetry, auto-update, Slack API integration, Explorer restart, process injection, or screenshot monitoring. The installer uses a process-scoped PowerShell execution-policy flag; it does not permanently change PowerShell policy.

## Compatibility and limitations

- **The Windows registry interface is undocumented.** It is used by existing Windows 11 tray utilities, but a future Windows update could change it. This project checks the Windows build and tolerates entries that appear late or disappear. It never falls back to restarting Explorer.
- This release has **not yet been visually tested on a real Windows desktop**. The test suite exercises the schedule and registry behavior separately from Explorer. Complete the [desktop acceptance checklist](docs/TESTING.md) on your machine before treating the release as verified there.
- Custom taskbars such as ExplorerPatcher or StartAllBack are outside the supported scope.
- Keep Windows' hidden-icon menu enabled if you want access to hidden Slack through the overflow arrow.
- Multiple simultaneous sessions for the **same** Windows account are outside scope. One background instance monitors the session in which it started; tray preferences are shared by that account.
- The schedule follows the PC's local time when travelling. It does not stay fixed to Israel time.
- This is an independent utility, unaffiliated with Slack or Microsoft.

## Build and test

From **Windows PowerShell 5.1** in the project folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-Tests.ps1
```

The source deliberately targets the compiler and .NET Framework bundled with Windows. Tests must not edit the real tray settings or install the background task. GitHub Actions runs build and tests on Windows and packages the source ZIP. See [TESTING.md](docs/TESTING.md) for the separate manual UI checks.

## Technical references

- [Microsoft: notification-area behavior and user-controlled promotion](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)
- [NotifyIconPromote: an existing Windows 11 registry-based promoter](https://github.com/Aemony/NotifyIconPromote)
- [Microsoft: interactive-token scheduled tasks](https://learn.microsoft.com/en-us/windows/win32/taskschd/principal-logontype)

Released under the Unlicense. Contributions welcome, especially verified Windows-build compatibility reports and small reproducible bug reports.
