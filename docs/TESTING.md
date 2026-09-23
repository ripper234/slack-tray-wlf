# Test and release checklist

## Automated coverage

Run `tests\Run-Tests.ps1` in Windows PowerShell 5.1. The suite compiles the actual production C# source with a separate console test entry point, checks schedule boundaries across the full week, checks path matching, and exercises backup/restoration using a unique disposable registry subtree. It must not create the production task or modify real tray preferences.

The CI job also parses every PowerShell script and builds the actual Windows GUI executable. A green CI build does not verify Explorer's visual response or installer behavior under a standard user's desktop session.

## Manual Windows desktop checks

Use a disposable Windows user or VM for simulated clock changes. Changing a real PC's time can affect unrelated apps. Record the Windows edition/build (`winver`) and Slack installation type.

- Install as a standard user. Confirm no UAC prompt, no admin credentials, one scheduled task and one windowless background process.
- While Slack is closed, install and wait at least ten seconds. Confirm Slack stays closed.
- Launch Slack during Sunday through Thursday before 09:00; icon stays in overflow. Cross 09:00; icon moves out within five seconds.
- Cross 18:00; icon moves to overflow within five seconds. Friday and Saturday stay hidden, including during daytime. Sunday 09:00 shows it again.
- Quit and reopen Slack, and let Slack update if available. Confirm newly registered Slack entries receive the schedule.
- Sleep across a boundary and resume; state catches up within five seconds after the app resumes.
- Reboot and sign in. Confirm enforcement starts automatically, including on battery power.
- Stop only the utility process through Task Manager. Confirm its scheduled watchdog restarts it within five minutes while logged in.
- Confirm other tray icons, pinned taskbar buttons, Slack windows, messages, and notification preferences are unaffected.
- Run Status; compare the reported registry state with what Explorer actually displays.
- Reinstall to test an upgrade. Verify only one instance and that original-setting backups survive.
- Uninstall. Confirm the scheduled task and process are gone and the original Slack tray preference is restored. Reboot to confirm it stays uninstalled.

## Current validation status

Initial authoring validation (Linux environment):

- Production source compiled successfully against .NET Framework 4.8 reference assemblies with C# language version 5.
- Actual production policy tests passed: all 10,080 minutes of the week, exact opening/closing tick boundaries, and executable-path matching.
- Every C# and PowerShell source file passed static syntax parsing.
- Independent review covered schedule enforcement, task recovery, scoped writes, and backup restoration.

Windows registry fixtures, native Windows PowerShell parsing, installation/uninstallation, Task Scheduler behavior, and visual Explorer checks have **not yet run**. CI and the manual checklist are provided for those gates. Do not describe the app as visually verified solely because its source compiles or its policy tests pass.
