using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace CodexResetMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MonitorContext());
        }
    }

    internal sealed class MonitorContext : ApplicationContext
    {
        private readonly ConfigStore store;
        private MonitorConfig config;
        private NotifyIcon notifyIcon;
        private StatusBarWindow statusBar;
        private PopupWindow popupWindow;
        private System.Windows.Forms.Timer refreshTimer;
        private System.Windows.Forms.Timer hideTimer;
        private volatile bool refreshing;
        private bool popupPinned;
        private MonitorSnapshot snapshot = MonitorSnapshot.Loading();

        public MonitorContext()
        {
            this.store = new ConfigStore();
            this.config = this.store.Load();
            this.notifyIcon = this.CreateNotifyIcon();
            this.statusBar = new StatusBarWindow();
            this.popupWindow = new PopupWindow();
            this.statusBar.SetSnapshot(this.snapshot);
            this.popupWindow.SetSnapshot(this.snapshot);
            this.statusBar.MouseEnteredBar += delegate { this.ShowPopup(false); };
            this.statusBar.MouseLeftBar += delegate { this.QueueHidePopup(); };
            this.statusBar.TogglePinnedRequested += delegate { this.TogglePinnedPopup(); };
            this.statusBar.HidePopupRequested += delegate { this.HidePopup(true); };
            this.statusBar.RefreshRequested += delegate { this.RefreshNow(); };
            this.statusBar.SettingsRequested += delegate { this.ShowSettings(false); };
            this.statusBar.ExitRequested += delegate { this.ExitApp(); };
            this.popupWindow.MouseEnteredPopup += delegate { this.ShowPopup(false); };
            this.popupWindow.MouseLeftPopup += delegate { this.QueueHidePopup(); };
            this.popupWindow.HidePopupRequested += delegate { this.HidePopup(true); };
            this.popupWindow.RefreshRequested += delegate { this.RefreshNow(); };
            this.popupWindow.SettingsRequested += delegate { this.ShowSettings(false); };
            this.popupWindow.ExitRequested += delegate { this.ExitApp(); };
            this.statusBar.Show();

            if (!this.config.HasUsableSecret())
            {
                this.ShowSettings(true);
            }

            this.refreshTimer = new System.Windows.Forms.Timer();
            this.refreshTimer.Interval = Math.Max(60, this.config.RefreshSeconds) * 1000;
            this.refreshTimer.Tick += delegate { this.RefreshNow(); };
            this.refreshTimer.Start();

            this.hideTimer = new System.Windows.Forms.Timer();
            this.hideTimer.Interval = 180;
            this.hideTimer.Tick += delegate {
                this.hideTimer.Stop();
                if (!this.popupPinned && !this.statusBar.ContainsCursor() && !this.popupWindow.ContainsCursor())
                {
                    this.popupWindow.Hide();
                }
            };
            this.RefreshNow();
        }

        private NotifyIcon CreateNotifyIcon()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show details", null, delegate { this.ShowDetails(); });
            menu.Items.Add("Refresh now", null, delegate { this.RefreshNow(); });
            menu.Items.Add("Settings", null, delegate { this.ShowSettings(false); });
            menu.Items.Add("Hide popup", null, delegate { this.HidePopup(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { this.ExitApp(); });

            var icon = new NotifyIcon();
            icon.Icon = IconFactory.CreateTrayIcon();
            icon.Text = "Codex Reset Monitor";
            icon.Visible = true;
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += delegate { this.ShowDetails(); };
            return icon;
        }

        private void RefreshNow()
        {
            if (this.refreshing) return;
            this.refreshing = true;
            ThreadPool.QueueUserWorkItem(delegate {
                MonitorSnapshot next;
                try
                {
                    next = CodexClient.Fetch(this.config);
                }
                catch (Exception ex)
                {
                    next = MonitorSnapshot.Error(ex.Message, ex.ToString());
                }
                this.PostToUi(delegate {
                    this.refreshing = false;
                    this.snapshot = next;
                    this.statusBar.SetSnapshot(next);
                    this.popupWindow.SetSnapshot(next);
                    this.notifyIcon.Text = next.TrayText();
                });
            });
        }

        private void ShowDetails()
        {
            using (var form = new DetailsForm(this.snapshot))
            {
                form.ShowDialog();
            }
        }

        private void ShowSettings(bool firstRun)
        {
            using (var form = new SettingsForm(this.config))
            {
                if (firstRun)
                {
                    form.StartPosition = FormStartPosition.CenterScreen;
                }
                if (form.ShowDialog() == DialogResult.OK)
                {
                    this.config = form.Config;
                    this.store.Save(this.config);
                    this.refreshTimer.Interval = Math.Max(60, this.config.RefreshSeconds) * 1000;
                    this.RefreshNow();
                }
            }
        }

        private void PostToUi(Action action)
        {
            if (this.statusBar.IsDisposed) return;
            this.statusBar.BeginInvoke(action);
        }

        private void ShowPopup(bool pin)
        {
            if (pin) this.popupPinned = true;
            this.popupWindow.PlaceNear(this.statusBar);
            this.popupWindow.Show();
            this.popupWindow.BringToFront();
        }

        private void TogglePinnedPopup()
        {
            if (this.popupPinned)
            {
                this.HidePopup(true);
                return;
            }
            this.popupPinned = true;
            this.ShowPopup(true);
        }

        private void HidePopup(bool clearPin)
        {
            if (clearPin) this.popupPinned = false;
            this.popupWindow.Hide();
        }

        private void QueueHidePopup()
        {
            if (this.popupPinned) return;
            this.hideTimer.Stop();
            this.hideTimer.Start();
        }

        private void ExitApp()
        {
            this.notifyIcon.Visible = false;
            this.notifyIcon.Dispose();
            this.popupWindow.Close();
            this.statusBar.Close();
            this.refreshTimer.Stop();
            this.hideTimer.Stop();
            Application.Exit();
        }
    }

    internal sealed class MonitorConfig
    {
        public string AccessTokenDpapi = "";
        public string AccountId = "";
        public int RefreshSeconds = 300;
        public string ResetCardsEndpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
        public string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
        public string Originator = "Codex Desktop";
        public string OpenAIBeta = "codex-1";

        public bool HasUsableSecret()
        {
            return !string.IsNullOrWhiteSpace(this.AccessTokenDpapi)
                && !string.IsNullOrWhiteSpace(this.AccountId);
        }
    }

    internal sealed class ConfigStore
    {
        private readonly string configDir;
        private readonly string configPath;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public ConfigStore()
        {
            this.configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexResetMonitor");
            this.configPath = Path.Combine(this.configDir, "config.json");
        }

        public MonitorConfig Load()
        {
            var cfg = new MonitorConfig();
            if (!File.Exists(this.configPath)) return cfg;

            try
            {
                var json = File.ReadAllText(this.configPath, Encoding.UTF8);
                var root = this.serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return cfg;
                cfg.AccessTokenDpapi = GetString(root, "access_token_dpapi", cfg.AccessTokenDpapi);
                cfg.AccountId = GetString(root, "account_id", cfg.AccountId);
                cfg.RefreshSeconds = GetInt(root, "refresh_seconds", cfg.RefreshSeconds);
                cfg.ResetCardsEndpoint = GetString(root, "reset_cards_endpoint", cfg.ResetCardsEndpoint);
                cfg.UsageEndpoint = GetString(root, "usage_endpoint", cfg.UsageEndpoint);
                if (string.IsNullOrWhiteSpace(cfg.UsageEndpoint))
                {
                    cfg.UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
                }
                cfg.Originator = GetString(root, "originator", cfg.Originator);
                cfg.OpenAIBeta = GetString(root, "openai_beta", cfg.OpenAIBeta);
                return cfg;
            }
            catch
            {
                return cfg;
            }
        }

        public void Save(MonitorConfig cfg)
        {
            Directory.CreateDirectory(this.configDir);
            var root = new Dictionary<string, object>();
            root["access_token_dpapi"] = cfg.AccessTokenDpapi ?? "";
            root["account_id"] = cfg.AccountId ?? "";
            root["refresh_seconds"] = cfg.RefreshSeconds;
            root["reset_cards_endpoint"] = cfg.ResetCardsEndpoint ?? "";
            root["usage_endpoint"] = cfg.UsageEndpoint ?? "";
            root["originator"] = cfg.Originator ?? "";
            root["openai_beta"] = cfg.OpenAIBeta ?? "";
            File.WriteAllText(this.configPath, this.serializer.Serialize(root), Encoding.UTF8);
        }

        private static string GetString(Dictionary<string, object> root, string key, string fallback)
        {
            object value;
            if (!root.TryGetValue(key, out value) || value == null) return fallback;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
        }

        private static int GetInt(Dictionary<string, object> root, string key, int fallback)
        {
            object value;
            if (!root.TryGetValue(key, out value) || value == null) return fallback;
            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }
    }

    internal static class SecretBox
    {
        public static string Protect(string plainText)
        {
            var bytes = Encoding.Unicode.GetBytes(plainText ?? "");
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return ToHex(protectedBytes);
        }

        public static string Unprotect(string encryptedHex)
        {
            var bytes = FromHex(encryptedHex);
            var plainBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.Unicode.GetString(plainBytes);
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static byte[] FromHex(string hex)
        {
            if (hex == null) throw new ArgumentNullException("hex");
            hex = hex.Trim();
            if ((hex.Length % 2) != 0) throw new FormatException("Invalid DPAPI hex payload.");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return bytes;
        }
    }

    internal static class CodexClient
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };

        public static MonitorSnapshot Fetch(MonitorConfig config)
        {
            var token = SecretBox.Unprotect(config.AccessTokenDpapi);
            var usageJson = Get(config.UsageEndpoint, config, token);
            var resetJson = Get(config.ResetCardsEndpoint, config, token);

            var usageRoot = Serializer.DeserializeObject(usageJson) as Dictionary<string, object>;
            var resetRoot = Serializer.DeserializeObject(resetJson) as Dictionary<string, object>;

            var usage = UsageInfo.FromJson(usageRoot);
            var resetCards = ResetCardInfo.FromJson(resetRoot);
            return new MonitorSnapshot(usage, resetCards, DateTime.Now, null, usageJson, resetJson);
        }

        private static string Get(string uri, MonitorConfig config, string token)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.Accept = "application/json";
            request.UserAgent = "CodexResetMonitor";
            request.Headers["Authorization"] = "Bearer " + token;
            if (!string.IsNullOrWhiteSpace(config.OpenAIBeta)) request.Headers["OpenAI-Beta"] = config.OpenAIBeta;
            if (!string.IsNullOrWhiteSpace(config.Originator)) request.Headers["originator"] = config.Originator;
            if (!string.IsNullOrWhiteSpace(config.AccountId)) request.Headers["ChatGPT-Account-ID"] = config.AccountId;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }

    internal sealed class UsageInfo
    {
        public string Plan = "";
        public UsageWindow FiveHour = UsageWindow.Unavailable("5h");
        public UsageWindow Weekly = UsageWindow.Unavailable("1w");
        public string CreditsBalance = "";
        public bool HasCredits;

        public static UsageInfo FromJson(Dictionary<string, object> root)
        {
            var info = new UsageInfo();
            if (root == null) return info;
            info.Plan = Text(root, "plan_type") ?? Text(root, "plan") ?? "";

            var source = Dict(root, "rate_limit_status") ?? root;
            var rateLimit = Dict(source, "rate_limit") ?? Dict(root, "rate_limit");
            if (rateLimit != null)
            {
                var primary = Dict(rateLimit, "primary_window") ?? Dict(rateLimit, "primary");
                var secondary = Dict(rateLimit, "secondary_window") ?? Dict(rateLimit, "secondary");
                info.FiveHour = UsageWindow.FromJson("5h", primary, 18000);
                info.Weekly = UsageWindow.FromJson("1w", secondary, 604800);
            }

            var credits = Dict(root, "credits");
            if (credits != null)
            {
                info.CreditsBalance = Text(credits, "balance") ?? "";
                info.HasCredits = Bool(credits, "has_credits") || Bool(credits, "hasCredits");
            }
            return info;
        }

        internal static Dictionary<string, object> Dict(Dictionary<string, object> root, string key)
        {
            if (root == null) return null;
            object value;
            return root.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        internal static string Text(Dictionary<string, object> root, string key)
        {
            if (root == null) return null;
            object value;
            if (!root.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        internal static bool Bool(Dictionary<string, object> root, string key)
        {
            if (root == null) return false;
            object value;
            if (!root.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) && parsed;
        }

        internal static double? Number(Dictionary<string, object> root, params string[] keys)
        {
            if (root == null) return null;
            foreach (var key in keys)
            {
                object value;
                if (!root.TryGetValue(key, out value) || value == null) continue;
                if (value is int) return (int)value;
                if (value is long) return (long)value;
                if (value is double) return (double)value;
                if (value is decimal) return (double)(decimal)value;
                double parsed;
                if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) return parsed;
            }
            return null;
        }
    }

    internal sealed class UsageWindow
    {
        public string Label;
        public bool Available;
        public double UsedPercent;
        public double RemainingPercent;
        public DateTime? ResetAtLocal;
        public int WindowSeconds;

        public static UsageWindow Unavailable(string label)
        {
            return new UsageWindow { Label = label, Available = false, WindowSeconds = 0 };
        }

        public static UsageWindow FromJson(string label, Dictionary<string, object> root, int defaultSeconds)
        {
            if (root == null) return Unavailable(label);

            var remaining = UsageInfo.Number(root, "remaining_percent", "remaining_percentage");
            var used = UsageInfo.Number(root, "used_percent", "used_percentage", "utilization");
            if (remaining.HasValue)
            {
                used = 100.0 - remaining.Value;
            }
            if (used.HasValue && used.Value <= 1.0) used = used.Value * 100.0;
            var usedPct = Clamp(used ?? 0.0, 0.0, 100.0);

            var resetAt = UsageInfo.Number(root, "reset_at", "resets_at", "reset_time", "expires_at");
            var resetAfter = UsageInfo.Number(root, "reset_after_seconds");
            DateTime? resetLocal = null;
            if (resetAt.HasValue)
            {
                resetLocal = UnixToLocal(resetAt.Value);
            }
            else if (resetAfter.HasValue)
            {
                resetLocal = DateTime.Now.AddSeconds(Math.Max(0, resetAfter.Value));
            }

            var seconds = UsageInfo.Number(root, "limit_window_seconds");
            var minutes = UsageInfo.Number(root, "window_minutes");
            var windowSeconds = seconds.HasValue ? (int)Math.Round(seconds.Value) : (minutes.HasValue ? (int)Math.Round(minutes.Value * 60.0) : defaultSeconds);

            return new UsageWindow
            {
                Label = label,
                Available = true,
                UsedPercent = usedPct,
                RemainingPercent = 100.0 - usedPct,
                ResetAtLocal = resetLocal,
                WindowSeconds = windowSeconds
            };
        }

        public string ShortLine()
        {
            if (!this.Available) return this.Label + " \u5269\u4f59 --";
            return string.Format(CultureInfo.InvariantCulture, "{0} \u5269\u4f59 {1:0.#}% · {2}", this.Label, this.RemainingPercent, ResetDescription());
        }

        public string DetailLine()
        {
            if (!this.Available) return this.Label + ": unavailable";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}: \u5269\u4f59 {1:0.0}% (\u5df2\u7528 {2:0.0}%), {3}, \u7a97\u53e3 {4:0.#}h",
                this.Label,
                this.RemainingPercent,
                this.UsedPercent,
                this.ResetDescription(),
                this.WindowSeconds / 3600.0);
        }

        public string ResetDescription()
        {
            return FormatResetDescription(this.ResetAtLocal);
        }

        public static string FormatResetDescription(DateTime? resetAtLocal)
        {
            if (!resetAtLocal.HasValue) return "\u91cd\u7f6e --";
            var remaining = resetAtLocal.Value - DateTime.Now;
            if (remaining.TotalSeconds < 0) remaining = TimeSpan.Zero;
            string duration;
            if (remaining.TotalDays >= 1)
            {
                duration = string.Format(CultureInfo.InvariantCulture, "{0} d {1:00} h", (int)Math.Floor(remaining.TotalDays), remaining.Hours);
            }
            else
            {
                duration = string.Format(CultureInfo.InvariantCulture, "{0} h {1:00} m", (int)Math.Floor(remaining.TotalHours), remaining.Minutes);
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "\u5373\u5c06\u4e8e {0} \u540e\u91cd\u7f6e ({1})",
                duration,
                resetAtLocal.Value.ToString("yyyy-MM-dd HH:mm"));
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static DateTime UnixToLocal(double value)
        {
            var seconds = value > 10000000000.0 ? value / 1000.0 : value;
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(seconds).ToLocalTime();
        }
    }

    internal sealed class ResetCardInfo
    {
        public int Count;
        public List<DateTime> Expirations = new List<DateTime>();

        public static ResetCardInfo FromJson(Dictionary<string, object> root)
        {
            var info = new ResetCardInfo();
            var dates = new List<DateTime>();
            FindDates(root, dates);
            dates.Sort();
            foreach (var dt in dates)
            {
                bool exists = false;
                foreach (var existing in info.Expirations)
                {
                    if (Math.Abs((existing - dt).TotalSeconds) < 1) { exists = true; break; }
                }
                if (!exists) info.Expirations.Add(dt);
            }
            info.Count = info.Expirations.Count;
            if (info.Count == 0)
            {
                var available = FindNumberByName(root, "available");
                var count = FindNumberByName(root, "count");
                info.Count = (int)(available ?? count ?? 0);
            }
            return info;
        }

        public string ShortLine()
        {
            if (this.Expirations.Count == 0) return "\u91cd\u7f6e\u5361 --";
            return "\u91cd\u7f6e\u5361 " + this.Expirations.Count + " · \u6700\u8fd1 " + UsageWindow.FormatResetDescription(this.Expirations[0]);
        }

        public string Detail()
        {
            if (this.Expirations.Count == 0) return "Reset cards: no expiry fields found.";
            var sb = new StringBuilder();
            sb.AppendLine("\u91cd\u7f6e\u5361\u5230\u671f\u65f6\u95f4:");
            for (int i = 0; i < this.Expirations.Count; i++)
            {
                sb.AppendLine("- \u91cd\u7f6e\u5361 " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + UsageWindow.FormatResetDescription(this.Expirations[i]));
            }
            return sb.ToString().TrimEnd();
        }

        public IEnumerable<string> BarLines()
        {
            if (this.Expirations.Count == 0)
            {
                yield return "\u91cd\u7f6e\u5361 --";
                yield break;
            }
            for (int i = 0; i < this.Expirations.Count; i++)
            {
                yield return "\u91cd\u7f6e\u5361 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " · " + UsageWindow.FormatResetDescription(this.Expirations[i]);
            }
        }

        private static void FindDates(object value, List<DateTime> dates)
        {
            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (var kv in dict)
                {
                    var key = kv.Key.ToLowerInvariant();
                    if (key.Contains("expir") || key.Contains("valid") || key.Contains("until"))
                    {
                        DateTime dt;
                        if (TryParseDate(kv.Value, out dt)) dates.Add(dt);
                    }
                    FindDates(kv.Value, dates);
                }
                return;
            }
            var arr = value as object[];
            if (arr != null)
            {
                foreach (var item in arr) FindDates(item, dates);
            }
        }

        private static bool TryParseDate(object value, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (value == null) return false;
            double number;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
            {
                if (number > 10000000000.0) number = number / 1000.0;
                if (number > 1000000000.0)
                {
                    var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    dt = epoch.AddSeconds(number).ToLocalTime();
                    return true;
                }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
            {
                dt = dto.ToLocalTime().DateTime;
                return true;
            }
            return false;
        }

        private static double? FindNumberByName(object value, string namePart)
        {
            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (var kv in dict)
                {
                    if (kv.Key.ToLowerInvariant().Contains(namePart))
                    {
                        double parsed;
                        if (double.TryParse(Convert.ToString(kv.Value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                        {
                            return parsed;
                        }
                    }
                    var child = FindNumberByName(kv.Value, namePart);
                    if (child.HasValue) return child;
                }
            }
            var arr = value as object[];
            if (arr != null)
            {
                foreach (var item in arr)
                {
                    var child = FindNumberByName(item, namePart);
                    if (child.HasValue) return child;
                }
            }
            return null;
        }
    }

    internal sealed class MonitorSnapshot
    {
        public UsageInfo Usage;
        public ResetCardInfo ResetCards;
        public DateTime UpdatedAt;
        public string ErrorMessage;
        public string Details;

        public MonitorSnapshot(UsageInfo usage, ResetCardInfo cards, DateTime updatedAt, string error, string usageJson, string resetJson)
        {
            this.Usage = usage ?? new UsageInfo();
            this.ResetCards = cards ?? new ResetCardInfo();
            this.UpdatedAt = updatedAt;
            this.ErrorMessage = error;
            this.Details = this.BuildDetails(usageJson, resetJson);
        }

        public static MonitorSnapshot Loading()
        {
            return new MonitorSnapshot(new UsageInfo(), new ResetCardInfo(), DateTime.Now, null, "", "") { Details = "Loading..." };
        }

        public static MonitorSnapshot Error(string message, string detail)
        {
            return new MonitorSnapshot(new UsageInfo(), new ResetCardInfo(), DateTime.Now, message, "", "") { Details = detail };
        }

        public string TrayText()
        {
            var text = this.ErrorMessage != null ? "Codex Monitor: " + this.ErrorMessage : this.Usage.FiveHour.ShortLine() + " | " + this.Usage.Weekly.ShortLine();
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }

        public List<string> BarMessages()
        {
            var messages = new List<string>();
            if (this.ErrorMessage != null)
            {
                messages.Add("\u8fde\u63a5\u5931\u8d25 \u00b7 " + this.ErrorMessage);
                return messages;
                messages.Add("\u8fde\u63a5\u5931\u8d25 · " + this.ErrorMessage);
                return messages;
            }
            messages.Add(this.Usage.FiveHour.ShortLine());
            messages.Add(this.Usage.Weekly.ShortLine());
            messages.AddRange(this.ResetCards.BarLines());
            return messages;
        }

        private string BuildDetails(string usageJson, string resetJson)
        {
            if (this.ErrorMessage != null) return this.ErrorMessage;
            var sb = new StringBuilder();
            sb.AppendLine("Codex\u5269\u4f59\u7528\u91cf&\u91cd\u7f6e");
            sb.AppendLine("Last refresh: " + this.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            if (!string.IsNullOrWhiteSpace(this.Usage.Plan)) sb.AppendLine("Plan: " + this.Usage.Plan);
            sb.AppendLine(this.Usage.FiveHour.DetailLine());
            sb.AppendLine(this.Usage.Weekly.DetailLine());
            if (this.Usage.HasCredits) sb.AppendLine("Credits balance: " + (string.IsNullOrWhiteSpace(this.Usage.CreditsBalance) ? "--" : this.Usage.CreditsBalance));
            sb.AppendLine();
            sb.AppendLine(this.ResetCards.Detail());
            return sb.ToString().TrimEnd();
        }
    }

    internal sealed class FloatingWindow : Form
    {
        public event EventHandler ShowDetailsRequested;
        private MonitorSnapshot snapshot = MonitorSnapshot.Loading();

        public FloatingWindow()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.DoubleBuffered = true;
            this.Size = new Size(430, 112);
            this.BackColor = Color.FromArgb(18, 20, 24);
            this.ForeColor = Color.White;
            this.StartPosition = FormStartPosition.Manual;
            this.Font = new Font("Segoe UI", 9f);
            this.Cursor = Cursors.Hand;
            this.SetLocation();
            this.Click += delegate { this.RaiseDetails(); };
        }

        public void SetSnapshot(MonitorSnapshot next)
        {
            this.snapshot = next;
            this.Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var path = RoundedRect(new Rectangle(0, 0, this.Width, this.Height), 14))
            {
                this.Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new LinearGradientBrush(this.ClientRectangle, Color.FromArgb(28, 32, 40), Color.FromArgb(8, 92, 76), 0f))
            {
                g.FillRectangle(brush, this.ClientRectangle);
            }
            using (var path = RoundedRect(new Rectangle(0, 0, this.Width - 1, this.Height - 1), 14))
            using (var pen = new Pen(Color.FromArgb(85, 255, 255, 255)))
            {
                g.DrawPath(pen, path);
            }

            var accent = Color.FromArgb(58, 217, 169);
            using (var accentBrush = new SolidBrush(accent))
            using (var textBrush = new SolidBrush(Color.White))
            using (var mutedBrush = new SolidBrush(Color.FromArgb(205, 230, 236, 240)))
            using (var titleFont = new Font("Segoe UI Semibold", 10.5f))
            using (var mainFont = new Font("Segoe UI Semibold", 12.5f))
            using (var smallFont = new Font("Segoe UI", 9f))
            {
                g.FillEllipse(accentBrush, 16, 17, 10, 10);
                g.DrawString("Codex 用量", titleFont, textBrush, 34, 10);
                g.DrawString("剩余", smallFont, mutedBrush, 346, 12);

                if (this.snapshot.ErrorMessage != null)
                {
                    g.DrawString("连接失败", mainFont, textBrush, 18, 42);
                    g.DrawString(this.TrimText(this.snapshot.ErrorMessage, 48), smallFont, mutedBrush, 18, 72);
                    return;
                }

                g.DrawString(this.snapshot.Usage.FiveHour.ShortLine(), mainFont, textBrush, 18, 38);
                g.DrawString(this.snapshot.Usage.Weekly.ShortLine(), mainFont, textBrush, 18, 66);
                g.DrawString(this.snapshot.ResetCards.ShortLine(), smallFont, mutedBrush, 230, 84);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            this.SetLocation();
        }

        private void SetLocation()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(area.Right - this.Width - 14, area.Bottom - this.Height - 14);
        }

        private void RaiseDetails()
        {
            var handler = this.ShowDetailsRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private string TrimText(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max - 1) + "...";
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class StatusBarWindow : Form
    {
        public event EventHandler MouseEnteredBar;
        public event EventHandler MouseLeftBar;
        public event EventHandler TogglePinnedRequested;
        public event EventHandler HidePopupRequested;
        public event EventHandler RefreshRequested;
        public event EventHandler SettingsRequested;
        public event EventHandler ExitRequested;

        private List<string> messages = new List<string>();
        private int messageIndex;
        private string currentText = "";
        private string nextText = "";
        private int animationOffset;
        private readonly System.Windows.Forms.Timer rotateTimer;
        private readonly System.Windows.Forms.Timer animationTimer;
        private readonly System.Windows.Forms.Timer layoutTimer;
        private IntPtr taskbarHandle = IntPtr.Zero;

        public StatusBarWindow()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.DoubleBuffered = true;
            this.Size = new Size(560, 34);
            this.BackColor = Color.FromArgb(18, 20, 24);
            this.StartPosition = FormStartPosition.Manual;
            this.Font = new Font("Segoe UI", 9f);
            this.Cursor = Cursors.Hand;
            this.ContextMenuStrip = this.BuildMenu();
            this.SetLocation();
            this.MouseEnter += delegate { Raise(this.MouseEnteredBar); };
            this.MouseLeave += delegate { Raise(this.MouseLeftBar); };
            this.MouseClick += delegate(object sender, MouseEventArgs args) {
                if (args.Button == MouseButtons.Left) Raise(this.TogglePinnedRequested);
            };

            this.rotateTimer = new System.Windows.Forms.Timer();
            this.rotateTimer.Interval = 3600;
            this.rotateTimer.Tick += delegate { this.StartRoll(); };
            this.rotateTimer.Start();

            this.animationTimer = new System.Windows.Forms.Timer();
            this.animationTimer.Interval = 22;
            this.animationTimer.Tick += delegate { this.StepRoll(); };

            this.layoutTimer = new System.Windows.Forms.Timer();
            this.layoutTimer.Interval = 3000;
            this.layoutTimer.Tick += delegate { this.AttachToTaskbarOrFallback(); };
            this.layoutTimer.Start();
        }

        public void SetSnapshot(MonitorSnapshot next)
        {
            this.messages = next.BarMessages();
            if (this.messages.Count == 0) this.messages.Add("Codex --");
            this.messageIndex = Math.Min(this.messageIndex, this.messages.Count - 1);
            this.currentText = this.messages[this.messageIndex];
            this.nextText = "";
            this.animationOffset = 0;
            this.Invalidate();
        }

        public bool ContainsCursor()
        {
            return this.Visible && this.ClientRectangle.Contains(this.PointToClient(Cursor.Position));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            this.AttachToTaskbarOrFallback();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var path = RoundedRect(new Rectangle(0, 0, this.Width, this.Height), 11))
            {
                this.Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new LinearGradientBrush(this.ClientRectangle, Color.FromArgb(21, 24, 30), Color.FromArgb(11, 108, 91), 0f))
            {
                g.FillRectangle(brush, this.ClientRectangle);
            }
            using (var pen = new Pen(Color.FromArgb(95, 255, 255, 255)))
            using (var path = RoundedRect(new Rectangle(0, 0, this.Width - 1, this.Height - 1), 11))
            {
                g.DrawPath(pen, path);
            }

            using (var accent = new SolidBrush(Color.FromArgb(64, 230, 178)))
            using (var textBrush = new SolidBrush(Color.White))
            using (var font = new Font("Segoe UI Semibold", 9.5f))
            {
                g.FillEllipse(accent, 13, 12, 9, 9);
                var clip = g.Clip;
                g.SetClip(new Rectangle(32, 4, this.Width - 44, this.Height - 8));
                g.DrawString(this.currentText, font, textBrush, 32, 7 - this.animationOffset);
                if (!string.IsNullOrEmpty(this.nextText))
                {
                    g.DrawString(this.nextText, font, textBrush, 32, 7 + this.Height - this.animationOffset);
                }
                g.Clip = clip;
            }
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Hide popup", null, delegate { Raise(this.HidePopupRequested); });
            menu.Items.Add("Refresh now", null, delegate { Raise(this.RefreshRequested); });
            menu.Items.Add("Settings", null, delegate { Raise(this.SettingsRequested); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Raise(this.ExitRequested); });
            return menu;
        }

        private void StartRoll()
        {
            if (this.messages.Count <= 1 || this.animationTimer.Enabled) return;
            var nextIndex = (this.messageIndex + 1) % this.messages.Count;
            this.nextText = this.messages[nextIndex];
            this.animationOffset = 0;
            this.animationTimer.Start();
        }

        private void StepRoll()
        {
            this.animationOffset += 3;
            if (this.animationOffset >= this.Height)
            {
                this.animationTimer.Stop();
                this.messageIndex = (this.messageIndex + 1) % this.messages.Count;
                this.currentText = this.messages[this.messageIndex];
                this.nextText = "";
                this.animationOffset = 0;
            }
            this.Invalidate();
        }

        private void SetLocation()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(area.Right - this.Width - 12, area.Bottom - this.Height - 6);
        }

        private void AttachToTaskbarOrFallback()
        {
            var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero)
            {
                this.taskbarHandle = IntPtr.Zero;
                this.SetLocation();
                return;
            }

            if (this.taskbarHandle != taskbar)
            {
                this.taskbarHandle = taskbar;
                NativeMethods.SetParent(this.Handle, taskbar);
                var style = NativeMethods.GetWindowLongPtr(this.Handle, NativeMethods.GWL_STYLE).ToInt64();
                style &= ~NativeMethods.WS_POPUP;
                style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
                NativeMethods.SetWindowLongPtr(this.Handle, NativeMethods.GWL_STYLE, new IntPtr(style));
            }

            NativeMethods.RECT taskbarClient;
            if (!NativeMethods.GetClientRect(taskbar, out taskbarClient))
            {
                this.SetLocation();
                return;
            }

            int parentWidth = Math.Max(1, taskbarClient.Right - taskbarClient.Left);
            int parentHeight = Math.Max(1, taskbarClient.Bottom - taskbarClient.Top);
            int reservedRight = 230;

            var tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero)
            {
                NativeMethods.RECT trayRect;
                NativeMethods.RECT taskbarRect;
                if (NativeMethods.GetWindowRect(tray, out trayRect) && NativeMethods.GetWindowRect(taskbar, out taskbarRect))
                {
                    reservedRight = Math.Max(160, taskbarRect.Right - trayRect.Left + 10);
                }
            }

            int width = Math.Min(this.Width, Math.Max(240, parentWidth - reservedRight - 20));
            int x = Math.Max(8, parentWidth - reservedRight - width - 8);
            int y = Math.Max(2, (parentHeight - this.Height) / 2);
            NativeMethods.SetWindowPos(
                this.Handle,
                IntPtr.Zero,
                x,
                y,
                width,
                this.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
        }

        private static void Raise(EventHandler handler)
        {
            if (handler != null) handler(null, EventArgs.Empty);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class PopupWindow : Form
    {
        public event EventHandler MouseEnteredPopup;
        public event EventHandler MouseLeftPopup;
        public event EventHandler HidePopupRequested;
        public event EventHandler RefreshRequested;
        public event EventHandler SettingsRequested;
        public event EventHandler ExitRequested;

        private MonitorSnapshot snapshot = MonitorSnapshot.Loading();

        public PopupWindow()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.DoubleBuffered = true;
            this.Size = new Size(560, 218);
            this.BackColor = Color.FromArgb(18, 20, 24);
            this.StartPosition = FormStartPosition.Manual;
            this.Font = new Font("Segoe UI", 9f);
            this.ContextMenuStrip = this.BuildMenu();
            this.MouseEnter += delegate { Raise(this.MouseEnteredPopup); };
            this.MouseLeave += delegate { Raise(this.MouseLeftPopup); };
        }

        public void SetSnapshot(MonitorSnapshot next)
        {
            this.snapshot = next;
            this.Invalidate();
        }

        public void PlaceNear(Form bar)
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            var barScreen = bar.PointToScreen(Point.Empty);
            var barBottom = bar.PointToScreen(new Point(0, bar.Height)).Y;
            var x = Math.Min(Math.Max(area.Left + 8, barScreen.X), area.Right - this.Width - 8);
            var y = barScreen.Y - this.Height - 8;
            if (y < area.Top + 8) y = barBottom + 8;
            this.Location = new Point(x, y);
        }

        public bool ContainsCursor()
        {
            return this.Visible && this.ClientRectangle.Contains(this.PointToClient(Cursor.Position));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var path = RoundedRect(new Rectangle(0, 0, this.Width, this.Height), 16))
            {
                this.Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new LinearGradientBrush(this.ClientRectangle, Color.FromArgb(28, 32, 40), Color.FromArgb(8, 92, 76), 0f))
            {
                g.FillRectangle(brush, this.ClientRectangle);
            }
            using (var path = RoundedRect(new Rectangle(0, 0, this.Width - 1, this.Height - 1), 16))
            using (var pen = new Pen(Color.FromArgb(95, 255, 255, 255)))
            {
                g.DrawPath(pen, path);
            }

            using (var accentBrush = new SolidBrush(Color.FromArgb(58, 217, 169)))
            using (var textBrush = new SolidBrush(Color.White))
            using (var mutedBrush = new SolidBrush(Color.FromArgb(210, 230, 236, 240)))
            using (var titleFont = new Font("Segoe UI Semibold", 11.5f))
            using (var mainFont = new Font("Segoe UI Semibold", 10.5f))
            using (var smallFont = new Font("Segoe UI", 9f))
            {
                g.FillEllipse(accentBrush, 18, 19, 10, 10);
                g.DrawString("Codex\u5269\u4f59\u7528\u91cf&\u91cd\u7f6e", titleFont, textBrush, 36, 12);
                g.DrawString("Updated " + this.snapshot.UpdatedAt.ToString("HH:mm:ss"), smallFont, mutedBrush, this.Width - 130, 15);

                if (this.snapshot.ErrorMessage != null)
                {
                    g.DrawString("\u8fde\u63a5\u5931\u8d25", mainFont, textBrush, 20, 54);
                    g.DrawString(TrimText(this.snapshot.ErrorMessage, 70), smallFont, mutedBrush, 20, 82);
                    return;
                }

                int y = 54;
                g.DrawString(this.snapshot.Usage.FiveHour.DetailLine(), mainFont, textBrush, 20, y); y += 28;
                g.DrawString(this.snapshot.Usage.Weekly.DetailLine(), mainFont, textBrush, 20, y); y += 34;

                g.DrawString("\u91cd\u7f6e\u5361", mainFont, textBrush, 20, y);
                y += 26;
                if (this.snapshot.ResetCards.Expirations.Count == 0)
                {
                    g.DrawString("--", smallFont, mutedBrush, 28, y);
                }
                else
                {
                    for (int i = 0; i < this.snapshot.ResetCards.Expirations.Count; i++)
                    {
                        var line = "\u91cd\u7f6e\u5361 " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + UsageWindow.FormatResetDescription(this.snapshot.ResetCards.Expirations[i]);
                        g.DrawString(line, smallFont, mutedBrush, 28, y);
                        y += 22;
                    }
                }
            }
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Hide popup", null, delegate { Raise(this.HidePopupRequested); });
            menu.Items.Add("Refresh now", null, delegate { Raise(this.RefreshRequested); });
            menu.Items.Add("Settings", null, delegate { Raise(this.SettingsRequested); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Raise(this.ExitRequested); });
            return menu;
        }

        private static string TrimText(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max - 1) + "...";
        }

        private static void Raise(EventHandler handler)
        {
            if (handler != null) handler(null, EventArgs.Empty);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class DetailsForm : Form
    {
        public DetailsForm(MonitorSnapshot snapshot)
        {
            this.Text = "Codex Usage Details";
            this.Size = new Size(760, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(24, 27, 33);
            this.ForeColor = Color.White;

            var box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.Dock = DockStyle.Fill;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Color.FromArgb(18, 20, 24);
            box.ForeColor = Color.FromArgb(235, 240, 244);
            box.Font = new Font("Consolas", 10f);
            box.Text = snapshot.Details;
            this.Controls.Add(box);
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TextBox tokenBox = new TextBox();
        private readonly TextBox accountBox = new TextBox();
        private readonly TextBox usageBox = new TextBox();
        private readonly NumericUpDown refreshBox = new NumericUpDown();
        private readonly MonitorConfig initial;
        public MonitorConfig Config;

        public SettingsForm(MonitorConfig cfg)
        {
            this.initial = cfg;
            this.Config = cfg;
            this.Text = "Codex Monitor Settings";
            this.Size = new Size(620, 310);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(28, 31, 38);
            this.ForeColor = Color.White;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(16);
            layout.ColumnCount = 2;
            layout.RowCount = 5;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            this.Controls.Add(layout);

            this.tokenBox.UseSystemPasswordChar = true;
            this.accountBox.Text = cfg.AccountId ?? "";
            this.usageBox.Text = string.IsNullOrWhiteSpace(cfg.UsageEndpoint) ? "https://chatgpt.com/backend-api/wham/usage" : cfg.UsageEndpoint;
            this.refreshBox.Minimum = 60;
            this.refreshBox.Maximum = 3600;
            this.refreshBox.Value = Math.Max(60, cfg.RefreshSeconds);

            AddRow(layout, 0, "ACCESS_TOKEN", this.tokenBox);
            AddRow(layout, 1, "ACCOUNT_ID", this.accountBox);
            AddRow(layout, 2, "Usage endpoint", this.usageBox);
            AddRow(layout, 3, "Refresh seconds", this.refreshBox);

            var hint = new Label();
            hint.Text = "Token is encrypted with Windows DPAPI for the current Windows user. Leave token empty to keep the existing encrypted token.";
            hint.ForeColor = Color.FromArgb(190, 205, 215);
            hint.Dock = DockStyle.Fill;
            layout.Controls.Add(hint, 1, 4);

            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 48;
            buttons.Padding = new Padding(0, 6, 12, 8);
            this.Controls.Add(buttons);

            var save = new Button();
            save.Text = "Save";
            save.Width = 90;
            save.Click += delegate { this.SaveAndClose(); };
            buttons.Controls.Add(save);

            var cancel = new Button();
            cancel.Text = "Cancel";
            cancel.Width = 90;
            cancel.Click += delegate { this.DialogResult = DialogResult.Cancel; this.Close(); };
            buttons.Controls.Add(cancel);
        }

        private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
        {
            var lbl = new Label();
            lbl.Text = label;
            lbl.Dock = DockStyle.Fill;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.ForeColor = Color.White;
            control.Dock = DockStyle.Fill;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.Controls.Add(lbl, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private void SaveAndClose()
        {
            var next = new MonitorConfig();
            next.AccessTokenDpapi = this.initial.AccessTokenDpapi;
            if (!string.IsNullOrWhiteSpace(this.tokenBox.Text))
            {
                next.AccessTokenDpapi = SecretBox.Protect(this.tokenBox.Text.Trim());
            }
            next.AccountId = this.accountBox.Text.Trim();
            next.RefreshSeconds = (int)this.refreshBox.Value;
            next.ResetCardsEndpoint = this.initial.ResetCardsEndpoint;
            next.UsageEndpoint = this.usageBox.Text.Trim();
            next.Originator = this.initial.Originator;
            next.OpenAIBeta = this.initial.OpenAIBeta;
            if (string.IsNullOrWhiteSpace(next.AccessTokenDpapi) || string.IsNullOrWhiteSpace(next.AccountId))
            {
                MessageBox.Show("ACCESS_TOKEN and ACCOUNT_ID are required.", "Codex Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            this.Config = next;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

    internal static class NativeMethods
    {
        public const int GWL_STYLE = -16;
        public const long WS_CHILD = 0x40000000L;
        public const long WS_POPUP = unchecked((long)0x80000000L);
        public const long WS_VISIBLE = 0x10000000L;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);
    }

    internal static class IconFactory
    {
        public static Icon CreateTrayIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(14, 118, 96), Color.FromArgb(33, 38, 50), 45f))
                {
                    g.FillEllipse(bg, 2, 2, 28, 28);
                }
                using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 2f))
                {
                    g.DrawEllipse(pen, 2, 2, 28, 28);
                }
                using (var font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.White))
                {
                    g.DrawString("C", font, brush, 10, 7);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
