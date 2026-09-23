using System;
using Microsoft.Win32;
using SlackTrayHours;

namespace SlackTrayHours.Tests
{
    // No real tray keys, startup tasks, Slack processes, or production backup keys
    // are touched. Every registry fixture lives in a unique disposable subtree.
    internal static class PolicyAndRegistryTests
    {
        private static int passed;
        private static int failed;
        private const string SlackPath = @"C:\Users\Test\AppData\Local\slack\slack.exe";

        public static int Main(string[] args)
        {
            bool policyOnly = args.Length == 1 && args[0] == "--policy-only";
            if (args.Length > 0 && !policyOnly)
            {
                Console.Error.WriteLine("Usage: PolicyAndRegistryTests.exe [--policy-only]");
                return 2;
            }
            Run("every minute of the weekly schedule", WeeklySchedule);
            Run("exact opening and closing boundaries", ScheduleBoundaries);
            Run("Slack executable matching", ExecutableMatching);
            if (policyOnly)
            {
                Console.WriteLine("SKIPPED Windows registry tests (--policy-only).");
                Console.WriteLine("{0} passed; {1} failed.", passed, failed);
                return failed == 0 ? 0 : 1;
            }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Console.Error.WriteLine("Registry tests require Windows; use --policy-only explicitly elsewhere.");
                return 2;
            }
            RunRegistry("Slack absent means no changes or backups", NoSlack);
            RunRegistry("other applications remain untouched", OtherApplication);
            RunRegistry("original DWORD restored after repeated transitions", RestoreDword);
            RunRegistry("original missing value restored", RestoreMissingValue);
            RunRegistry("unchanged values need no backup", AlreadyCorrect);
            RunRegistry("manual override preserved on restore", ManualOverride);
            RunRegistry("manual deletion preserved on restore", ManualDeletion);
            RunRegistry("reused icon identity is not restored", ReusedIdentity);
            RunRegistry("missing icon is not recreated", MissingIcon);
            RunRegistry("malformed executable metadata is ignored", MalformedPath);
            RunRegistry("malformed promotion metadata is safe", MalformedPromotion);
            RunRegistry("corrupt backups prevent writes and remain for diagnosis", CorruptBackup);
            RunRegistry("interrupted write can be restored", PendingWriteRecovery);
            Console.WriteLine("{0} passed; {1} failed.", passed, failed);
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine("FAIL " + name + ": " + ex);
            }
        }

        private static void RunRegistry(string name, Action<RegistryKey, RegistryKey> test)
        {
            Run(name, delegate
            {
                string ownPath = @"Software\SlackTrayHours.Tests\" + Guid.NewGuid().ToString("N");
                try
                {
                    using (RegistryKey fixture = Registry.CurrentUser.CreateSubKey(ownPath))
                    using (RegistryKey icons = fixture.CreateSubKey("Icons"))
                    using (RegistryKey backups = fixture.CreateSubKey("Backups"))
                        test(icons, backups);
                }
                finally { Registry.CurrentUser.DeleteSubKeyTree(ownPath, false); }
            });
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!Object.Equals(expected, actual))
                throw new Exception(message + ": expected " + expected + ", got " + actual);
        }

        private static void WeeklySchedule()
        {
            DateTime sunday = new DateTime(2026, 9, 20);
            for (int day = 0; day < 7; day++)
                for (int minute = 0; minute < 1440; minute++)
                {
                    DateTime local = sunday.AddDays(day).AddMinutes(minute);
                    bool expected = day < 5 && minute >= 540 && minute < 1080;
                    Equal(expected, Policy.ShouldShow(local), local.ToString("O"));
                }
        }

        private static void ScheduleBoundaries()
        {
            DateTime sunday = new DateTime(2026, 9, 20);
            for (int day = 0; day < 7; day++)
            {
                DateTime open = sunday.AddDays(day).AddHours(9);
                DateTime close = sunday.AddDays(day).AddHours(18);
                Equal(false, Policy.ShouldShow(open.AddTicks(-1)), "tick before opening");
                Equal(day < 5, Policy.ShouldShow(open), "opening");
                Equal(day < 5, Policy.ShouldShow(close.AddTicks(-1)), "tick before closing");
                Equal(false, Policy.ShouldShow(close), "closing");
                Equal(false, Policy.ShouldShow(sunday.AddDays(day)), "midnight");
            }
            // Calls need no transition history: resume and clock changes use current time.
            Equal(false, Policy.ShouldShow(sunday.AddDays(5).AddHours(12)), "Friday after resume");
            Equal(true, Policy.ShouldShow(sunday.AddHours(10)), "Sunday after clock change");
        }

        private static void ExecutableMatching()
        {
            string[] accepted = {
                SlackPath, @"C:\Slack\SLACK.EXE", "\"" + SlackPath + "\"",
                @"%LOCALAPPDATA%\slack\slack.exe",
                @"{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\slack\slack.exe"
            };
            foreach (string path in accepted)
                Equal(true, Policy.IsSlackExecutablePath(path), "accept " + path);
            string[] rejected = {
                null, "", " ", @"C:\slack.exe\other.exe", @"C:\Apps\notslack.exe",
                @"C:\Apps\slack.exe.bak", @"C:\Apps\slack-helper.exe",
                @"C:\Apps\slack.exe --start", @"C:\Slack\Update.exe"
            };
            foreach (string path in rejected)
                Equal(false, Policy.IsSlackExecutablePath(path), "reject " + path);
        }

        private static void AddIcon(RegistryKey icons, string name, string executable, int? promoted)
        {
            using (RegistryKey key = icons.CreateSubKey(name))
            {
                key.SetValue("ExecutablePath", executable, RegistryValueKind.String);
                if (promoted.HasValue) key.SetValue("IsPromoted", promoted.Value, RegistryValueKind.DWord);
            }
        }

        private static object Promotion(RegistryKey icons, string name)
        {
            using (RegistryKey key = icons.OpenSubKey(name))
                return key == null ? null : key.GetValue("IsPromoted", null);
        }

        private static void NoSlack(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, 0);
            ReconciliationResult result = RegistryController.Reconcile(icons, backups, false, true);
            Equal(0, result.Changed, "writes when Slack absent");
            Equal(0, backups.SubKeyCount, "backups when Slack absent");
            Equal((object)0, Promotion(icons, "100"), "original visibility");
        }

        private static void OtherApplication(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, 0);
            AddIcon(icons, "200", @"C:\Tools\other.exe", 0);
            ReconciliationResult result = RegistryController.Reconcile(icons, backups, true, true);
            Equal(1, result.Changed, "only Slack changes");
            Equal(0, result.Errors, "reconciliation errors");
            Equal((object)1, Promotion(icons, "100"), "Slack visibility");
            Equal((object)0, Promotion(icons, "200"), "other app visibility");
            Equal(1, backups.SubKeyCount, "only Slack backed up");
        }

        private static void RestoreDword(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, 0);
            Equal(1, RegistryController.Reconcile(icons, backups, true, true).Changed, "first write");
            Equal(0, RegistryController.Reconcile(icons, backups, true, true).Changed, "repeated write");
            Equal(1, RegistryController.Reconcile(icons, backups, true, false).Changed, "evening");
            Equal(1, RegistryController.Reconcile(icons, backups, true, true).Changed, "next morning");
            RestoreResult result = RegistryController.Restore(icons, backups);
            Equal(1, result.Restored, "restored count");
            Equal(0, result.Errors, "restore errors");
            Equal((object)0, Promotion(icons, "100"), "first original retained");
            using (RegistryKey key = icons.OpenSubKey("100"))
                Equal(RegistryValueKind.DWord, key.GetValueKind("IsPromoted"), "restored value kind");
            Equal(0, backups.SubKeyCount, "consumed backup removed");
        }

        private static void RestoreMissingValue(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, null);
            Equal(1, RegistryController.Reconcile(icons, backups, true, true).Changed, "set absent value");
            Equal((object)1, Promotion(icons, "100"), "promoted value");
            Equal(1, RegistryController.Restore(icons, backups).Restored, "restored missing value");
            Equal<object>(null, Promotion(icons, "100"), "original absence restored");
            using (RegistryKey key = icons.OpenSubKey("100"))
                Equal(SlackPath, (string)key.GetValue("ExecutablePath"), "icon itself preserved");
        }

        private static void AlreadyCorrect(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, 1);
            Equal(0, RegistryController.Reconcile(icons, backups, true, true).Changed, "unneeded writes");
            Equal(0, backups.SubKeyCount, "unneeded backups");
        }

        private static void ManualOverride(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, 0);
            RegistryController.Reconcile(icons, backups, true, true);
            using (RegistryKey key = icons.OpenSubKey("100", true))
                key.SetValue("IsPromoted", 0, RegistryValueKind.DWord);
            RestoreResult result = RegistryController.Restore(icons, backups);
            Equal(0, result.Restored, "manual edit must not be overwritten");
            Equal(1, result.Preserved, "manual edit preserved count");
            Equal((object)0, Promotion(icons, "100"), "manual visibility retained");
        }

        private static void ManualDeletion(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, 0);
            RegistryController.Reconcile(icons, backups, true, true);
            using (RegistryKey key = icons.OpenSubKey("100", true)) key.DeleteValue("IsPromoted");
            Equal(0, RegistryController.Restore(icons, backups).Restored, "manual deletion must remain");
            Equal<object>(null, Promotion(icons, "100"), "manual deletion retained");
        }

        private static void ReusedIdentity(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, 0);
            RegistryController.Reconcile(icons, backups, true, true);
            using (RegistryKey key = icons.OpenSubKey("100", true))
                key.SetValue("ExecutablePath", @"C:\Tools\other.exe", RegistryValueKind.String);
            Equal(0, RegistryController.Restore(icons, backups).Restored, "reused key not restored");
            Equal((object)1, Promotion(icons, "100"), "replacement icon untouched");
        }

        private static void MissingIcon(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, 0);
            RegistryController.Reconcile(icons, backups, true, true);
            icons.DeleteSubKeyTree("100");
            Equal(0, RegistryController.Restore(icons, backups).Restored, "deleted icon not restored");
            Equal(0, icons.SubKeyCount, "deleted icon not recreated");
        }

        private static void MalformedPath(RegistryKey icons, RegistryKey backups)
        {
            using (RegistryKey key = icons.CreateSubKey("100"))
            {
                key.SetValue("ExecutablePath", 12, RegistryValueKind.DWord);
                key.SetValue("IsPromoted", 0, RegistryValueKind.DWord);
            }
            Equal(0, RegistryController.Reconcile(icons, backups, true, true).Changed, "bad path ignored");
            Equal((object)0, Promotion(icons, "100"), "bad path does not trigger write");
            Equal(0, backups.SubKeyCount, "bad path does not trigger backup");
        }

        private static void MalformedPromotion(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, null);
            using (RegistryKey key = icons.OpenSubKey("100", true))
                key.SetValue("IsPromoted", "not-a-number", RegistryValueKind.String);
            RegistryController.Reconcile(icons, backups, true, true);
            RegistryController.Restore(icons, backups);
            // Either skip unsupported original types, or preserve their exact data
            // through backup/restore. Never silently destroy an unknown value.
            Equal((object)"not-a-number", Promotion(icons, "100"), "malformed original preserved");
            using (RegistryKey key = icons.OpenSubKey("100"))
                Equal(RegistryValueKind.String, key.GetValueKind("IsPromoted"), "original type preserved");
        }

        private static void CorruptBackup(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, 0);
            RegistryController.Reconcile(icons, backups, true, true);
            using (RegistryKey backup = backups.OpenSubKey("100", true))
                backup.SetValue("SchemaVersion", "broken", RegistryValueKind.String);
            ReconciliationResult reconcile = RegistryController.Reconcile(icons, backups, true, false);
            Equal(0, reconcile.Changed, "do not write without valid recovery data");
            Equal(1, reconcile.Errors, "corrupt backup reported");
            RestoreResult restore = RegistryController.Restore(icons, backups);
            Equal(0, restore.Restored, "no restore with corrupt backup");
            Equal(1, restore.Errors, "restore error reported");
            Equal((object)1, Promotion(icons, "100"), "icon unchanged");
            Equal(1, backups.SubKeyCount, "corrupt backup retained for diagnosis");
        }

        private static void PendingWriteRecovery(RegistryKey icons, RegistryKey backups)
        {
            AddIcon(icons, "100", SlackPath, 0);
            RegistryController.Reconcile(icons, backups, true, true);
            // Model termination after writing the icon but before committing LastWritten.
            using (RegistryKey backup = backups.OpenSubKey("100", true))
            {
                backup.DeleteValue("LastWritten");
                backup.SetValue("PendingWrite", 1, RegistryValueKind.DWord);
            }
            Equal(1, RegistryController.Restore(icons, backups).Restored, "pending write recovered");
            Equal((object)0, Promotion(icons, "100"), "original restored after interruption");
        }
    }
}
