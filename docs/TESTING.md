# Test and release checklist

## Automated coverage

Run `tests\Run-Tests.ps1` in Windows PowerShell 5.1. The suite compiles the production C# source with a separate console test entry point, checks both workweek schedules across the full week, verifies configuration parsing and path matching, and exercises backup/restoration in a unique disposable registry subtree. It must not create the production task or modify real tray preferences.

The CI job also parses every PowerShell script and builds the Windows GUI executable. A green CI build does not verify Explorer's visual response or installer behavior in a standard user's desktop session.

## Manual Windows desktop checks

Use a disposable Windows user or VM for simulated clock changes. Changing a real PC's time can affect unrelated apps. Record the Windows edition/build (`winver`) and Slack installation type.

- Install as a standard user. Confirm no UAC prompt, no admin credentials, one scheduled task and one windowless background process. Confirm the three prompts, press Enter for each, and check the default Sunday–Thursday 08:00–18:00 schedule.
- While Slack is closed, install and wait at least ten seconds. Confirm Slack stays closed.
- Launch Slack Sunday through Thursday before 08:00; icon stays in overflow. Cross 08:00; icon moves out within five seconds.
- Cross 18:00; icon moves to overflow within five seconds. Friday and Saturday stay hidden, including during daytime. Sunday 08:00 shows it again.
- Reinstall and choose Monday with 08:30–17:15. Confirm Sunday is hidden, Monday 08:30 shows, Friday 17:15 hides, and Saturday stays hidden. Check that Status displays the selected schedule. Reinstall again and confirm these choices appear as the prompt defaults.
- Quit and reopen Slack, and let Slack update if available. Confirm newly registered Slack entries receive the schedule.
- Sleep across a boundary and resume; state catches up within five seconds after the app resumes.
- Reboot and sign in. Confirm enforcement starts automatically, including on battery power.
- Stop only the utility process through Task Manager. Confirm its scheduled watchdog restarts it within five minutes while logged in.
- Confirm other tray icons, pinned taskbar buttons, Slack windows, messages, and notification preferences are unaffected.
- Run Status; compare the reported registry state with what Explorer actually displays.
- Upgrade from v0.1 to v0.2. Verify one instance, three installer prompts, and original-setting backups retained.
- Upgrade from v0.2 to v0.3 by manually installing the v0.3 ZIP once. Verify that Update.cmd is installed. Run it when a newer version is available, decline its confirmation, and verify that no source archive was downloaded and that the running installation, task, schedule, and tray preference have not changed.
- Run the update command again and confirm. Verify it reports the target version before changing anything, preserves the selected three schedule settings and original-setting backups, and leaves exactly one background instance and one task.
- Disconnect from the internet during the version check; verify the current installation keeps running unchanged. Separately, fail the archive download after confirmation, then try an invalid or incomplete archive; verify neither case starts the installer.
- If the new installer fails after stopping the old process, verify the prior version and task are restored and running, or that the failure clearly identifies recovery files for manual repair.
- Re-run the update command when already on the current version. Verify it reports that no update is needed and does not restart the process or rewrite settings.
- Uninstall. Confirm the scheduled task, process, and config.json are gone and the original Slack tray preference is restored. Reboot to confirm it stays uninstalled.

## Current validation status

The v0.1 and v0.2 Windows CI builds and tests passed. The v0.3 CI workflow must run policy and registry fixtures, PowerShell syntax parsing, and executable compilation on Windows. A passing workflow cannot verify interactive installation or updating, Task Scheduler behavior, network failures, or Explorer's visible response. Complete the manual checks above on a Windows desktop before describing these behaviors as verified.
