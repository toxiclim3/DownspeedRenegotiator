using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DownswitchRenegotiator
{
    internal enum LinkState
    {
        Unknown,
        Up,
        Down,
        Error
    }

    internal sealed class LinkStatus
    {
        public LinkState State;
        public int SpeedMbps;
        public string Duplex;
        public string RawOutput;
        public string ErrorMessage;

        public LinkStatus(LinkState state, int speedMbps, string duplex, string rawOutput, string errorMessage)
        {
            State = state;
            SpeedMbps = speedMbps;
            Duplex = duplex ?? "";
            RawOutput = rawOutput ?? "";
            ErrorMessage = errorMessage ?? "";
        }

        public string FormatSummary(int wanPort)
        {
            if (State == LinkState.Up)
            {
                if (!string.IsNullOrEmpty(Duplex))
                {
                    return string.Format("WAN port {0}: Up {1} Mbps ({2})", wanPort, SpeedMbps, Duplex);
                }
                return string.Format("WAN port {0}: Up {1} Mbps", wanPort, SpeedMbps);
            }
            if (State == LinkState.Down)
            {
                return string.Format("WAN port {0}: Down", wanPort);
            }
            if (State == LinkState.Error)
            {
                return string.Format("WAN port {0}: Error ({1})", wanPort, string.IsNullOrEmpty(ErrorMessage) ? "unknown" : ErrorMessage);
            }
            return string.Format("WAN port {0}: Unknown", wanPort);
        }

        public string FormatShortStatus()
        {
            if (State == LinkState.Up)
            {
                if (!string.IsNullOrEmpty(Duplex))
                {
                    return string.Format("{0} Mbps ({1})", SpeedMbps, Duplex);
                }
                return string.Format("{0} Mbps", SpeedMbps);
            }
            if (State == LinkState.Down)
            {
                return "Link down";
            }
            if (State == LinkState.Error)
            {
                return "SSH error";
            }
            return "Checking...";
        }
    }

    internal sealed class AppConfig
    {
        private static readonly Regex HostRegex = new Regex("^[A-Za-z0-9][A-Za-z0-9.\\-:]*$", RegexOptions.Compiled);
        private static readonly Regex UserRegex = new Regex("^[A-Za-z0-9_][A-Za-z0-9._-]*$", RegexOptions.Compiled);

        public const string DefaultTemplateFileName = "default_config.ini";

        public string BaseDir;
        public string ConfigPath;
        public string Host = "192.168.1.1";
        public int Port = 22;
        public string User = "admin";
        public string KeyFile = "ssh\\id_ed25519";
        public string KnownHosts = "ssh\\known_hosts";
        public int WanPort = 0;
        public int TargetSpeedMbps = 1000;
        public int PollSeconds = 300;
        public bool AutoRenegotiate = true;
        public bool NotifyOnDownswitch = true;
        public bool NotifyOnRenegotiateSuccess = true;
        public bool NotifyOnRenegotiateBelowTarget = true;
        public bool NotifyOnRenegotiateError = true;
        public bool NotifyOnSettingsError = true;
        public int CooldownSeconds = 120;
        public int MaxAutoRetries = 3;

        public string ResolvedKeyPath
        {
            get { return ResolvePath(KeyFile); }
        }

        public string ResolvedKnownHostsPath
        {
            get { return ResolvePath(KnownHosts); }
        }

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini"); }
        }

        public string ResolvePath(string pathValue)
        {
            if (Path.IsPathRooted(pathValue))
            {
                return pathValue;
            }
            string root = string.IsNullOrEmpty(BaseDir) ? AppDomain.CurrentDomain.BaseDirectory : BaseDir;
            return Path.GetFullPath(Path.Combine(root, pathValue));
        }

        public static AppConfig Load(string path)
        {
            bool createdFromDefault;
            return Load(path, out createdFromDefault);
        }

        public static AppConfig Load(string path, out bool createdFromDefault)
        {
            createdFromDefault = false;
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                string dir = Path.GetDirectoryName(fullPath);
                string templatePath = string.IsNullOrEmpty(dir)
                    ? DefaultTemplateFileName
                    : Path.Combine(dir, DefaultTemplateFileName);
                if (!File.Exists(templatePath))
                {
                    throw new FileNotFoundException(
                        string.Format("Configuration file not found: {0} (default template also missing: {1})", fullPath, templatePath),
                        fullPath);
                }

                AppConfig templateCfg = ParseFile(templatePath, fullPath);
                try
                {
                    File.Copy(templatePath, fullPath, false);
                    createdFromDefault = true;
                    return templateCfg;
                }
                catch (IOException)
                {
                    if (!File.Exists(fullPath))
                    {
                        throw;
                    }
                }
            }

            return ParseFile(fullPath, fullPath);
        }

        private static AppConfig ParseFile(string sourcePath, string configPath)
        {
            AppConfig cfg = new AppConfig();
            cfg.ConfigPath = configPath;
            cfg.BaseDir = Path.GetDirectoryName(configPath);

            string[] lines = File.ReadAllLines(sourcePath, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    throw new FormatException(string.Format("Invalid config line {0}: {1}", i + 1, line));
                }
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                ApplySetting(cfg, key, val, i + 1);
            }

            Validate(cfg);
            return cfg;
        }

        private static bool ParseBoolSetting(string key, string val, int lineNumber)
        {
            bool boolVal;
            if (!bool.TryParse(val, out boolVal))
            {
                throw new FormatException(string.Format("Invalid {0} on line {1}", key, lineNumber));
            }
            return boolVal;
        }

        private static void ApplySetting(AppConfig cfg, string key, string val, int lineNumber)
        {
            int intVal;
            switch (key)
            {
                case "host":
                    cfg.Host = val;
                    break;
                case "port":
                    if (!int.TryParse(val, out intVal))
                    {
                        throw new FormatException(string.Format("Invalid port on line {0}", lineNumber));
                    }
                    cfg.Port = intVal;
                    break;
                case "user":
                    cfg.User = val;
                    break;
                case "key_file":
                    cfg.KeyFile = val;
                    break;
                case "known_hosts":
                    cfg.KnownHosts = val;
                    break;
                case "wan_port":
                    if (!int.TryParse(val, out intVal))
                    {
                        throw new FormatException(string.Format("Invalid wan_port on line {0}", lineNumber));
                    }
                    cfg.WanPort = intVal;
                    break;
                case "target_speed_mbps":
                    if (!int.TryParse(val, out intVal))
                    {
                        throw new FormatException(string.Format("Invalid target_speed_mbps on line {0}", lineNumber));
                    }
                    cfg.TargetSpeedMbps = intVal;
                    break;
                case "poll_seconds":
                    if (!int.TryParse(val, out intVal))
                    {
                        throw new FormatException(string.Format("Invalid poll_seconds on line {0}", lineNumber));
                    }
                    cfg.PollSeconds = intVal;
                    break;
                case "auto_renegotiate":
                    cfg.AutoRenegotiate = ParseBoolSetting(key, val, lineNumber);
                    break;
                case "notify_on_downswitch":
                    cfg.NotifyOnDownswitch = ParseBoolSetting(key, val, lineNumber);
                    break;
                case "notify_on_renegotiate_success":
                    cfg.NotifyOnRenegotiateSuccess = ParseBoolSetting(key, val, lineNumber);
                    break;
                case "notify_on_renegotiate_below_target":
                    cfg.NotifyOnRenegotiateBelowTarget = ParseBoolSetting(key, val, lineNumber);
                    break;
                case "notify_on_renegotiate_error":
                    cfg.NotifyOnRenegotiateError = ParseBoolSetting(key, val, lineNumber);
                    break;
                case "notify_on_settings_error":
                    cfg.NotifyOnSettingsError = ParseBoolSetting(key, val, lineNumber);
                    break;
                case "cooldown_seconds":
                    if (!int.TryParse(val, out intVal))
                    {
                        throw new FormatException(string.Format("Invalid cooldown_seconds on line {0}", lineNumber));
                    }
                    cfg.CooldownSeconds = intVal;
                    break;
                case "max_auto_retries":
                    if (!int.TryParse(val, out intVal))
                    {
                        throw new FormatException(string.Format("Invalid max_auto_retries on line {0}", lineNumber));
                    }
                    cfg.MaxAutoRetries = intVal;
                    break;
                default:
                    throw new FormatException(string.Format("Unknown config key '{0}' on line {1}", key, lineNumber));
            }
        }

        public static void Validate(AppConfig cfg)
        {
            if (string.IsNullOrEmpty(cfg.Host) || !HostRegex.IsMatch(cfg.Host))
            {
                throw new InvalidOperationException("Invalid host in config.ini: " + cfg.Host);
            }
            if (cfg.Port < 1 || cfg.Port > 65535)
            {
                throw new InvalidOperationException("port must be between 1 and 65535.");
            }
            if (string.IsNullOrEmpty(cfg.User) || !UserRegex.IsMatch(cfg.User))
            {
                throw new InvalidOperationException("Invalid user in config.ini: " + cfg.User);
            }
            if (!IsValidSafePathValue(cfg.KeyFile))
            {
                throw new InvalidOperationException("Invalid key_file in config.ini.");
            }
            if (!IsValidSafePathValue(cfg.KnownHosts))
            {
                throw new InvalidOperationException("Invalid known_hosts in config.ini.");
            }
            if (cfg.WanPort < 0 || cfg.WanPort > 31)
            {
                throw new InvalidOperationException("wan_port must be between 0 and 31.");
            }
            if (cfg.TargetSpeedMbps < 100)
            {
                throw new InvalidOperationException("target_speed_mbps must be >= 100.");
            }
            if (cfg.PollSeconds < 5)
            {
                throw new InvalidOperationException("poll_seconds must be >= 5.");
            }
            if (cfg.CooldownSeconds < 10)
            {
                throw new InvalidOperationException("cooldown_seconds must be >= 10.");
            }
            if (cfg.MaxAutoRetries < 1)
            {
                throw new InvalidOperationException("max_auto_retries must be >= 1.");
            }
        }

        private static bool IsValidSafePathValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            return value.IndexOf('"') < 0 && value.IndexOf('\r') < 0 && value.IndexOf('\n') < 0;
        }

        public static void SaveBoolSetting(string path, string key, bool enabled)
        {
            SaveSetting(path, key, enabled ? "true" : "false");
        }

        public static void SaveIntSetting(string path, string key, int value)
        {
            SaveSetting(path, key, value.ToString());
        }

        public static void SaveSetting(string path, string key, string value)
        {
            string valueLine = key + "=" + value;
            List<string> updated = new List<string>();
            bool replaced = false;

            if (File.Exists(path))
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].Trim();
                    if (!trimmed.StartsWith("#") && !trimmed.StartsWith(";") &&
                        trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = trimmed.IndexOf('=');
                        if (eq > 0 && trimmed.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            updated.Add(valueLine);
                            replaced = true;
                            continue;
                        }
                    }
                    updated.Add(lines[i]);
                }
            }

            if (!replaced)
            {
                updated.Add(valueLine);
            }

            File.WriteAllLines(path, updated.ToArray(), new UTF8Encoding(false));
        }
    }

    internal sealed class AutoRetryPolicy
    {
        public int ConsecutiveFailures;
        public bool PausedForDrop;
        public bool DownswitchActive;
        public DateTime LastAttemptUtc = DateTime.MinValue;

        public void ResetRecovery()
        {
            ConsecutiveFailures = 0;
            PausedForDrop = false;
            DownswitchActive = false;
        }

        public bool ObserveDownswitch(LinkStatus status, int targetSpeedMbps)
        {
            if (status == null)
            {
                return false;
            }
            if (status.State == LinkState.Up && status.SpeedMbps >= targetSpeedMbps)
            {
                ResetRecovery();
                return false;
            }
            if (status.State == LinkState.Up && status.SpeedMbps > 0 && status.SpeedMbps < targetSpeedMbps)
            {
                if (!DownswitchActive)
                {
                    DownswitchActive = true;
                    return true;
                }
                return false;
            }
            return false;
        }

        public bool ShouldTrigger(bool autoEnabled, LinkStatus status, int targetSpeedMbps, int cooldownSeconds, int maxAutoRetries, DateTime utcNow)
        {
            if (status == null)
            {
                return false;
            }

            if (status.State == LinkState.Up && status.SpeedMbps >= targetSpeedMbps)
            {
                ResetRecovery();
                return false;
            }

            if (!autoEnabled)
            {
                return false;
            }
            if (status.State != LinkState.Up)
            {
                return false;
            }
            if (status.SpeedMbps <= 0 || status.SpeedMbps >= targetSpeedMbps)
            {
                return false;
            }
            if (PausedForDrop || ConsecutiveFailures >= maxAutoRetries)
            {
                PausedForDrop = true;
                return false;
            }
            if (LastAttemptUtc != DateTime.MinValue && (utcNow - LastAttemptUtc).TotalSeconds < cooldownSeconds)
            {
                return false;
            }
            return true;
        }

        public void RecordAttempt(DateTime utcNow, int maxAutoRetries)
        {
            LastAttemptUtc = utcNow;
            ConsecutiveFailures++;
            if (ConsecutiveFailures >= maxAutoRetries)
            {
                PausedForDrop = true;
            }
        }

        public void ResetForManual(DateTime utcNow)
        {
            ConsecutiveFailures = 0;
            PausedForDrop = false;
            LastAttemptUtc = utcNow;
        }
    }

    internal sealed class SshResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;
        public bool TimedOut;

        public SshResult(int exitCode, string stdOut, string stdErr, bool timedOut)
        {
            ExitCode = exitCode;
            StdOut = stdOut ?? "";
            StdErr = stdErr ?? "";
            TimedOut = timedOut;
        }
    }

    internal static class SwconfigParser
    {
        private static readonly Regex LinkDownRegex = new Regex(@"\blink:down\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LinkUpRegex = new Regex(@"\blink:up\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SpeedRegex = new Regex(@"speed:(\d+)baseT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DuplexRegex = new Regex(@"\b(full|half)-duplex\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static LinkStatus ParseSwconfigLink(string output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return new LinkStatus(LinkState.Error, 0, "", output, "Empty output");
            }
            string trimmed = output.Trim();
            if (trimmed.Length == 0)
            {
                return new LinkStatus(LinkState.Error, 0, "", output, "Empty output");
            }

            if (LinkDownRegex.IsMatch(trimmed))
            {
                return new LinkStatus(LinkState.Down, 0, "", trimmed, "");
            }

            if (LinkUpRegex.IsMatch(trimmed))
            {
                Match speedMatch = SpeedRegex.Match(trimmed);
                if (speedMatch.Success)
                {
                    int speed;
                    if (int.TryParse(speedMatch.Groups[1].Value, out speed) && speed > 0)
                    {
                        Match duplexMatch = DuplexRegex.Match(trimmed);
                        string duplex = duplexMatch.Success ? duplexMatch.Value.ToLowerInvariant() : "";
                        return new LinkStatus(LinkState.Up, speed, duplex, trimmed, "");
                    }
                }
            }

            return new LinkStatus(LinkState.Error, 0, "", trimmed, "Unrecognized swconfig output: " + trimmed);
        }
    }

    internal static class SshRunner
    {
        public static string BuildCheckCommand(int wanPort)
        {
            return string.Format("swconfig dev switch0 port {0} get link", wanPort);
        }

        public static string BuildRenegotiateCommand(int wanPort)
        {
            return string.Format(
                "trap '' HUP; mii_mgr -s -p {0} -r 0 -v 0x3900; sleep 2; mii_mgr -s -p {0} -r 0 -v 0x3100; sleep 1; mii_mgr -s -p {0} -r 0 -v 0x3300",
                wanPort);
        }

        public static string ResolveSshExe()
        {
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string openSsh = Path.Combine(sysDir, "OpenSSH\\ssh.exe");
            if (File.Exists(openSsh))
            {
                return openSsh;
            }
            return "ssh.exe";
        }

        public static string BuildSshArguments(AppConfig cfg, string remoteCommand)
        {
            return string.Format(
                "-i \"{0}\" -p {1} -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o \"UserKnownHostsFile={2}\" -o ConnectTimeout=5 -o ServerAliveInterval=5 -o ServerAliveCountMax=2 {3}@{4} \"{5}\"",
                cfg.KeyFile,
                cfg.Port,
                cfg.KnownHosts,
                cfg.User,
                cfg.Host,
                remoteCommand);
        }

        public static void EnsureKeyPermissions(string fullPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                {
                    return;
                }

                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                SecurityIdentifier currentUser = identity != null ? identity.User : null;
                if (currentUser == null)
                {
                    return;
                }

                SecurityIdentifier sysSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                SecurityIdentifier admSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

                FileSecurity existing = File.GetAccessControl(fullPath);
                bool needsFix = !existing.AreAccessRulesProtected;
                if (!needsFix)
                {
                    AuthorizationRuleCollection rules = existing.GetAccessRules(true, true, typeof(SecurityIdentifier));
                    foreach (FileSystemAccessRule rule in rules)
                    {
                        SecurityIdentifier sid = rule.IdentityReference as SecurityIdentifier;
                        if (rule.AccessControlType != AccessControlType.Allow || sid == null ||
                            (!sid.Equals(sysSid) && !sid.Equals(admSid) && !sid.Equals(currentUser)))
                        {
                            needsFix = true;
                            break;
                        }
                    }
                }

                if (!needsFix)
                {
                    return;
                }

                FileSecurity clean = new FileSecurity();
                clean.SetAccessRuleProtection(true, false);
                clean.AddAccessRule(new FileSystemAccessRule(sysSid, FileSystemRights.FullControl, AccessControlType.Allow));
                clean.AddAccessRule(new FileSystemAccessRule(admSid, FileSystemRights.FullControl, AccessControlType.Allow));
                clean.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
                File.SetAccessControl(fullPath, clean);
            }
            catch
            {
                // Best-effort only so portable folders on non-NTFS or restricted shares still run.
            }
        }

        public static string EnsureKnownHostsParentDirectory(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return "known_hosts path is empty.";
            }
            if (Directory.Exists(fullPath))
            {
                return "known_hosts path is a directory: " + fullPath;
            }
            try
            {
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return null;
            }
            catch (Exception ex)
            {
                return "Could not create known_hosts directory: " + ex.Message;
            }
        }

        public static SshResult RunSsh(AppConfig cfg, string remoteCommand, int timeoutMs)
        {
            if (!File.Exists(cfg.ResolvedKeyPath))
            {
                return new SshResult(1, "", "Private key not found: " + cfg.ResolvedKeyPath, false);
            }
            string knownHostsErr = EnsureKnownHostsParentDirectory(cfg.ResolvedKnownHostsPath);
            if (knownHostsErr != null)
            {
                return new SshResult(1, "", knownHostsErr, false);
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = ResolveSshExe();
            psi.Arguments = BuildSshArguments(cfg, remoteCommand);
            psi.WorkingDirectory = string.IsNullOrEmpty(cfg.BaseDir) ? AppDomain.CurrentDomain.BaseDirectory : cfg.BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();

            using (Process proc = new Process())
            {
                proc.StartInfo = psi;
                proc.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (stdout)
                        {
                            stdout.AppendLine(e.Data);
                        }
                    }
                };
                proc.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (stderr)
                        {
                            stderr.AppendLine(e.Data);
                        }
                    }
                };

                proc.Start();
                try
                {
                    proc.StandardInput.Close();
                }
                catch
                {
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                bool exited = proc.WaitForExit(timeoutMs);
                if (!exited)
                {
                    try
                    {
                        proc.Kill();
                    }
                    catch
                    {
                    }
                    return new SshResult(-1, stdout.ToString().Trim(), "SSH command timed out after " + timeoutMs + " ms.", true);
                }

                // Flush async output/error handlers.
                proc.WaitForExit();
                return new SshResult(proc.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim(), false);
            }
        }

        public static LinkStatus CheckLink(AppConfig cfg)
        {
            SshResult res = RunSsh(cfg, BuildCheckCommand(cfg.WanPort), 12000);
            if (res.TimedOut || res.ExitCode != 0)
            {
                string err = !string.IsNullOrEmpty(res.StdErr) ? res.StdErr : (!string.IsNullOrEmpty(res.StdOut) ? res.StdOut : ("Exit code " + res.ExitCode));
                return new LinkStatus(LinkState.Error, 0, "", res.StdOut, FirstErrorLine(err));
            }
            return SwconfigParser.ParseSwconfigLink(res.StdOut);
        }

        public static string FirstErrorLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string firstNonEmpty = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
                if (firstNonEmpty.Length == 0)
                {
                    firstNonEmpty = trimmed;
                }
                if (!trimmed.StartsWith("Warning: Permanently added ", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed;
                }
            }
            return firstNonEmpty;
        }
    }

    internal static class AutorunHelper
    {
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string ValueName = "DownswitchRenegotiator";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    object raw = key.GetValue(ValueName);
                    if (raw == null)
                    {
                        return false;
                    }
                    string expected = "\"" + Application.ExecutablePath + "\"";
                    string actual = raw.ToString().Trim();
                    return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(actual, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Could not open HKCU Run registry key.");
                }
                if (enable)
                {
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly AppConfig _cfg;
        private readonly Mutex _mutex;
        private readonly AutoRetryPolicy _policy = new AutoRetryPolicy();
        private readonly Control _invoker;
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Forms.Timer _pollTimer;

        private static readonly int[] PollPresetSeconds = new int[] { 60, 300, 900, 1800, 3600, 7200, 18000, 86400 };

        private sealed class NotificationOption
        {
            public readonly string Label;
            public readonly string ConfigKey;
            public readonly Func<AppConfig, bool> Getter;
            public readonly Action<AppConfig, bool> Setter;

            public NotificationOption(string label, string configKey, Func<AppConfig, bool> getter, Action<AppConfig, bool> setter)
            {
                Label = label;
                ConfigKey = configKey;
                Getter = getter;
                Setter = setter;
            }
        }

        private static readonly NotificationOption[] NotificationOptions = new NotificationOption[]
        {
            new NotificationOption(
                "Downswitch detected",
                "notify_on_downswitch",
                delegate(AppConfig c) { return c.NotifyOnDownswitch; },
                delegate(AppConfig c, bool v) { c.NotifyOnDownswitch = v; }),
            new NotificationOption(
                "Renegotiate succeeded",
                "notify_on_renegotiate_success",
                delegate(AppConfig c) { return c.NotifyOnRenegotiateSuccess; },
                delegate(AppConfig c, bool v) { c.NotifyOnRenegotiateSuccess = v; }),
            new NotificationOption(
                "Renegotiate still below target",
                "notify_on_renegotiate_below_target",
                delegate(AppConfig c) { return c.NotifyOnRenegotiateBelowTarget; },
                delegate(AppConfig c, bool v) { c.NotifyOnRenegotiateBelowTarget = v; }),
            new NotificationOption(
                "Renegotiate error",
                "notify_on_renegotiate_error",
                delegate(AppConfig c) { return c.NotifyOnRenegotiateError; },
                delegate(AppConfig c, bool v) { c.NotifyOnRenegotiateError = v; }),
            new NotificationOption(
                "Config & autorun errors",
                "notify_on_settings_error",
                delegate(AppConfig c) { return c.NotifyOnSettingsError; },
                delegate(AppConfig c, bool v) { c.NotifyOnSettingsError = v; })
        };

        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _renegotiateItem;
        private readonly ToolStripMenuItem _checkNowItem;
        private readonly ToolStripMenuItem _pollRateMenu;
        private readonly List<ToolStripMenuItem> _pollRateItems = new List<ToolStripMenuItem>();
        private readonly ToolStripMenuItem _autoItem;
        private readonly ToolStripMenuItem _notificationsMenu;
        private readonly List<ToolStripMenuItem> _notificationItems = new List<ToolStripMenuItem>();
        private readonly ToolStripMenuItem _autorunItem;

        private Icon _currentIcon;
        private bool _busy;
        private bool _disposed;

        public TrayApp(AppConfig cfg, Mutex mutex)
        {
            _cfg = cfg;
            _mutex = mutex;

            _invoker = new Control();
            IntPtr handle = _invoker.Handle;
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create UI marshaling handle.");
            }

            ContextMenuStrip menu = new ContextMenuStrip();
            _statusItem = new ToolStripMenuItem("WAN: Checking...");
            _statusItem.Enabled = false;

            _renegotiateItem = new ToolStripMenuItem("Renegotiate WAN now", null, delegate(object s, EventArgs e)
            {
                StartRenegotiate(false);
            });

            _checkNowItem = new ToolStripMenuItem("Check speed now", null, delegate(object s, EventArgs e)
            {
                StartCheck();
            });

            _pollRateMenu = new ToolStripMenuItem();
            for (int i = 0; i < PollPresetSeconds.Length; i++)
            {
                int seconds = PollPresetSeconds[i];
                ToolStripMenuItem presetItem = new ToolStripMenuItem(FormatIntervalLong(seconds));
                presetItem.Tag = seconds;
                presetItem.Click += OnSelectPollRate;
                _pollRateItems.Add(presetItem);
                _pollRateMenu.DropDownItems.Add(presetItem);
            }
            UpdatePollRateMenu();

            _autoItem = new ToolStripMenuItem(string.Format("Auto-renegotiate (< {0} Mbps)", _cfg.TargetSpeedMbps));
            _autoItem.CheckOnClick = true;
            _autoItem.Checked = _cfg.AutoRenegotiate;
            _autoItem.Click += OnToggleAuto;

            _notificationsMenu = new ToolStripMenuItem();
            for (int i = 0; i < NotificationOptions.Length; i++)
            {
                NotificationOption opt = NotificationOptions[i];
                ToolStripMenuItem notifyItem = new ToolStripMenuItem(opt.Label);
                notifyItem.CheckOnClick = true;
                notifyItem.Tag = opt;
                notifyItem.Click += OnToggleNotification;
                _notificationItems.Add(notifyItem);
                _notificationsMenu.DropDownItems.Add(notifyItem);
            }
            UpdateNotificationsMenu();

            _autorunItem = new ToolStripMenuItem("Start with Windows");
            _autorunItem.CheckOnClick = true;
            _autorunItem.Checked = AutorunHelper.IsEnabled();
            _autorunItem.Click += OnToggleAutorun;

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit", null, delegate(object s, EventArgs e)
            {
                ExitApplication();
            });

            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_renegotiateItem);
            menu.Items.Add(_checkNowItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_pollRateMenu);
            menu.Items.Add(_autoItem);
            menu.Items.Add(_notificationsMenu);
            menu.Items.Add(_autorunItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon();
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.Text = SafeTooltip("WAN: Checking...");
            SetBadgeIcon("...", Color.FromArgb(30, 120, 210));
            _notifyIcon.DoubleClick += delegate(object s, EventArgs e)
            {
                StartCheck();
            };
            _notifyIcon.Visible = true;

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = _cfg.PollSeconds * 1000;
            _pollTimer.Tick += delegate(object s, EventArgs e)
            {
                StartCheck();
            };
            _pollTimer.Start();

            SshRunner.EnsureKeyPermissions(_cfg.ResolvedKeyPath);
            StartCheck();
        }

        public static string SafeTooltip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            if (text.Length <= 63)
            {
                return text;
            }
            return text.Substring(0, 60) + "...";
        }

        public static string FormatInterval(int seconds)
        {
            if (seconds >= 3600 && seconds % 3600 == 0)
            {
                return (seconds / 3600) + "h";
            }
            if (seconds >= 60 && seconds % 60 == 0)
            {
                return (seconds / 60) + "m";
            }
            return seconds + "s";
        }

        public static string FormatIntervalLong(int seconds)
        {
            if (seconds >= 3600 && seconds % 3600 == 0)
            {
                int hours = seconds / 3600;
                return hours == 1 ? "1 hour" : (hours + " hours");
            }
            if (seconds >= 60 && seconds % 60 == 0)
            {
                int minutes = seconds / 60;
                return minutes == 1 ? "1 minute" : (minutes + " minutes");
            }
            return seconds == 1 ? "1 second" : (seconds + " seconds");
        }

        private void UpdatePollRateMenu()
        {
            _pollRateMenu.Text = string.Format("Check interval ({0})", FormatInterval(_cfg.PollSeconds));
            for (int i = 0; i < _pollRateItems.Count; i++)
            {
                ToolStripMenuItem item = _pollRateItems[i];
                int seconds = (int)item.Tag;
                item.Checked = (seconds == _cfg.PollSeconds);
            }
        }

        private void OnSelectPollRate(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null || !(item.Tag is int))
            {
                return;
            }
            int nextSeconds = (int)item.Tag;
            if (nextSeconds == _cfg.PollSeconds)
            {
                return;
            }
            try
            {
                AppConfig.SaveIntSetting(_cfg.ConfigPath, "poll_seconds", nextSeconds);
                _cfg.PollSeconds = nextSeconds;
                _pollTimer.Stop();
                _pollTimer.Interval = nextSeconds * 1000;
                _pollTimer.Start();
                UpdatePollRateMenu();
            }
            catch (Exception ex)
            {
                UpdatePollRateMenu();
                ShowSettingsError("Config error", ex.Message);
            }
        }

        private void OnToggleAuto(object sender, EventArgs e)
        {
            bool next = _autoItem.Checked;
            try
            {
                AppConfig.SaveBoolSetting(_cfg.ConfigPath, "auto_renegotiate", next);
                _cfg.AutoRenegotiate = next;
                if (!next)
                {
                    _policy.PausedForDrop = false;
                }
            }
            catch (Exception ex)
            {
                _autoItem.Checked = _cfg.AutoRenegotiate;
                ShowSettingsError("Config error", ex.Message);
            }
        }

        private void UpdateNotificationsMenu()
        {
            int enabledCount = 0;
            for (int i = 0; i < _notificationItems.Count; i++)
            {
                ToolStripMenuItem item = _notificationItems[i];
                NotificationOption opt = (NotificationOption)item.Tag;
                bool isEnabled = opt.Getter(_cfg);
                item.Checked = isEnabled;
                if (isEnabled)
                {
                    enabledCount++;
                }
            }
            _notificationsMenu.Text = string.Format("Notifications ({0}/{1})", enabledCount, _notificationItems.Count);
        }

        private void OnToggleNotification(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null || !(item.Tag is NotificationOption))
            {
                return;
            }
            NotificationOption opt = (NotificationOption)item.Tag;
            bool next = item.Checked;
            try
            {
                AppConfig.SaveBoolSetting(_cfg.ConfigPath, opt.ConfigKey, next);
                opt.Setter(_cfg, next);
                UpdateNotificationsMenu();
            }
            catch (Exception ex)
            {
                UpdateNotificationsMenu();
                ShowSettingsError("Config error", ex.Message);
            }
        }

        private void OnToggleAutorun(object sender, EventArgs e)
        {
            bool next = _autorunItem.Checked;
            try
            {
                AutorunHelper.SetEnabled(next);
            }
            catch (Exception ex)
            {
                _autorunItem.Checked = AutorunHelper.IsEnabled();
                ShowSettingsError("Autorun error", ex.Message);
            }
        }

        private void ShowSettingsError(string title, string message)
        {
            if (_cfg.NotifyOnSettingsError)
            {
                ShowBalloon(title, message, ToolTipIcon.Error);
            }
        }

        private void SetBusy(bool busy, string workingLabel)
        {
            _busy = busy;
            _renegotiateItem.Enabled = !busy;
            _checkNowItem.Enabled = !busy;
            if (busy && !string.IsNullOrEmpty(workingLabel))
            {
                _statusItem.Text = workingLabel;
                _notifyIcon.Text = SafeTooltip(workingLabel);
                SetBadgeIcon("...", Color.FromArgb(30, 120, 210));
            }
        }

        private void StartCheck()
        {
            if (_disposed || _busy)
            {
                return;
            }
            SetBusy(true, null);

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                LinkStatus status = SshRunner.CheckLink(_cfg);
                RunOnUi(delegate
                {
                    if (_disposed)
                    {
                        return;
                    }
                    SetBusy(false, null);
                    ApplyStatus(status, DateTime.Now, null);

                    bool isNewDownswitch = _policy.ObserveDownswitch(status, _cfg.TargetSpeedMbps);
                    bool willAuto = _policy.ShouldTrigger(
                        _cfg.AutoRenegotiate,
                        status,
                        _cfg.TargetSpeedMbps,
                        _cfg.CooldownSeconds,
                        _cfg.MaxAutoRetries,
                        DateTime.UtcNow);

                    if (isNewDownswitch && _cfg.NotifyOnDownswitch)
                    {
                        string msg = willAuto
                            ? string.Format("Link dropped to {0} - renegotiating...", status.FormatShortStatus())
                            : string.Format("Link dropped to {0}.", status.FormatShortStatus());
                        ShowBalloon("WAN downswitch detected", msg, ToolTipIcon.Warning);
                    }

                    if (willAuto)
                    {
                        StartRenegotiate(true);
                    }
                });
            });
        }

        private void StartRenegotiate(bool isAuto)
        {
            if (_disposed || _busy)
            {
                return;
            }
            if (isAuto)
            {
                _policy.RecordAttempt(DateTime.UtcNow, _cfg.MaxAutoRetries);
            }
            else
            {
                _policy.ResetForManual(DateTime.UtcNow);
            }

            SetBusy(true, isAuto ? "WAN: Auto-renegotiating..." : "WAN: Renegotiating...");

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                SshResult sshRes = SshRunner.RunSsh(_cfg, SshRunner.BuildRenegotiateCommand(_cfg.WanPort), 20000);
                LinkStatus finalStatus = null;
                string extraError = null;

                if (sshRes.TimedOut || sshRes.ExitCode != 0)
                {
                    extraError = !string.IsNullOrEmpty(sshRes.StdErr) ? sshRes.StdErr : ("SSH exit code " + sshRes.ExitCode);
                }

                int[] waitSteps = new int[] { 3000, 2000, 2000, 2000 };
                for (int i = 0; i < waitSteps.Length; i++)
                {
                    Thread.Sleep(waitSteps[i]);
                    if (_disposed)
                    {
                        return;
                    }
                    finalStatus = SshRunner.CheckLink(_cfg);
                    if (finalStatus.State == LinkState.Up)
                    {
                        break;
                    }
                }

                if (finalStatus == null)
                {
                    finalStatus = new LinkStatus(LinkState.Error, 0, "", "", extraError ?? "No status after renegotiate");
                }
                else if (finalStatus.State == LinkState.Error && !string.IsNullOrEmpty(extraError))
                {
                    finalStatus = new LinkStatus(LinkState.Error, 0, "", finalStatus.RawOutput, extraError);
                }

                RunOnUi(delegate
                {
                    if (_disposed)
                    {
                        return;
                    }
                    SetBusy(false, null);
                    ApplyStatus(finalStatus, DateTime.Now, isAuto ? "auto" : "manual");
                });
            });
        }

        private void ApplyStatus(LinkStatus status, DateTime localNow, string renegotiateSource)
        {
            string stamp = localNow.ToString("HH:mm:ss");
            string statusLine = string.Format("WAN: {0} @ {1}", status.FormatShortStatus(), stamp);
            if (_policy.PausedForDrop && status.State == LinkState.Up && status.SpeedMbps < _cfg.TargetSpeedMbps)
            {
                statusLine += " (auto paused)";
            }

            _statusItem.Text = statusLine;
            _notifyIcon.Text = SafeTooltip(statusLine);

            if (status.State == LinkState.Up)
            {
                if (status.SpeedMbps >= _cfg.TargetSpeedMbps)
                {
                    _policy.ResetRecovery();
                    SetBadgeIcon(FormatSpeedBadge(status.SpeedMbps), Color.FromArgb(30, 150, 70));
                }
                else
                {
                    Color warnColor = status.SpeedMbps <= 10 ? Color.FromArgb(195, 45, 45) : Color.FromArgb(215, 115, 20);
                    SetBadgeIcon(FormatSpeedBadge(status.SpeedMbps), warnColor);
                }
            }
            else
            {
                SetBadgeIcon("?", Color.FromArgb(110, 115, 125));
            }

            if (renegotiateSource != null)
            {
                if (status.State == LinkState.Up && status.SpeedMbps >= _cfg.TargetSpeedMbps)
                {
                    if (_cfg.NotifyOnRenegotiateSuccess)
                    {
                        ShowBalloon("WAN renegotiated", string.Format("Link is up at {0}.", status.FormatShortStatus()), ToolTipIcon.Info);
                    }
                }
                else if (status.State == LinkState.Up)
                {
                    if (_cfg.NotifyOnRenegotiateBelowTarget)
                    {
                        string extra = _policy.PausedForDrop
                            ? " Auto-retry paused after max attempts; click Renegotiate WAN now to retry."
                            : "";
                        ShowBalloon(
                            "WAN still below target",
                            string.Format("Link came back at {0}.{1}", status.FormatShortStatus(), extra),
                            ToolTipIcon.Warning);
                    }
                }
                else
                {
                    if (_cfg.NotifyOnRenegotiateError)
                    {
                        ShowBalloon(
                            "WAN renegotiate warning",
                            string.IsNullOrEmpty(status.ErrorMessage) ? status.FormatShortStatus() : status.ErrorMessage,
                            ToolTipIcon.Error);
                    }
                }
            }
        }

        private static string FormatSpeedBadge(int speedMbps)
        {
            if (speedMbps >= 1000)
            {
                int gbps = speedMbps / 1000;
                return gbps.ToString() + "G";
            }
            return speedMbps.ToString();
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            if (_disposed)
            {
                return;
            }
            _notifyIcon.ShowBalloonTip(4000, title, text, icon);
        }

        private void SetBadgeIcon(string text, Color bgColor)
        {
            Icon nextIcon = CreateBadgeIcon(text, bgColor);
            Icon oldIcon = _currentIcon;
            _currentIcon = nextIcon;
            _notifyIcon.Icon = nextIcon;
            if (oldIcon != null)
            {
                oldIcon.Dispose();
            }
        }

        private static Icon CreateBadgeIcon(string text, Color bgColor)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    using (SolidBrush bg = new SolidBrush(bgColor))
                    using (Pen border = new Pen(Color.FromArgb(220, 255, 255, 255), 1.5f))
                    {
                        g.FillRectangle(bg, 1, 1, 30, 30);
                        g.DrawRectangle(border, 1, 1, 29, 29);
                    }

                    float fontSize = text.Length >= 3 ? 10.5f : 13.5f;
                    using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, 32, 31), sf);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(hIcon).Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private void RunOnUi(Action action)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                _invoker.BeginInvoke(action);
            }
            catch
            {
            }
        }

        private void ExitApplication()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            if (_currentIcon != null)
            {
                _currentIcon.Dispose();
                _currentIcon = null;
            }
            _invoker.Dispose();
            if (_mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                }
                _mutex.Dispose();
            }
            ExitThread();
        }
    }

    internal static class SelfTests
    {
        private static void AssertTrue(bool cond, string message)
        {
            if (!cond)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        private static void AssertEqual(object expected, object actual, string message)
        {
            if (!object.Equals(expected, actual))
            {
                throw new InvalidOperationException(string.Format("{0}: expected [{1}], got [{2}]", message, expected, actual));
            }
        }

        public static int RunAll()
        {
            TestParseSwconfigLink();
            TestConfigLoadAndValidate();
            TestConfigDefaultGeneration();
            TestAutoRetryPolicy();
            TestSafeTooltip();
            TestBuildSshArguments();
            Console.WriteLine("ALL SELF-TESTS PASSED");
            return 0;
        }

        private static void TestParseSwconfigLink()
        {
            LinkStatus gbe = SwconfigParser.ParseSwconfigLink("port:0 link:up speed:1000baseT full-duplex ");
            AssertEqual(LinkState.Up, gbe.State, "1000baseT state");
            AssertEqual(1000, gbe.SpeedMbps, "1000baseT speed");
            AssertEqual("full-duplex", gbe.Duplex, "1000baseT duplex");

            LinkStatus fe = SwconfigParser.ParseSwconfigLink("port:1 link:up speed:100baseT half-duplex");
            AssertEqual(LinkState.Up, fe.State, "100baseT state");
            AssertEqual(100, fe.SpeedMbps, "100baseT speed");
            AssertEqual("half-duplex", fe.Duplex, "100baseT duplex");

            LinkStatus down = SwconfigParser.ParseSwconfigLink("port:4 link:down");
            AssertEqual(LinkState.Down, down.State, "down state");
            AssertEqual(0, down.SpeedMbps, "down speed");

            LinkStatus err = SwconfigParser.ParseSwconfigLink("garbled output");
            AssertEqual(LinkState.Error, err.State, "error state");
        }

        private static void TestConfigLoadAndValidate()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "dsr_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string cfgPath = Path.Combine(tempDir, "config.ini");
                File.WriteAllText(cfgPath,
                    "# comment\n" +
                    "host=192.168.1.1\n" +
                    "port=2222\n" +
                    "user=admin\n" +
                    "key_file=ssh\\id_ed25519\n" +
                    "known_hosts=ssh\\known_hosts\n" +
                    "wan_port=0\n" +
                    "target_speed_mbps=1000\n" +
                    "poll_seconds=30\n" +
                    "auto_renegotiate=true\n" +
                    "cooldown_seconds=120\n" +
                    "max_auto_retries=3\n",
                    new UTF8Encoding(false));

                AppConfig cfg = AppConfig.Load(cfgPath);
                AssertEqual("192.168.1.1", cfg.Host, "cfg.Host");
                AssertEqual(2222, cfg.Port, "cfg.Port");
                AssertEqual(true, cfg.AutoRenegotiate, "cfg.AutoRenegotiate");
                AssertEqual(true, cfg.NotifyOnDownswitch, "default NotifyOnDownswitch");
                AssertEqual(true, cfg.NotifyOnRenegotiateSuccess, "default NotifyOnRenegotiateSuccess");
                AssertEqual(true, cfg.NotifyOnRenegotiateBelowTarget, "default NotifyOnRenegotiateBelowTarget");
                AssertEqual(true, cfg.NotifyOnRenegotiateError, "default NotifyOnRenegotiateError");
                AssertEqual(true, cfg.NotifyOnSettingsError, "default NotifyOnSettingsError");

                AppConfig.SaveBoolSetting(cfgPath, "auto_renegotiate", false);
                AppConfig.SaveBoolSetting(cfgPath, "notify_on_downswitch", false);
                AppConfig.SaveBoolSetting(cfgPath, "notify_on_renegotiate_success", false);
                AppConfig.SaveBoolSetting(cfgPath, "notify_on_renegotiate_below_target", false);
                AppConfig.SaveBoolSetting(cfgPath, "notify_on_renegotiate_error", false);
                AppConfig.SaveBoolSetting(cfgPath, "notify_on_settings_error", false);
                AppConfig.SaveIntSetting(cfgPath, "poll_seconds", 300);
                AppConfig reloaded = AppConfig.Load(cfgPath);
                AssertEqual(false, reloaded.AutoRenegotiate, "saved auto_renegotiate false");
                AssertEqual(false, reloaded.NotifyOnDownswitch, "saved notify_on_downswitch false");
                AssertEqual(false, reloaded.NotifyOnRenegotiateSuccess, "saved notify_on_renegotiate_success false");
                AssertEqual(false, reloaded.NotifyOnRenegotiateBelowTarget, "saved notify_on_renegotiate_below_target false");
                AssertEqual(false, reloaded.NotifyOnRenegotiateError, "saved notify_on_renegotiate_error false");
                AssertEqual(false, reloaded.NotifyOnSettingsError, "saved notify_on_settings_error false");
                AssertEqual(300, reloaded.PollSeconds, "saved poll_seconds 300");

                AssertEqual("1m", TrayApp.FormatInterval(60), "format 60s");
                AssertEqual("1h", TrayApp.FormatInterval(3600), "format 3600s");
                AssertEqual("24h", TrayApp.FormatInterval(86400), "format 86400s");
                AssertEqual("30s", TrayApp.FormatInterval(30), "format 30s");

                cfg.WanPort = 7;
                AppConfig.Validate(cfg);
                cfg.WanPort = 31;
                AppConfig.Validate(cfg);

                int[] invalidWanPorts = new int[] { -1, 32 };
                for (int i = 0; i < invalidWanPorts.Length; i++)
                {
                    bool caughtWan = false;
                    try
                    {
                        cfg.WanPort = invalidWanPorts[i];
                        AppConfig.Validate(cfg);
                    }
                    catch (InvalidOperationException)
                    {
                        caughtWan = true;
                    }
                    AssertTrue(caughtWan, "invalid wan_port rejected: " + invalidWanPorts[i]);
                }
                cfg.WanPort = 0;

                bool caughtInvalid = false;
                try
                {
                    cfg.Port = 70000;
                    AppConfig.Validate(cfg);
                }
                catch (InvalidOperationException)
                {
                    caughtInvalid = true;
                }
                AssertTrue(caughtInvalid, "invalid port rejected");
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                }
            }
        }

        private static void TestConfigDefaultGeneration()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "dsr_default_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string cfgPath = Path.Combine(tempDir, "config.ini");
                string defaultPath = Path.Combine(tempDir, AppConfig.DefaultTemplateFileName);

                // 1. Missing both config.ini and default_config.ini throws FileNotFoundException without creating config.ini
                bool caughtMissing = false;
                try
                {
                    AppConfig.Load(cfgPath);
                }
                catch (FileNotFoundException)
                {
                    caughtMissing = true;
                }
                AssertTrue(caughtMissing, "missing both config and default throws");
                AssertEqual(false, File.Exists(cfgPath), "config.ini not created when default missing");

                // 2. Invalid default_config.ini fails validation without creating config.ini
                File.WriteAllText(defaultPath, "host=192.168.1.1\nport=99999\n", new UTF8Encoding(false));
                bool caughtInvalidDefault = false;
                try
                {
                    bool createdInvalid;
                    AppConfig.Load(cfgPath, out createdInvalid);
                }
                catch (InvalidOperationException)
                {
                    caughtInvalidDefault = true;
                }
                AssertTrue(caughtInvalidDefault, "invalid default_config.ini rejected");
                AssertEqual(false, File.Exists(cfgPath), "config.ini not created from invalid default");

                // 3. Valid default_config.ini generates config.ini once and does not overwrite subsequent edits
                File.WriteAllText(defaultPath,
                    "# default template\n" +
                    "host=192.168.1.1\n" +
                    "port=22\n" +
                    "user=admin\n" +
                    "key_file=ssh\\id_ed25519\n" +
                    "known_hosts=ssh\\known_hosts\n" +
                    "wan_port=0\n" +
                    "target_speed_mbps=1000\n" +
                    "poll_seconds=300\n" +
                    "auto_renegotiate=true\n" +
                    "notify_on_downswitch=true\n" +
                    "cooldown_seconds=120\n" +
                    "max_auto_retries=3\n",
                    new UTF8Encoding(false));

                bool created;
                AppConfig firstCfg = AppConfig.Load(cfgPath, out created);
                AssertEqual(true, created, "created from default_config.ini");
                AssertEqual(true, File.Exists(cfgPath), "config.ini created");
                AssertEqual("192.168.1.1", firstCfg.Host, "generated Host");
                AssertEqual(22, firstCfg.Port, "generated Port");
                AssertEqual(cfgPath, firstCfg.ConfigPath, "generated ConfigPath");
                AssertEqual(tempDir, firstCfg.BaseDir, "generated BaseDir");

                bool createdSecond;
                AppConfig secondCfg = AppConfig.Load(cfgPath, out createdSecond);
                AssertEqual(false, createdSecond, "second load does not recreate");
                AssertEqual(22, secondCfg.Port, "second load port");

                AppConfig.SaveIntSetting(cfgPath, "port", 2222);
                bool createdThird;
                AppConfig editedCfg = AppConfig.Load(cfgPath, out createdThird);
                AssertEqual(false, createdThird, "load after edit does not recreate");
                AssertEqual(2222, editedCfg.Port, "edited config.ini preserved");
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                }
            }
        }

        private static void TestAutoRetryPolicy()
        {
            AutoRetryPolicy p = new AutoRetryPolicy();
            LinkStatus s100 = new LinkStatus(LinkState.Up, 100, "full-duplex", "", "");
            LinkStatus s1000 = new LinkStatus(LinkState.Up, 1000, "full-duplex", "", "");
            LinkStatus sDown = new LinkStatus(LinkState.Down, 0, "", "", "");
            LinkStatus sErr = new LinkStatus(LinkState.Error, 0, "", "", "timeout");
            DateTime t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            AssertEqual(false, p.ShouldTrigger(false, s100, 1000, 120, 3, t0), "disabled");
            AssertEqual(false, p.ShouldTrigger(true, sDown, 1000, 120, 3, t0), "down");
            AssertEqual(false, p.ShouldTrigger(true, sErr, 1000, 120, 3, t0), "error");
            AssertEqual(false, p.ShouldTrigger(true, s1000, 1000, 120, 3, t0), "already 1000");

            AssertEqual(true, p.ShouldTrigger(true, s100, 1000, 120, 3, t0), "attempt 1");
            p.RecordAttempt(t0, 3);
            AssertEqual(false, p.ShouldTrigger(true, s100, 1000, 120, 3, t0.AddSeconds(60)), "cooldown active");
            AssertEqual(true, p.ShouldTrigger(true, s100, 1000, 120, 3, t0.AddSeconds(120)), "attempt 2");
            p.RecordAttempt(t0.AddSeconds(120), 3);
            AssertEqual(true, p.ShouldTrigger(true, s100, 1000, 120, 3, t0.AddSeconds(240)), "attempt 3");
            p.RecordAttempt(t0.AddSeconds(240), 3);
            AssertEqual(true, p.PausedForDrop, "paused after max retries");
            AssertEqual(false, p.ShouldTrigger(true, s100, 1000, 120, 3, t0.AddSeconds(600)), "blocked while paused");

            // Recovery resets pause and failure count
            AssertEqual(false, p.ShouldTrigger(true, s1000, 1000, 120, 3, t0.AddSeconds(630)), "recovered");
            AssertEqual(false, p.PausedForDrop, "unpaused after recovery");
            AssertEqual(0, p.ConsecutiveFailures, "failures reset after recovery");
            AssertEqual(true, p.ShouldTrigger(true, s100, 1000, 120, 3, t0.AddSeconds(660)), "triggers on new drop");

            // Manual reset clears pause immediately
            p.RecordAttempt(t0.AddSeconds(660), 1);
            AssertEqual(true, p.PausedForDrop, "paused at max=1");
            p.ResetForManual(t0.AddSeconds(700));
            AssertEqual(false, p.PausedForDrop, "manual reset unpauses");
            AssertEqual(0, p.ConsecutiveFailures, "manual reset clears failures");

            // Downswitch notification fires once per drop, ignores transient Down/Error, resets on recovery
            AutoRetryPolicy np = new AutoRetryPolicy();
            AssertEqual(false, np.ObserveDownswitch(s1000, 1000), "healthy 1G no notify");
            AssertEqual(true, np.ObserveDownswitch(s100, 1000), "first 100M drop notifies");
            AssertEqual(false, np.ObserveDownswitch(s100, 1000), "repeated 100M does not spam");
            AssertEqual(false, np.ObserveDownswitch(sErr, 1000), "transient error suppressed");
            AssertEqual(false, np.ObserveDownswitch(sDown, 1000), "transient down suppressed");
            AssertEqual(false, np.ObserveDownswitch(s100, 1000), "still suppressed after transient error/down");
            AssertEqual(false, np.ObserveDownswitch(s1000, 1000), "recovery resets state");
            AssertEqual(false, np.DownswitchActive, "DownswitchActive reset");
            AssertEqual(true, np.ObserveDownswitch(s100, 1000), "subsequent drop notifies again");
        }

        private static void TestSafeTooltip()
        {
            string shortText = "WAN: 1000 Mbps (full-duplex)";
            AssertEqual(shortText, TrayApp.SafeTooltip(shortText), "short tooltip preserved");

            string longText = new string('x', 80);
            string truncated = TrayApp.SafeTooltip(longText);
            AssertEqual(63, truncated.Length, "tooltip truncated to 63");
            AssertTrue(truncated.EndsWith("..."), "tooltip ellipsis");
        }

        private static void TestBuildSshArguments()
        {
            AppConfig cfg = new AppConfig();
            string args = SshRunner.BuildSshArguments(cfg, SshRunner.BuildCheckCommand(0));
            AssertTrue(args.Contains("-i \"ssh\\id_ed25519\""), "key arg");
            AssertTrue(args.Contains("-p 22"), "port arg");
            AssertTrue(args.Contains("-o StrictHostKeyChecking=accept-new"), "accept-new host key");
            AssertTrue(args.Contains("-o \"UserKnownHostsFile=ssh\\known_hosts\""), "known_hosts arg");
            AssertTrue(args.Contains("admin@192.168.1.1 \"swconfig dev switch0 port 0 get link\""), "cmd arg");

            AssertEqual(
                "admin@192.168.1.1: Permission denied (publickey).",
                SshRunner.FirstErrorLine("Warning: Permanently added '[192.168.1.1]:22' (ED25519) to the list of known hosts.\nadmin@192.168.1.1: Permission denied (publickey)."),
                "skips permanently added warning");
            AssertEqual(
                "Connection timed out",
                SshRunner.FirstErrorLine("Connection timed out"),
                "single-line error");

            string tempDir = Path.Combine(Path.GetTempPath(), "dsr_kh_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                string nestedDir = Path.Combine(tempDir, "nested");
                string khPath = Path.Combine(nestedDir, "known_hosts");
                AssertEqual(null, SshRunner.EnsureKnownHostsParentDirectory(khPath), "creates nested dir");
                AssertTrue(Directory.Exists(nestedDir), "nested dir exists");
                AssertTrue(SshRunner.EnsureKnownHostsParentDirectory(nestedDir) != null, "rejects directory path");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch
                {
                }
            }
        }
    }

    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        private static void EnsureConsoleAttached()
        {
            try
            {
                if (!Console.IsOutputRedirected)
                {
                    AttachConsole(-1);
                }
            }
            catch
            {
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                string mode = args[0].Trim().ToLowerInvariant();
                if (mode == "--self-test")
                {
                    EnsureConsoleAttached();
                    try
                    {
                        return SelfTests.RunAll();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex.ToString());
                        return 1;
                    }
                }
                if (mode == "--check-once")
                {
                    EnsureConsoleAttached();
                    try
                    {
                        bool createdFromDefault;
                        AppConfig cfg = AppConfig.Load(AppConfig.DefaultPath, out createdFromDefault);
                        if (createdFromDefault)
                        {
                            Console.WriteLine("Created config.ini from default_config.ini: " + cfg.ConfigPath);
                            Console.WriteLine("Edit config.ini with your router settings and run again.");
                            return 1;
                        }
                        SshRunner.EnsureKeyPermissions(cfg.ResolvedKeyPath);
                        LinkStatus status = SshRunner.CheckLink(cfg);
                        Console.WriteLine(status.FormatSummary(cfg.WanPort));
                        return (status.State == LinkState.Up || status.State == LinkState.Down) ? 0 : 1;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Error: " + ex.Message);
                        return 1;
                    }
                }
            }

            AppConfig appCfg;
            bool guiCreatedFromDefault;
            try
            {
                appCfg = AppConfig.Load(AppConfig.DefaultPath, out guiCreatedFromDefault);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DownswitchRenegotiator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            if (guiCreatedFromDefault)
            {
                MessageBox.Show(
                    "Created config.ini from default_config.ini:\n" + appCfg.ConfigPath + "\n\nEdit it with your router settings, then start DownswitchRenegotiator again.",
                    "DownswitchRenegotiator",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 0;
            }

            bool createdNew;
            Mutex mutex = new Mutex(true, "Local\\DownswitchRenegotiator", out createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(appCfg, mutex));
            return 0;
        }
    }
}
