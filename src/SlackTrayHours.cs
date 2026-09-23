// Slack Tray Hours 0.3.0. Released under the Unlicense. Compatible with C# 5 / .NET Framework 4.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace SlackTrayHours
{
    public sealed class WorkSchedule
    {
        public static readonly WorkSchedule Default = new WorkSchedule(DayOfWeek.Sunday,
            TimeSpan.FromHours(8), TimeSpan.FromHours(18));

        public readonly DayOfWeek WorkWeekStart;
        public readonly TimeSpan StartTime;
        public readonly TimeSpan EndTime;

        public WorkSchedule(DayOfWeek workWeekStart, TimeSpan startTime, TimeSpan endTime)
        {
            if (workWeekStart != DayOfWeek.Sunday && workWeekStart != DayOfWeek.Monday)
                throw new ArgumentOutOfRangeException("workWeekStart");
            if (startTime < TimeSpan.Zero || startTime >= TimeSpan.FromDays(1) ||
                endTime <= startTime || endTime >= TimeSpan.FromDays(1) ||
                startTime.Ticks % TimeSpan.TicksPerMinute != 0 ||
                endTime.Ticks % TimeSpan.TicksPerMinute != 0)
                throw new ArgumentOutOfRangeException("startTime", "Choose times within the same day, in whole minutes, with start before end.");
            WorkWeekStart = workWeekStart;
            StartTime = startTime;
            EndTime = endTime;
        }

        public string Describe()
        {
            return WorkWeekStart.ToString() + "-" +
                (WorkWeekStart == DayOfWeek.Sunday ? "Thursday" : "Friday") + " " +
                StartTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture) + "-" +
                EndTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture) + ", local Windows time";
        }

        // A small, strict parser avoids a runtime dependency beyond the built-in .NET Framework.
        // Only the four documented fields are accepted; an invalid file must never enable a default schedule.
        public static bool TryParseConfig(string json, out WorkSchedule schedule)
        {
            schedule = null;
            if (json == null) return false;
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            int position = 0;
            SkipWhitespace(json, ref position);
            if (!Take(json, ref position, '{')) return false;
            while (true)
            {
                SkipWhitespace(json, ref position);
                if (Take(json, ref position, '}')) break;
                string key;
                if (!ReadPlainString(json, ref position, out key) || values.ContainsKey(key)) return false;
                SkipWhitespace(json, ref position);
                if (!Take(json, ref position, ':')) return false;
                SkipWhitespace(json, ref position);
                string value;
                if (key == "schemaVersion")
                {
                    // The schema is deliberately numeric rather than a quoted string.
                    if (!Take(json, ref position, '1')) return false;
                    value = "1";
                }
                else if (key == "workWeekStart" || key == "startTime" || key == "endTime")
                {
                    if (!ReadPlainString(json, ref position, out value)) return false;
                }
                else return false;
                values.Add(key, value);
                SkipWhitespace(json, ref position);
                if (Take(json, ref position, '}')) break;
                if (!Take(json, ref position, ',')) return false;
                SkipWhitespace(json, ref position);
                if (position < json.Length && json[position] == '}') return false;
            }
            SkipWhitespace(json, ref position);
            if (position != json.Length || values.Count != 4 || !values.ContainsKey("schemaVersion") ||
                !values.ContainsKey("workWeekStart") || !values.ContainsKey("startTime") ||
                !values.ContainsKey("endTime")) return false;
            DayOfWeek first;
            if (values["workWeekStart"] == "Sunday") first = DayOfWeek.Sunday;
            else if (values["workWeekStart"] == "Monday") first = DayOfWeek.Monday;
            else return false;
            TimeSpan start, end;
            if (!ReadTime(values["startTime"], out start) || !ReadTime(values["endTime"], out end) ||
                start >= end) return false;
            schedule = new WorkSchedule(first, start, end);
            return true;
        }

        private static bool ReadTime(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (value.Length != 5 || value[2] != ':' ||
                value[0] < '0' || value[0] > '9' || value[1] < '0' || value[1] > '9' ||
                value[3] < '0' || value[3] > '9' || value[4] < '0' || value[4] > '9') return false;
            int hours = (value[0] - '0') * 10 + value[1] - '0';
            int minutes = (value[3] - '0') * 10 + value[4] - '0';
            if (hours > 23 || minutes > 59) return false;
            time = new TimeSpan(hours, minutes, 0);
            return true;
        }

        private static bool ReadPlainString(string source, ref int position, out string value)
        {
            value = null;
            if (!Take(source, ref position, '"')) return false;
            int begin = position;
            while (position < source.Length && source[position] != '"')
            {
                // Escapes are unnecessary for the ASCII keys and values in this schema.
                if (source[position] == '\\' || source[position] < ' ') return false;
                position++;
            }
            if (position >= source.Length) return false;
            value = source.Substring(begin, position - begin);
            position++;
            return true;
        }

        private static bool Take(string source, ref int position, char expected)
        {
            if (position >= source.Length || source[position] != expected) return false;
            position++;
            return true;
        }

        private static void SkipWhitespace(string source, ref int position)
        {
            while (position < source.Length && (source[position] == ' ' || source[position] == '\t' ||
                source[position] == '\r' || source[position] == '\n')) position++;
        }
    }

    public static class Policy
    {
        public static bool ShouldShow(DateTime localTime)
        {
            return ShouldShow(localTime, WorkSchedule.Default);
        }

        public static bool ShouldShow(DateTime localTime, WorkSchedule schedule)
        {
            if (schedule == null) throw new ArgumentNullException("schedule");
            int day = ((int)localTime.DayOfWeek - (int)schedule.WorkWeekStart + 7) % 7;
            return day < 5 && localTime.TimeOfDay >= schedule.StartTime &&
                localTime.TimeOfDay < schedule.EndTime;
        }

        public static bool IsSlackExecutablePath(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath)) return false;
            string path = Environment.ExpandEnvironmentVariables(executablePath.Trim()).Trim();
            if (path.StartsWith("\"", StringComparison.Ordinal))
            {
                if (path.Length < 2 || !path.EndsWith("\"", StringComparison.Ordinal)) return false;
                path = path.Substring(1, path.Length - 2);
            }
            if (path.IndexOfAny(new char[] { '\0', '\r', '\n', '"' }) >= 0) return false;
            int separator = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return String.Equals(path.Substring(separator + 1), "slack.exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class ReconciliationResult
    {
        public int Examined;
        public int Changed;
        public int Errors;
    }

    public sealed class RestoreResult
    {
        public int Restored;
        public int Preserved;
        public int Discarded;
        public int Errors;
    }

    // Registry roots and policy inputs are injectable so tests never need the real tray keys.
    public static class RegistryController
    {
        private const string ValueName = "IsPromoted";

        public static ReconciliationResult Reconcile(RegistryKey iconRoot, RegistryKey backupRoot,
                                                      bool slackRunning, bool shouldShow)
        {
            ReconciliationResult result = new ReconciliationResult();
            if (!slackRunning || iconRoot == null) return result;
            int desired = shouldShow ? 1 : 0;
            foreach (string name in iconRoot.GetSubKeyNames())
            {
                try
                {
                    using (RegistryKey icon = iconRoot.OpenSubKey(name, true))
                    {
                        if (icon == null) continue;
                        string identity = ReadExecutablePath(icon);
                        if (!Policy.IsSlackExecutablePath(identity)) continue;
                        result.Examined++;
                        if (IsDword(icon, ValueName, desired)) continue;
                        if (backupRoot == null) { result.Errors++; continue; }
                        using (RegistryKey backup = PrepareBackup(backupRoot, name, icon, identity))
                        {
                            // Flush the original value and pending intent BEFORE modifying Explorer's key.
                            // PendingWrite also covers a crash between the icon write and LastWritten.
                            backup.SetValue("PendingWrite", desired, RegistryValueKind.DWord);
                            backup.Flush();
                            icon.SetValue(ValueName, desired, RegistryValueKind.DWord);
                            icon.Flush();
                            backup.SetValue("LastWritten", desired, RegistryValueKind.DWord);
                            backup.DeleteValue("PendingWrite", false);
                            backup.Flush();
                            result.Changed++;
                        }
                    }
                }
                catch (Exception error)
                {
                    if (IsFatal(error)) throw;
                    result.Errors++;
                }
            }
            return result;
        }

        public static RestoreResult Restore(RegistryKey iconRoot, RegistryKey backupRoot)
        {
            RestoreResult result = new RestoreResult();
            if (backupRoot == null) return result;
            foreach (string name in backupRoot.GetSubKeyNames())
            {
                try
                {
                    bool remove = false;
                    using (RegistryKey backup = backupRoot.OpenSubKey(name, false))
                    {
                        if (backup == null) continue;
                        ValidateBackup(backup);
                        using (RegistryKey icon = iconRoot == null ? null : iconRoot.OpenSubKey(name, true))
                        {
                            string identity = icon == null ? null : ReadExecutablePath(icon);
                            if (icon == null || !Policy.IsSlackExecutablePath(identity) ||
                                !String.Equals(identity, backup.GetValue("ExecutablePath") as string,
                                               StringComparison.OrdinalIgnoreCase))
                            {
                                // Explorer may recycle numeric keys or remove an uninstalled app.
                                result.Discarded++;
                                remove = true;
                            }
                            else if (!MatchesLastWrite(icon, backup))
                            {
                                // Preserve a newer user or application change instead of overwriting it.
                                result.Preserved++;
                                remove = true;
                            }
                            else
                            {
                                int hadValue = (int)backup.GetValue("HadValue");
                                if (hadValue == 0) icon.DeleteValue(ValueName, false);
                                else
                                {
                                    RegistryValueKind kind = (RegistryValueKind)(int)backup.GetValue("OriginalKind");
                                    object original = backup.GetValue("OriginalValue", null,
                                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                                    icon.SetValue(ValueName, original, kind);
                                }
                                icon.Flush();
                                result.Restored++;
                                remove = true;
                            }
                        }
                    }
                    if (remove)
                    {
                        backupRoot.DeleteSubKeyTree(name, false);
                        backupRoot.Flush();
                    }
                }
                catch (Exception error)
                {
                    if (IsFatal(error)) throw;
                    // Keep failed records so restore can be retried.
                    result.Errors++;
                }
            }
            return result;
        }

        public static string ReadExecutablePath(RegistryKey icon)
        {
            return icon.GetValue("ExecutablePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        private static RegistryKey PrepareBackup(RegistryKey root, string name, RegistryKey icon, string identity)
        {
            RegistryKey existing = root.OpenSubKey(name, true);
            if (existing != null)
            {
                try
                {
                    if (IsDword(existing, "Ready", 1))
                    {
                        ValidateBackup(existing);
                        if (String.Equals(identity, existing.GetValue("ExecutablePath") as string,
                                          StringComparison.OrdinalIgnoreCase)) return existing;
                    }
                    else if (HasValue(existing, "PendingWrite") || HasValue(existing, "LastWritten"))
                    {
                        throw new InvalidDataException("Incomplete backup contains write markers.");
                    }
                }
                catch
                {
                    existing.Dispose();
                    throw;
                }
                existing.Dispose();
                root.DeleteSubKeyTree(name, false);
            }

            bool hadValue = HasValue(icon, ValueName);
            RegistryValueKind kind = hadValue ? icon.GetValueKind(ValueName) : RegistryValueKind.None;
            if (hadValue && !SupportedKind(kind)) throw new InvalidDataException("Unsupported registry value kind.");
            object original = hadValue ? icon.GetValue(ValueName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
            if (hadValue && original == null) throw new InvalidDataException("Cannot read original value.");
            RegistryKey backup = root.CreateSubKey(name);
            if (backup == null) throw new IOException("Cannot create backup.");
            try
            {
                backup.SetValue("SchemaVersion", 1, RegistryValueKind.DWord);
                backup.SetValue("ExecutablePath", identity, RegistryValueKind.String);
                backup.SetValue("HadValue", hadValue ? 1 : 0, RegistryValueKind.DWord);
                if (hadValue)
                {
                    backup.SetValue("OriginalKind", (int)kind, RegistryValueKind.DWord);
                    backup.SetValue("OriginalValue", original, kind);
                }
                backup.SetValue("Ready", 1, RegistryValueKind.DWord);
                backup.Flush();
                return backup;
            }
            catch
            {
                backup.Dispose();
                throw;
            }
        }

        private static void ValidateBackup(RegistryKey backup)
        {
            if (!IsDword(backup, "Ready", 1) || !IsDword(backup, "SchemaVersion", 1) ||
                !Policy.IsSlackExecutablePath(backup.GetValue("ExecutablePath") as string) ||
                (!IsDword(backup, "HadValue", 0) && !IsDword(backup, "HadValue", 1)))
                throw new InvalidDataException("Invalid backup record.");
            if (IsDword(backup, "HadValue", 1))
            {
                if (!HasValue(backup, "OriginalKind") ||
                    backup.GetValueKind("OriginalKind") != RegistryValueKind.DWord)
                    throw new InvalidDataException("Missing original kind.");
                RegistryValueKind kind = (RegistryValueKind)(int)backup.GetValue("OriginalKind");
                if (!SupportedKind(kind) || !HasValue(backup, "OriginalValue") ||
                    backup.GetValueKind("OriginalValue") != kind)
                    throw new InvalidDataException("Invalid original value.");
            }
        }

        private static bool SupportedKind(RegistryValueKind kind)
        {
            return kind == RegistryValueKind.None || kind == RegistryValueKind.String ||
                   kind == RegistryValueKind.ExpandString || kind == RegistryValueKind.Binary ||
                   kind == RegistryValueKind.DWord || kind == RegistryValueKind.MultiString ||
                   kind == RegistryValueKind.QWord;
        }

        private static bool MatchesLastWrite(RegistryKey icon, RegistryKey backup)
        {
            if (!HasValue(icon, ValueName) || icon.GetValueKind(ValueName) != RegistryValueKind.DWord) return false;
            int value = (int)icon.GetValue(ValueName);
            if (value != 0 && value != 1) return false;
            return IsDword(backup, "LastWritten", value) || IsDword(backup, "PendingWrite", value);
        }

        public static bool HasValue(RegistryKey key, string name)
        {
            foreach (string candidate in key.GetValueNames())
                if (String.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsDword(RegistryKey key, string name, int value)
        {
            return HasValue(key, name) && key.GetValueKind(name) == RegistryValueKind.DWord &&
                   key.GetValue(name) is int && (int)key.GetValue(name) == value;
        }

        private static bool IsFatal(Exception error)
        {
            return error is OutOfMemoryException || error is StackOverflowException || error is ThreadAbortException;
        }
    }

    public static class Program
    {
        public const string Version = "0.3.0";
        private const string IconsPath = @"Control Panel\NotifyIconSettings";
        private const string BackupPath = @"Software\SlackTrayHours\Backup";
        private static string lastLogState;

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--stop") return StopWorker();
                if (args.Length == 1 && args[0] == "--restore") return Restore();
                if (args.Length == 1 && args[0] == "--status")
                {
                    Console.WriteLine(GetStatus());
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--status-file")
                {
                    if (!Path.IsPathRooted(args[1])) return 3;
                    File.WriteAllText(args[1], GetStatus(), new UTF8Encoding(false));
                    return 0;
                }
                bool once = args.Length == 1 && args[0] == "--once";
                if (args.Length != 0 && !once) return 3;
                int build;
                if (!SupportedWindows(out build))
                {
                    Log("Unsupported Windows; Windows 11 build 22621 or later is required.");
                    return 2;
                }
                if (once) return ReconcileOnce();
                return Run();
            }
            catch (Exception error)
            {
                Log("Operation failed (" + error.GetType().Name + ").");
                return 1;
            }
        }

        private static int Run()
        {
            using (Mutex worker = new Mutex(false, ObjectName("Worker")))
            {
                if (!Acquire(worker, 0)) return 0;
                try
                {
                    using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, ObjectName("Stop")))
                    {
                        Log("Started version " + Version + ".");
                        do
                        {
                            try { ReconcileOnce(); }
                            catch (Exception error) { LogState("Reconciliation failed (" + error.GetType().Name + ")."); }
                        } while (!stop.WaitOne(5000));
                        Log("Stopped.");
                    }
                }
                finally { worker.ReleaseMutex(); }
            }
            return 0;
        }

        private static int ReconcileOnce()
        {
            WorkSchedule schedule;
            string source;
            if (!TryLoadSchedule(out schedule, out source))
            {
                LogState("Schedule configuration is invalid or unreadable; no icon changes.");
                return 1;
            }
            bool running = SlackRunningInCurrentSession();
            bool show = Policy.ShouldShow(DateTime.Now, schedule);
            if (!running)
            {
                LogState("Slack is not running in this session; no icon changes.");
                return 0;
            }
            using (Mutex gate = new Mutex(false, ObjectName("State")))
            {
                if (!Acquire(gate, 10000)) { LogState("State is busy; retrying on next check."); return 1; }
                try
                {
                    using (RegistryKey icons = Registry.CurrentUser.OpenSubKey(IconsPath, true))
                    {
                        if (icons == null) { LogState("No notification icon settings exist yet."); return 0; }
                        using (RegistryKey backup = Registry.CurrentUser.CreateSubKey(BackupPath))
                        {
                            ReconciliationResult result = RegistryController.Reconcile(icons, backup, running, show);
                            string state = "Desired=" + (show ? "shown" : "hidden") +
                                "; matching icons=" + result.Examined + "; changed=" + result.Changed +
                                "; errors=" + result.Errors + ".";
                            LogState(state);
                            return result.Errors == 0 ? 0 : 1;
                        }
                    }
                }
                finally { gate.ReleaseMutex(); }
            }
        }

        private static int StopWorker()
        {
            Mutex worker;
            try { worker = Mutex.OpenExisting(ObjectName("Worker")); }
            catch (WaitHandleCannotBeOpenedException) { return 0; }
            using (worker)
            {
                if (Acquire(worker, 0)) { worker.ReleaseMutex(); return 0; }
                EventWaitHandle stop = null;
                Stopwatch clock = Stopwatch.StartNew();
                while (stop == null && clock.ElapsedMilliseconds < 15000)
                {
                    try { stop = EventWaitHandle.OpenExisting(ObjectName("Stop")); }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        if (Acquire(worker, 100)) { worker.ReleaseMutex(); return 0; }
                    }
                }
                if (stop == null) return 4;
                using (stop) { stop.Set(); }
                int remaining = Math.Max(0, 15000 - (int)clock.ElapsedMilliseconds);
                if (!Acquire(worker, remaining)) return 4;
                worker.ReleaseMutex();
                return 0;
            }
        }

        private static int Restore()
        {
            int stopped = StopWorker();
            if (stopped != 0) return stopped;
            using (Mutex worker = new Mutex(false, ObjectName("Worker")))
            {
                // Prevent a new daemon from starting in the gap between stop and restore.
                if (!Acquire(worker, 0)) return 4;
                try
                {
                    using (Mutex gate = new Mutex(false, ObjectName("State")))
                    {
                        if (!Acquire(gate, 15000)) return 4;
                        try
                        {
                            using (RegistryKey icons = Registry.CurrentUser.OpenSubKey(IconsPath, true))
                            using (RegistryKey backups = Registry.CurrentUser.OpenSubKey(BackupPath, true))
                            {
                                RestoreResult result = RegistryController.Restore(icons, backups);
                                Log("Restore: restored=" + result.Restored + "; preserved=" + result.Preserved +
                                    "; stale backups discarded=" + result.Discarded + "; errors=" + result.Errors + ".");
                                return result.Errors == 0 ? 0 : 1;
                            }
                        }
                        finally { gate.ReleaseMutex(); }
                    }
                }
                finally { worker.ReleaseMutex(); }
            }
        }

        private static bool Acquire(Mutex mutex, int milliseconds)
        {
            try { return mutex.WaitOne(milliseconds); }
            catch (AbandonedMutexException) { return true; }
        }

        private static string ObjectName(string suffix)
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User == null) throw new InvalidOperationException("No user identity.");
                return @"Global\SlackTrayHours." + suffix + "." + identity.User.Value;
            }
        }

        private static bool SlackRunningInCurrentSession()
        {
            int session;
            using (Process current = Process.GetCurrentProcess()) { session = current.SessionId; }
            bool found = false;
            Process[] candidates = Process.GetProcessesByName("slack");
            foreach (Process process in candidates)
            {
                using (process)
                {
                    try { if (!process.HasExited && process.SessionId == session) found = true; }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            return found;
        }

        private static bool TryLoadSchedule(out WorkSchedule schedule, out string source)
        {
            schedule = null;
            source = "config.json";
            try
            {
                string configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SlackTrayHours", "config.json");
                try
                {
                    if ((File.GetAttributes(configPath) & FileAttributes.Directory) != 0) return false;
                }
                catch (FileNotFoundException)
                {
                    schedule = WorkSchedule.Default;
                    source = "default (no config.json)";
                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    schedule = WorkSchedule.Default;
                    source = "default (no config.json)";
                    return true;
                }
                string contents = File.ReadAllText(configPath, new UTF8Encoding(false, true));
                return WorkSchedule.TryParseConfig(contents, out schedule);
            }
            catch (Exception error)
            {
                if (error is OutOfMemoryException || error is StackOverflowException ||
                    error is ThreadAbortException) throw;
                return false;
            }
        }

        private static string GetStatus()
        {
            int build;
            bool supported = SupportedWindows(out build);
            StringBuilder result = new StringBuilder();
            result.AppendLine("Slack Tray Hours " + Version);
            result.AppendLine("Local time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            WorkSchedule schedule;
            string source;
            if (TryLoadSchedule(out schedule, out source))
            {
                result.AppendLine("Schedule: " + schedule.Describe() + " (" + source + ")");
                result.AppendLine("Desired icon state: " +
                    (Policy.ShouldShow(DateTime.Now, schedule) ? "shown" : "hidden"));
            }
            else
            {
                result.AppendLine("Schedule: invalid or unreadable config.json; icon changes paused");
                result.AppendLine("Desired icon state: unavailable");
            }
            result.AppendLine("Supported Windows build: " + (supported ? "yes" : "no") + " (" + build + ")");
            result.AppendLine("Slack running in this session: " + (SlackRunningInCurrentSession() ? "yes" : "no"));
            bool workerRunning = false;
            try
            {
                using (Mutex worker = Mutex.OpenExisting(ObjectName("Worker")))
                {
                    workerRunning = !Acquire(worker, 0);
                    if (!workerRunning) worker.ReleaseMutex();
                }
            }
            catch (WaitHandleCannotBeOpenedException) { }
            result.AppendLine("Background worker: " + (workerRunning ? "running" : "stopped"));
            int shown = 0, hidden = 0, other = 0;
            using (RegistryKey icons = Registry.CurrentUser.OpenSubKey(IconsPath, false))
            {
                if (icons != null)
                {
                    foreach (string name in icons.GetSubKeyNames())
                    {
                        using (RegistryKey icon = icons.OpenSubKey(name, false))
                        {
                            if (icon == null || !Policy.IsSlackExecutablePath(RegistryController.ReadExecutablePath(icon))) continue;
                            object value = icon.GetValue("IsPromoted");
                            if (value is int && (int)value == 1) shown++;
                            else if (value is int && (int)value == 0) hidden++;
                            else other++;
                        }
                    }
                }
            }
            result.AppendLine("Matching icon entries: shown=" + shown + ", hidden=" + hidden + ", unset/other=" + other);
            using (RegistryKey backups = Registry.CurrentUser.OpenSubKey(BackupPath, false))
                result.AppendLine("Saved original icon preferences: " + (backups == null ? 0 : backups.SubKeyCount));
            return result.ToString();
        }

        private static void LogState(string message)
        {
            if (String.Equals(lastLogState, message, StringComparison.Ordinal)) return;
            lastLogState = message;
            Log(message);
        }

        private static void Log(string message)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlackTrayHours");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "runtime.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 65536)
                {
                    string previous = path + ".1";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(path, previous);
                }
                // Messages intentionally contain no account names or executable paths.
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    " " + message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct VersionInfo
        {
            public uint Size;
            public uint Major;
            public uint Minor;
            public uint Build;
            public uint Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ServicePack;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref VersionInfo version);

        private static bool SupportedWindows(out int build)
        {
            build = 0;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            try
            {
                VersionInfo version = new VersionInfo();
                version.Size = (uint)Marshal.SizeOf(typeof(VersionInfo));
                if (RtlGetVersion(ref version) != 0) return false;
                build = (int)version.Build;
                return version.Major == 10 && version.Build >= 22621;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }
}
