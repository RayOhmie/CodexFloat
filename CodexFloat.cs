using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using WinFormsTimer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("CodexFloat")]
[assembly: AssemblyDescription("Windows floating monitor for Codex remaining usage and reset time.")]
[assembly: AssemblyCompany("RayOhmie")]
[assembly: AssemblyProduct("CodexFloat")]
[assembly: AssemblyCopyright("Copyright (C) 2026 By RayOhmie")]
[assembly: AssemblyVersion("0.1.47.0")]
[assembly: AssemblyFileVersion("0.1.47.0")]
[assembly: AssemblyInformationalVersion("0.1.47")]

namespace CodexFloat
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
        private readonly ConfigStore store = new ConfigStore();
        private MonitorConfig config;
        private readonly MiniBarForm bar;
        private readonly NotifyIcon notifyIcon;
        private readonly WinFormsTimer refreshTimer;
        private volatile bool refreshing;
        private MonitorSnapshot snapshot = MonitorSnapshot.Loading();
        private EnvironmentStatusPopupForm environmentStatusPopup;
        private bool startupEnvironmentCheckPending = true;
        private bool blockedByHighRiskEnvironment;
        private EnvironmentInfo pendingInitialTrustEnvironment;
        private EnvironmentInfo pendingMediumRiskEnvironment;

        public MonitorContext()
        {
            this.config = this.store.Load();
            T.SetLanguage(this.config.Language);
            if (!this.config.HasUsableSecret()) this.TryAutoImportSilently();
            this.ApplyStartupSetting();

            this.bar = new MiniBarForm();
            this.notifyIcon = this.CreateTrayIcon();
            this.bar.FormClosing += delegate { this.SaveCurrentPosition(); };

            this.bar.SetSnapshot(this.snapshot);
            this.bar.SetTheme(this.config.ThemeName);
            this.bar.ApplyBehavior(this.config);
            this.bar.LocationSaved += delegate(object sender, PointEventArgs e) {
                this.config.FloatingX = e.Location.X;
                this.config.FloatingY = e.Location.Y;
                this.store.Save(this.config);
            };
            this.bar.RefreshRequested += delegate { this.RefreshNow(true); };
            this.bar.SettingsRequested += delegate { this.ShowSettings(false); };
            this.bar.TrustedEnvironmentsRequested += delegate { this.ShowTrustedEnvironments(); };
            this.bar.ResetPositionRequested += delegate { this.ResetPosition(); };
            this.bar.HelpRequested += delegate { this.ShowHelp(); };
            this.bar.ErrorLogRequested += delegate { this.ShowErrorLogs(); };
            this.bar.AboutRequested += delegate { this.ShowAbout(); };
            this.bar.CheckUpdatesRequested += delegate { this.CheckUpdates(); };
            this.bar.ThemeChanged += delegate(object sender, ThemeEventArgs e) {
                this.config.ThemeName = e.ThemeName;
                this.store.Save(this.config);
            };
            this.bar.LanguageChanged += delegate(object sender, LanguageEventArgs e) {
                this.ChangeLanguage(e.Language);
            };
            this.bar.BehaviorChanged += delegate(object sender, BehaviorEventArgs e) {
                this.config.AutoStart = e.Config.AutoStart;
                this.config.OpacityPercent = e.Config.OpacityPercent;
                this.config.AlwaysOnTop = e.Config.AlwaysOnTop;
                this.config.MouseClickThrough = e.Config.MouseClickThrough;
                this.config.LockPosition = e.Config.LockPosition;
                this.config.ShowUserNameInDetails = e.Config.ShowUserNameInDetails;
                this.config.EnvironmentCheckOnStartup = e.Config.EnvironmentCheckOnStartup;
                this.config.EnvironmentConfirmMediumRiskOnManualRefresh = e.Config.EnvironmentConfirmMediumRiskOnManualRefresh;
                this.config.EnvironmentRecheckHighRiskOnManualRefresh = e.Config.EnvironmentRecheckHighRiskOnManualRefresh;
                this.CopyScrollSelections(e.Config, this.config);
                this.store.Save(this.config);
                this.ApplyStartupSetting();
                this.bar.ApplyBehavior(this.config);
            };
            this.bar.ExitRequested += delegate { this.ExitApp(); };

            this.bar.SetInitialLocation(this.config.FloatingX, this.config.FloatingY);
            this.bar.Show();

            if (!this.config.HasUsableSecret())
            {
                this.ShowSettings(true);
            }

            this.refreshTimer = new WinFormsTimer();
            this.refreshTimer.Interval = Math.Max(60, this.config.RefreshSeconds) * 1000;
            this.refreshTimer.Tick += delegate { this.RefreshNow(false); };
            this.refreshTimer.Start();

            this.RefreshNow(false);
        }

        private void TryAutoImportSilently()
        {
            ImportedCredentials imported;
            string message;
            if (!CredentialImporter.TryFind(out imported, out message)) return;
            this.config.AccessTokenDpapi = SecretBox.Protect(imported.AccessToken);
            this.config.AccountIdDpapi = SecretBox.Protect(imported.AccountId);
            this.config.AccountId = "";
            this.store.Save(this.config);
        }

        private void ApplyStartupSetting()
        {
            try
            {
                StartupManager.SetEnabled(this.config.AutoStart);
            }
            catch
            {
                this.config.AutoStart = StartupManager.IsEnabled();
            }
        }

        private NotifyIcon CreateTrayIcon()
        {
            var icon = new NotifyIcon();
            icon.Icon = IconFactory.CreateTrayIcon();
            icon.Text = "CodexFloat";
            icon.Visible = true;
            icon.ContextMenuStrip = this.BuildTrayMenu();
            icon.DoubleClick += delegate { this.ShowPopup(); };
            return icon;
        }

        private Rectangle FloatingScreenBounds()
        {
            return Screen.FromControl(this.bar).WorkingArea;
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(T.Text("显示详情"), null, delegate { this.bar.ShowExpandedTemporarily(); });
            menu.Items.Add(T.Text("刷新"), null, delegate { this.RefreshNow(true); });
            menu.Items.Add(this.BuildAppearanceMenu());
            menu.Items.Add(this.BuildBehaviorMenu());
            menu.Items.Add(this.BuildEnvironmentSafetyMenu());
            menu.Items.Add(this.BuildScrollDataMenu());
            menu.Items.Add(this.BuildLanguageMenu());
            menu.Items.Add(T.Text("设置"), null, delegate { this.ShowSettings(false); });
            menu.Items.Add(T.Text("重置悬浮窗位置"), null, delegate { this.ResetPosition(); });
            menu.Items.Add(T.Text("帮助"), null, delegate { this.ShowHelp(); });
            menu.Items.Add(T.Text("关于"), null, delegate { this.ShowAbout(); });
            menu.Items.Add(T.Text("检查更新"), null, delegate { this.CheckUpdates(); });
            menu.Items.Add(T.Text("查看错误日志"), null, delegate { this.ShowErrorLogs(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(T.Text("退出"), null, delegate { this.ExitApp(); });
            MenuDropDownPlacer.Attach(menu, this.FloatingScreenBounds);
            return menu;
        }

        private ToolStripMenuItem BuildAppearanceMenu()
        {
            var root = new ToolStripMenuItem(T.Text("外观"));
            foreach (var theme in ThemePalette.Names)
            {
                var item = new ToolStripMenuItem(theme);
                item.Checked = string.Equals(theme, this.config.ThemeName, StringComparison.OrdinalIgnoreCase);
                item.Click += delegate(object sender, EventArgs e) {
                    this.config.ThemeName = ((ToolStripMenuItem)sender).Text;
                    this.store.Save(this.config);
                    this.bar.SetTheme(this.config.ThemeName);
                    foreach (ToolStripMenuItem child in root.DropDownItems) child.Checked = child.Text == this.config.ThemeName;
                };
                root.DropDownItems.Add(item);
            }
            return root;
        }

        private ToolStripMenuItem BuildBehaviorMenu()
        {
            var root = new ToolStripMenuItem(T.Text("快捷设置"));
            root.DropDownOpening += delegate {
                root.DropDownItems.Clear();
                root.DropDownItems.Add(this.BuildToggleItem(T.Text("开机自动启动"), this.config.AutoStart, delegate { this.ToggleAutoStart(); }));
                root.DropDownItems.Add(this.BuildOpacityMenu());
                MenuDropDownPlacer.AttachChildren(root, this.FloatingScreenBounds);
                root.DropDownItems.Add(this.BuildToggleItem(T.Text("总是置顶"), this.config.AlwaysOnTop, delegate { this.ToggleAlwaysOnTop(); }));
                root.DropDownItems.Add(this.BuildToggleItem(T.Text("鼠标穿透"), this.config.MouseClickThrough, delegate { this.ToggleMouseClickThrough(); }));
                root.DropDownItems.Add(this.BuildToggleItem(T.Text("锁定窗口位置"), this.config.LockPosition, delegate { this.ToggleLockPosition(); }));
            };
            return root;
        }

        private ToolStripMenuItem BuildEnvironmentSafetyMenu()
        {
            var root = new ToolStripMenuItem(T.Text("env_safety_title"));
            root.DropDownItems.Add(new ToolStripMenuItem(T.Text("env_check_on_startup")));
            root.DropDownOpening += delegate {
                root.DropDownItems.Clear();
                root.DropDownItems.Add(this.BuildToggleItem(T.Text("env_check_on_startup"), this.config.EnvironmentCheckOnStartup, delegate { this.ToggleEnvironmentCheckOnStartup(); }));
                root.DropDownItems.Add(this.BuildToggleItem(T.Text("env_confirm_medium_on_manual"), this.config.EnvironmentConfirmMediumRiskOnManualRefresh, delegate { this.ToggleEnvironmentConfirmMediumOnManual(); }));
                root.DropDownItems.Add(this.BuildToggleItem(T.Text("env_recheck_high_on_manual"), this.config.EnvironmentRecheckHighRiskOnManualRefresh, delegate { this.ToggleEnvironmentRecheckHighOnManual(); }));
                root.DropDownItems.Add(new ToolStripSeparator());
                root.DropDownItems.Add(T.Text("env_trusted_library") + " (" + this.TrustedEnvironmentCount().ToString(CultureInfo.InvariantCulture) + ")", null, delegate { this.ShowTrustedEnvironments(); });
                MenuDropDownPlacer.AttachChildren(root, this.FloatingScreenBounds);
            };
            return root;
        }

        private ToolStripMenuItem BuildScrollDataMenu()
        {
            var root = new ToolStripMenuItem(T.Text("滚动数据"));
            root.DropDownItems.Add(this.BuildToggleItem(T.Text("scroll_5h"), this.config.ScrollShowFiveHour, delegate { this.config.ScrollShowFiveHour = !this.config.ScrollShowFiveHour; this.SaveScrollSelection(); }));
            root.DropDownItems.Add(this.BuildToggleItem(T.Text("scroll_weekly"), this.config.ScrollShowWeekly, delegate { this.config.ScrollShowWeekly = !this.config.ScrollShowWeekly; this.SaveScrollSelection(); }));
            root.DropDownItems.Add(this.BuildToggleItem(T.Text("scroll_gpt_55_xhigh"), this.config.ScrollShowGpt55XHigh, delegate { this.config.ScrollShowGpt55XHigh = !this.config.ScrollShowGpt55XHigh; this.SaveScrollSelection(); }));
            root.DropDownItems.Add(this.BuildToggleItem(T.Text("scroll_gpt_55_high"), this.config.ScrollShowGpt55High, delegate { this.config.ScrollShowGpt55High = !this.config.ScrollShowGpt55High; this.SaveScrollSelection(); }));
            root.DropDownItems.Add(this.BuildToggleItem(T.Text("scroll_gpt_55_medium"), this.config.ScrollShowGpt55Medium, delegate { this.config.ScrollShowGpt55Medium = !this.config.ScrollShowGpt55Medium; this.SaveScrollSelection(); }));
            root.DropDownItems.Add(this.BuildToggleItem(T.Text("scroll_gpt_54_xhigh"), this.config.ScrollShowGpt54XHigh, delegate { this.config.ScrollShowGpt54XHigh = !this.config.ScrollShowGpt54XHigh; this.SaveScrollSelection(); }));
            return root;
        }

        private void SaveScrollSelection()
        {
            this.store.Save(this.config);
            this.bar.ApplyBehavior(this.config);
            this.notifyIcon.ContextMenuStrip = this.BuildTrayMenu();
        }

        private void CopyScrollSelections(MonitorConfig source, MonitorConfig target)
        {
            target.ScrollShowFiveHour = source.ScrollShowFiveHour;
            target.ScrollShowWeekly = source.ScrollShowWeekly;
            target.ScrollShowGpt55XHigh = source.ScrollShowGpt55XHigh;
            target.ScrollShowGpt55High = source.ScrollShowGpt55High;
            target.ScrollShowGpt55Medium = source.ScrollShowGpt55Medium;
            target.ScrollShowGpt54XHigh = source.ScrollShowGpt54XHigh;
        }

        private ToolStripMenuItem BuildOpacityMenu()
        {
            var root = new ToolStripMenuItem(T.Text("不透明度"));
            foreach (var value in new[] { 100, 90, 80, 70, 60, 50, 40, 35 })
            {
                var item = new ToolStripMenuItem(value.ToString(CultureInfo.InvariantCulture) + "%");
                item.Checked = this.config.OpacityPercent == value;
                item.Click += delegate(object sender, EventArgs e) {
                    var text = ((ToolStripMenuItem)sender).Text.Replace("%", "");
                    int parsed;
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        this.config.OpacityPercent = parsed;
                        this.store.Save(this.config);
                        this.bar.ApplyBehavior(this.config);
                    }
                };
                root.DropDownItems.Add(item);
            }
            return root;
        }

        private ToolStripMenuItem BuildLanguageMenu()
        {
            var root = new ToolStripMenuItem(T.Text("语言"));
            var zh = new ToolStripMenuItem("简体中文");
            zh.Checked = T.IsChinese;
            zh.Click += delegate { this.ChangeLanguage("zh-CN"); };
            var en = new ToolStripMenuItem("English");
            en.Checked = !T.IsChinese;
            en.Click += delegate { this.ChangeLanguage("en-US"); };
            root.DropDownItems.Add(zh);
            root.DropDownItems.Add(en);
            return root;
        }

        private void ChangeLanguage(string language)
        {
            this.config.Language = language == "en-US" ? "en-US" : "zh-CN";
            T.SetLanguage(this.config.Language);
            this.store.Save(this.config);
            this.notifyIcon.ContextMenuStrip = this.BuildTrayMenu();
            this.bar.SetLanguage(this.config.Language);
            this.bar.SetSnapshot(this.snapshot);
        }

        private ToolStripMenuItem BuildToggleItem(string text, bool isChecked, Action toggle)
        {
            var item = new ToolStripMenuItem(text);
            item.Checked = isChecked;
            item.Click += delegate { toggle(); };
            return item;
        }

        private void ToggleAutoStart()
        {
            this.config.AutoStart = !this.config.AutoStart;
            this.store.Save(this.config);
            this.ApplyStartupSetting();
        }

        private void ToggleAlwaysOnTop()
        {
            this.config.AlwaysOnTop = !this.config.AlwaysOnTop;
            this.store.Save(this.config);
            this.bar.ApplyBehavior(this.config);
        }

        private void ToggleMouseClickThrough()
        {
            this.config.MouseClickThrough = !this.config.MouseClickThrough;
            this.store.Save(this.config);
            this.bar.ApplyBehavior(this.config);
        }

        private void ToggleLockPosition()
        {
            this.config.LockPosition = !this.config.LockPosition;
            this.store.Save(this.config);
            this.bar.ApplyBehavior(this.config);
        }

        private void ToggleEnvironmentCheckOnStartup()
        {
            this.config.EnvironmentCheckOnStartup = !this.config.EnvironmentCheckOnStartup;
            this.SaveEnvironmentSettings();
        }

        private void ToggleEnvironmentConfirmMediumOnManual()
        {
            this.config.EnvironmentConfirmMediumRiskOnManualRefresh = !this.config.EnvironmentConfirmMediumRiskOnManualRefresh;
            this.SaveEnvironmentSettings();
        }

        private void ToggleEnvironmentRecheckHighOnManual()
        {
            this.config.EnvironmentRecheckHighRiskOnManualRefresh = !this.config.EnvironmentRecheckHighRiskOnManualRefresh;
            this.SaveEnvironmentSettings();
        }

        private void SaveEnvironmentSettings()
        {
            this.store.Save(this.config);
            this.bar.ApplyBehavior(this.config);
            this.notifyIcon.ContextMenuStrip = this.BuildTrayMenu();
        }

        private int TrustedEnvironmentCount()
        {
            return this.config.TrustedEnvironments == null ? 0 : this.config.TrustedEnvironments.Count;
        }

        private void RefreshNow()
        {
            this.RefreshNow(true);
        }

        private void RefreshNow(bool manual)
        {
            if (this.refreshing) return;
            this.refreshing = true;
            this.bar.SetRefreshing(true);
            this.bar.SetStatusText(this.HasCurrentUsageData() ? T.Text("更新中省略") : T.Text("连接中") + "...");

            ThreadPool.QueueUserWorkItem(delegate {
                MonitorSnapshot next;
                try
                {
                    EnvironmentInfo environmentToConfirm = null;
                    if (!this.config.HasUsableSecret())
                    {
                        next = ErrorAdvisor.MissingCredentials();
                    }
                    else
                    {
                        var gate = this.EvaluateEnvironmentBeforeQuery(manual);
                        environmentToConfirm = gate.EnvironmentToConfirmAfterSuccess;
                        next = gate.AllowQuery ? CodexClient.Fetch(this.config) : gate.BlockedSnapshot;
                    }
                    if (environmentToConfirm != null && next != null && next.ErrorMessage == null)
                    {
                        this.ConfirmEnvironment(environmentToConfirm);
                        this.store.Save(this.config);
                    }
                }
                catch (Exception ex)
                {
                    next = ErrorAdvisor.FromException(ex);
                }

                this.PostToUi(delegate {
                    this.refreshing = false;
                    this.bar.SetRefreshing(false);
                    this.snapshot = next;
                    if (next.ErrorMessage != null) ErrorLogStore.Write(next);
                    this.bar.SetSnapshot(next);
                    this.notifyIcon.Text = next.TrayText();
                });
            });
        }

        private EnvironmentGateResult EvaluateEnvironmentBeforeQuery(bool manual)
        {
            var preGate = this.PrepareEnvironmentGate(manual);
            if (preGate != null) return preGate;

            EnvironmentInfo current;
            try
            {
                current = EnvironmentSafetyClient.Fetch();
            }
            catch (Exception ex)
            {
                this.ShowEnvironmentStatus(T.Text("env_check_failed_title"), T.Text("env_check_failed_message"), EnvironmentRiskLevel.Medium, null);
                return EnvironmentGateResult.Block(MonitorSnapshot.Error("ENV_CHECK_FAILED", T.Text("env_check_failed_title"), T.Text("env_check_failed_message") + "\r\n\r\n" + ex.Message));
            }

            if (current.IsHighRiskCountry())
            {
                this.blockedByHighRiskEnvironment = true;
                this.pendingInitialTrustEnvironment = null;
                this.pendingMediumRiskEnvironment = null;
                this.ShowEnvironmentStatus(T.Text("env_high_risk_title"), T.Text("env_high_risk_message"), EnvironmentRiskLevel.High, current);
                return EnvironmentGateResult.Block(MonitorSnapshot.Error("ENV_HIGH_RISK", T.Text("env_high_risk_title"), T.Text("env_high_risk_message") + "\r\n" + current.DisplayText()));
            }

            if (!this.HasConfirmedEnvironment())
            {
                var firstChoice = this.PromptInitialTrustEnvironment(current);
                if (firstChoice == EnvironmentPromptChoice.Continue)
                {
                    this.blockedByHighRiskEnvironment = false;
                    this.pendingInitialTrustEnvironment = null;
                    this.pendingMediumRiskEnvironment = null;
                    return EnvironmentGateResult.AllowWithConfirmation(current);
                }

                this.pendingInitialTrustEnvironment = current;
                this.pendingMediumRiskEnvironment = null;
                return EnvironmentGateResult.Block(MonitorSnapshot.Error("ENV_INITIAL_TRUST_PENDING", T.Text("env_initial_trust_title"), T.Text("env_initial_trust_pending_message") + "\r\n" + current.DisplayText()));
            }

            if (this.HasConfirmedEnvironment() && this.IsSameConfirmedEnvironment(current))
            {
                this.blockedByHighRiskEnvironment = false;
                this.pendingInitialTrustEnvironment = null;
                this.pendingMediumRiskEnvironment = null;
                this.ShowEnvironmentStatus(T.Text("env_safe_title"), T.Text("env_safe_message"), EnvironmentRiskLevel.Safe, current);
                return EnvironmentGateResult.Allow();
            }

            var promptMessage = T.Text("env_medium_risk_changed");
            var choice = this.PromptMediumRiskEnvironment(current, promptMessage);
            if (choice == EnvironmentPromptChoice.Continue)
            {
                this.blockedByHighRiskEnvironment = false;
                this.pendingInitialTrustEnvironment = null;
                this.pendingMediumRiskEnvironment = null;
                return EnvironmentGateResult.AllowWithConfirmation(current);
            }

            this.pendingMediumRiskEnvironment = current;
            return EnvironmentGateResult.Block(MonitorSnapshot.Error("ENV_MEDIUM_RISK_PENDING", T.Text("env_medium_risk_title"), promptMessage + "\r\n" + current.DisplayText()));
        }

        private EnvironmentGateResult PrepareEnvironmentGate(bool manual)
        {
            if (this.pendingInitialTrustEnvironment != null)
            {
                if (!manual)
                {
                    return EnvironmentGateResult.Block(MonitorSnapshot.Error("ENV_INITIAL_TRUST_PENDING", T.Text("env_initial_trust_title"), T.Text("env_initial_trust_pending_message")));
                }
                return null;
            }

            if (this.pendingMediumRiskEnvironment != null)
            {
                if (!manual)
                {
                    return EnvironmentGateResult.Block(MonitorSnapshot.Error("ENV_MEDIUM_RISK_PENDING", T.Text("env_medium_risk_title"), T.Text("env_medium_pending_message")));
                }
                if (!this.config.EnvironmentConfirmMediumRiskOnManualRefresh)
                {
                    var environmentToConfirm = this.pendingMediumRiskEnvironment;
                    this.pendingMediumRiskEnvironment = null;
                    return EnvironmentGateResult.AllowWithConfirmation(environmentToConfirm);
                }
                return null;
            }

            if (this.blockedByHighRiskEnvironment)
            {
                if (!manual)
                {
                    return EnvironmentGateResult.Block(MonitorSnapshot.Error("ENV_HIGH_RISK", T.Text("env_high_risk_title"), T.Text("env_high_risk_wait_message")));
                }
                if (!this.config.EnvironmentRecheckHighRiskOnManualRefresh)
                {
                    this.blockedByHighRiskEnvironment = false;
                    return EnvironmentGateResult.Allow();
                }
                return null;
            }

            if (this.startupEnvironmentCheckPending)
            {
                this.startupEnvironmentCheckPending = false;
                if (this.config.EnvironmentCheckOnStartup) return null;
            }

            return EnvironmentGateResult.Allow();
        }

        private EnvironmentPromptChoice PromptMediumRiskEnvironment(EnvironmentInfo info, string message)
        {
            if (this.bar.IsDisposed) return EnvironmentPromptChoice.Wait;
            var choice = EnvironmentPromptChoice.Wait;
            this.bar.Invoke(new MethodInvoker(delegate {
                using (var form = new EnvironmentRiskPromptForm(info, message))
                {
                    form.PlaceBottomRight(Screen.FromControl(this.bar).WorkingArea);
                    form.ShowDialog(this.bar);
                    choice = form.Choice;
                }
            }));
            return choice;
        }

        private EnvironmentPromptChoice PromptInitialTrustEnvironment(EnvironmentInfo info)
        {
            if (this.bar.IsDisposed) return EnvironmentPromptChoice.Wait;
            var choice = EnvironmentPromptChoice.Wait;
            this.bar.Invoke(new MethodInvoker(delegate {
                using (var form = new EnvironmentInitialTrustPromptForm(info))
                {
                    form.PlaceBottomRight(Screen.FromControl(this.bar).WorkingArea);
                    form.ShowDialog(this.bar);
                    choice = form.Choice;
                }
            }));
            return choice;
        }

        private void ShowEnvironmentStatus(string title, string message, EnvironmentRiskLevel riskLevel, EnvironmentInfo info)
        {
            this.PostToUi(delegate {
                if (this.bar.IsDisposed) return;
                if (this.environmentStatusPopup != null && !this.environmentStatusPopup.IsDisposed)
                {
                    this.environmentStatusPopup.Close();
                }
                this.environmentStatusPopup = new EnvironmentStatusPopupForm(riskLevel, title, message, info);
                this.environmentStatusPopup.PlaceBottomRight(Screen.FromControl(this.bar).WorkingArea);
                this.environmentStatusPopup.Show(this.bar);
            });
        }

        private bool HasConfirmedEnvironment()
        {
            return this.config.TrustedEnvironments != null && this.config.TrustedEnvironments.Count > 0;
        }

        private bool IsSameConfirmedEnvironment(EnvironmentInfo info)
        {
            if (info == null || this.config.TrustedEnvironments == null) return false;
            foreach (var trusted in this.config.TrustedEnvironments)
            {
                if (trusted != null && trusted.Matches(info)) return true;
            }
            return false;
        }

        private void ConfirmEnvironment(EnvironmentInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Ip)) return;
            if (this.config.TrustedEnvironments == null) this.config.TrustedEnvironments = new List<TrustedEnvironment>();
            foreach (var trusted in this.config.TrustedEnvironments)
            {
                if (trusted != null && trusted.Matches(info))
                {
                    trusted.UpdateFrom(info);
                    this.UpdateLegacyConfirmedEnvironmentFields(trusted);
                    return;
                }
            }
            var added = TrustedEnvironment.FromInfo(info);
            this.config.TrustedEnvironments.Add(added);
            this.UpdateLegacyConfirmedEnvironmentFields(added);
        }

        private void UpdateLegacyConfirmedEnvironmentFields(TrustedEnvironment trusted)
        {
            if (trusted == null)
            {
                this.config.EnvironmentLastConfirmedIp = "";
                this.config.EnvironmentLastConfirmedCountryCode = "";
                this.config.EnvironmentLastConfirmedCountryName = "";
                this.config.EnvironmentLastConfirmedRegion = "";
                this.config.EnvironmentLastConfirmedCity = "";
                return;
            }
            this.config.EnvironmentLastConfirmedIp = trusted.Ip;
            this.config.EnvironmentLastConfirmedCountryCode = trusted.CountryCode;
            this.config.EnvironmentLastConfirmedCountryName = trusted.CountryName;
            this.config.EnvironmentLastConfirmedRegion = trusted.Region;
            this.config.EnvironmentLastConfirmedCity = trusted.City;
        }

        private bool HasCurrentUsageData()
        {
            return this.snapshot != null
                && this.snapshot.ErrorMessage == null
                && this.snapshot.Usage != null
                && (this.snapshot.Usage.FiveHour.Available || this.snapshot.Usage.Weekly.Available);
        }

        private void ShowPopup()
        {
            this.bar.ShowExpandedTemporarily();
        }

        private void ResetPosition()
        {
            this.config.FloatingX = null;
            this.config.FloatingY = null;
            this.store.Save(this.config);
            this.bar.SetInitialLocation(null, null);
        }

        private void ShowSettings(bool firstRun)
        {
            using (var form = new SettingsForm(this.config))
            {
                if (firstRun) form.StartPosition = FormStartPosition.CenterScreen;
                if (form.ShowDialog() == DialogResult.OK)
                {
                    this.config = form.Config;
                    this.store.Save(this.config);
                    this.ApplyStartupSetting();
                    this.bar.ApplyBehavior(this.config);
                    this.notifyIcon.ContextMenuStrip = this.BuildTrayMenu();
                    this.bar.SetInitialLocation(this.config.FloatingX, this.config.FloatingY);
                    this.refreshTimer.Interval = Math.Max(60, this.config.RefreshSeconds) * 1000;
                    this.RefreshNow(false);
                }
            }
        }

        private void ShowTrustedEnvironments()
        {
            using (var form = new TrustedEnvironmentsForm(this.config.TrustedEnvironments))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    this.config.TrustedEnvironments = form.TrustedEnvironments;
                    var last = this.config.TrustedEnvironments.Count == 0 ? null : this.config.TrustedEnvironments[this.config.TrustedEnvironments.Count - 1];
                    this.UpdateLegacyConfirmedEnvironmentFields(last);
                    this.store.Save(this.config);
                    this.bar.ApplyBehavior(this.config);
                    this.notifyIcon.ContextMenuStrip = this.BuildTrayMenu();
                }
            }
        }

        private void ShowHelp()
        {
            LinkOpener.OpenUrl(AppLinks.ProjectGithub);
        }

        private void ShowErrorLogs()
        {
            ErrorLogStore.OpenFolder();
        }

        private void ShowAbout()
        {
            using (var dialog = new AboutForm())
            {
                dialog.ShowDialog();
            }
        }

        private void CheckUpdates()
        {
            LinkOpener.OpenUrl(AppLinks.Releases);
        }

        private void PostToUi(Action action)
        {
            if (this.bar.IsDisposed) return;
            this.bar.BeginInvoke(action);
        }

        private void ExitApp()
        {
            this.notifyIcon.Visible = false;
            this.notifyIcon.Dispose();
            this.bar.Close();
            this.refreshTimer.Stop();
            Application.Exit();
        }

        private void SaveCurrentPosition()
        {
            var p = this.bar.LocationForSave();
            this.config.FloatingX = p.X;
            this.config.FloatingY = p.Y;
            this.store.Save(this.config);
        }
    }

    internal sealed class MonitorConfig
    {
        public string AccessTokenDpapi = "";
        public string AccountIdDpapi = "";
        public string AccountId = ""; // Legacy plaintext value, migrated on next save.
        public int RefreshSeconds = 300;
        public string ResetCardsEndpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
        public string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
        public string Originator = "Codex Desktop";
        public string OpenAIBeta = "codex-1";
        public string ThemeName = "Traffic Classic";
        public string Language = "zh-CN";
        public bool AutoStart;
        public int OpacityPercent = 100;
        public bool AlwaysOnTop = true;
        public bool MouseClickThrough;
        public bool LockPosition;
        public bool ShowUserNameInDetails = true;
        public bool ScrollShowFiveHour = true;
        public bool ScrollShowWeekly = true;
        public bool ScrollShowGpt55XHigh = true;
        public bool ScrollShowGpt55High = true;
        public bool ScrollShowGpt55Medium = true;
        public bool ScrollShowGpt54XHigh = true;
        public bool EnvironmentCheckOnStartup = true;
        public bool EnvironmentConfirmMediumRiskOnManualRefresh = true;
        public bool EnvironmentRecheckHighRiskOnManualRefresh = true;
        public string EnvironmentLastConfirmedIp = "";
        public string EnvironmentLastConfirmedCountryCode = "";
        public string EnvironmentLastConfirmedCountryName = "";
        public string EnvironmentLastConfirmedRegion = "";
        public string EnvironmentLastConfirmedCity = "";
        public List<TrustedEnvironment> TrustedEnvironments = new List<TrustedEnvironment>();
        public int? FloatingX;
        public int? FloatingY;

        public bool HasUsableSecret()
        {
            return !string.IsNullOrWhiteSpace(this.AccessTokenDpapi) && (!string.IsNullOrWhiteSpace(this.AccountIdDpapi) || !string.IsNullOrWhiteSpace(this.AccountId));
        }

        public string GetAccountId()
        {
            if (!string.IsNullOrWhiteSpace(this.AccountIdDpapi)) return SecretBox.Unprotect(this.AccountIdDpapi);
            return this.AccountId ?? "";
        }

        public void CopyTrustedEnvironmentsFrom(MonitorConfig source)
        {
            this.TrustedEnvironments = new List<TrustedEnvironment>();
            if (source == null || source.TrustedEnvironments == null) return;
            foreach (var item in source.TrustedEnvironments)
            {
                if (item != null) this.TrustedEnvironments.Add(item.Clone());
            }
        }
    }

    internal sealed class ConfigStore
    {
        private readonly string configDir;
        private readonly string configPath;
        private readonly string legacyConfigPath;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public ConfigStore()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            this.configDir = Path.Combine(appData, "CodexFloat");
            this.configPath = Path.Combine(this.configDir, "config.json");
            this.legacyConfigPath = Path.Combine(appData, "CodexResetMonitor", "config.json");
        }

        public MonitorConfig Load()
        {
            var cfg = new MonitorConfig();
            this.MigrateLegacyConfig();
            if (!File.Exists(this.configPath)) return cfg;
            try
            {
                var json = File.ReadAllText(this.configPath, Encoding.UTF8);
                var root = this.serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return cfg;
                cfg.AccessTokenDpapi = GetString(root, "access_token_dpapi", cfg.AccessTokenDpapi);
                cfg.AccountIdDpapi = GetString(root, "account_id_dpapi", cfg.AccountIdDpapi);
                cfg.AccountId = GetString(root, "account_id", cfg.AccountId);
                cfg.RefreshSeconds = GetInt(root, "refresh_seconds", cfg.RefreshSeconds);
                cfg.ResetCardsEndpoint = GetString(root, "reset_cards_endpoint", cfg.ResetCardsEndpoint);
                cfg.UsageEndpoint = GetString(root, "usage_endpoint", cfg.UsageEndpoint);
                if (string.IsNullOrWhiteSpace(cfg.UsageEndpoint)) cfg.UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
                cfg.Originator = GetString(root, "originator", cfg.Originator);
                cfg.OpenAIBeta = GetString(root, "openai_beta", cfg.OpenAIBeta);
                cfg.ThemeName = GetString(root, "theme", cfg.ThemeName);
                cfg.Language = GetString(root, "language", cfg.Language);
                cfg.AutoStart = GetBool(root, "auto_start", cfg.AutoStart);
                cfg.OpacityPercent = Math.Max(35, Math.Min(100, GetInt(root, "opacity_percent", cfg.OpacityPercent)));
                cfg.AlwaysOnTop = GetBool(root, "always_on_top", cfg.AlwaysOnTop);
                cfg.MouseClickThrough = GetBool(root, "mouse_click_through", cfg.MouseClickThrough);
                cfg.LockPosition = GetBool(root, "lock_position", cfg.LockPosition);
                cfg.ShowUserNameInDetails = GetBool(root, "show_user_name_in_details", cfg.ShowUserNameInDetails);
                cfg.ScrollShowFiveHour = GetBool(root, "scroll_show_5h", cfg.ScrollShowFiveHour);
                cfg.ScrollShowWeekly = GetBool(root, "scroll_show_weekly", cfg.ScrollShowWeekly);
                cfg.ScrollShowGpt55XHigh = GetBool(root, "scroll_show_gpt_55_xhigh", cfg.ScrollShowGpt55XHigh);
                cfg.ScrollShowGpt55High = GetBool(root, "scroll_show_gpt_55_high", cfg.ScrollShowGpt55High);
                cfg.ScrollShowGpt55Medium = GetBool(root, "scroll_show_gpt_55_medium", cfg.ScrollShowGpt55Medium);
                cfg.ScrollShowGpt54XHigh = GetBool(root, "scroll_show_gpt_54_xhigh", cfg.ScrollShowGpt54XHigh);
                cfg.EnvironmentCheckOnStartup = GetBool(root, "environment_check_on_startup", cfg.EnvironmentCheckOnStartup);
                cfg.EnvironmentConfirmMediumRiskOnManualRefresh = GetBool(root, "environment_confirm_medium_risk_on_manual_refresh", cfg.EnvironmentConfirmMediumRiskOnManualRefresh);
                cfg.EnvironmentRecheckHighRiskOnManualRefresh = GetBool(root, "environment_recheck_high_risk_on_manual_refresh", cfg.EnvironmentRecheckHighRiskOnManualRefresh);
                cfg.EnvironmentLastConfirmedIp = GetString(root, "environment_last_confirmed_ip", cfg.EnvironmentLastConfirmedIp);
                cfg.EnvironmentLastConfirmedCountryCode = GetString(root, "environment_last_confirmed_country_code", cfg.EnvironmentLastConfirmedCountryCode);
                cfg.EnvironmentLastConfirmedCountryName = GetString(root, "environment_last_confirmed_country_name", cfg.EnvironmentLastConfirmedCountryName);
                cfg.EnvironmentLastConfirmedRegion = GetString(root, "environment_last_confirmed_region", cfg.EnvironmentLastConfirmedRegion);
                cfg.EnvironmentLastConfirmedCity = GetString(root, "environment_last_confirmed_city", cfg.EnvironmentLastConfirmedCity);
                cfg.TrustedEnvironments = GetTrustedEnvironments(root);
                if (cfg.TrustedEnvironments.Count == 0 && !string.IsNullOrWhiteSpace(cfg.EnvironmentLastConfirmedIp))
                {
                    cfg.TrustedEnvironments.Add(new TrustedEnvironment {
                        Ip = cfg.EnvironmentLastConfirmedIp,
                        CountryCode = cfg.EnvironmentLastConfirmedCountryCode,
                        CountryName = cfg.EnvironmentLastConfirmedCountryName,
                        Region = cfg.EnvironmentLastConfirmedRegion,
                        City = cfg.EnvironmentLastConfirmedCity,
                        ConfirmedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture)
                    });
                }
                cfg.FloatingX = GetNullableInt(root, "floating_x");
                cfg.FloatingY = GetNullableInt(root, "floating_y");
            }
            catch
            {
                return cfg;
            }
            return cfg;
        }

        private void MigrateLegacyConfig()
        {
            try
            {
                if (File.Exists(this.configPath) || !File.Exists(this.legacyConfigPath)) return;
                Directory.CreateDirectory(this.configDir);
                File.Copy(this.legacyConfigPath, this.configPath, false);
            }
            catch
            {
            }
        }

        public void Save(MonitorConfig cfg)
        {
            Directory.CreateDirectory(this.configDir);
            var root = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(cfg.AccountIdDpapi) && !string.IsNullOrWhiteSpace(cfg.AccountId))
            {
                cfg.AccountIdDpapi = SecretBox.Protect(cfg.AccountId.Trim());
                cfg.AccountId = "";
            }
            root["access_token_dpapi"] = cfg.AccessTokenDpapi ?? "";
            root["account_id_dpapi"] = cfg.AccountIdDpapi ?? "";
            root["refresh_seconds"] = cfg.RefreshSeconds;
            root["reset_cards_endpoint"] = cfg.ResetCardsEndpoint ?? "";
            root["usage_endpoint"] = cfg.UsageEndpoint ?? "";
            root["originator"] = cfg.Originator ?? "";
            root["openai_beta"] = cfg.OpenAIBeta ?? "";
            root["theme"] = cfg.ThemeName ?? "Traffic Classic";
            root["language"] = string.IsNullOrWhiteSpace(cfg.Language) ? "zh-CN" : cfg.Language;
            root["auto_start"] = cfg.AutoStart;
            root["opacity_percent"] = cfg.OpacityPercent;
            root["always_on_top"] = cfg.AlwaysOnTop;
            root["mouse_click_through"] = cfg.MouseClickThrough;
            root["lock_position"] = cfg.LockPosition;
            root["show_user_name_in_details"] = cfg.ShowUserNameInDetails;
            root["scroll_show_5h"] = cfg.ScrollShowFiveHour;
            root["scroll_show_weekly"] = cfg.ScrollShowWeekly;
            root["scroll_show_gpt_55_xhigh"] = cfg.ScrollShowGpt55XHigh;
            root["scroll_show_gpt_55_high"] = cfg.ScrollShowGpt55High;
            root["scroll_show_gpt_55_medium"] = cfg.ScrollShowGpt55Medium;
            root["scroll_show_gpt_54_xhigh"] = cfg.ScrollShowGpt54XHigh;
            root["environment_check_on_startup"] = cfg.EnvironmentCheckOnStartup;
            root["environment_confirm_medium_risk_on_manual_refresh"] = cfg.EnvironmentConfirmMediumRiskOnManualRefresh;
            root["environment_recheck_high_risk_on_manual_refresh"] = cfg.EnvironmentRecheckHighRiskOnManualRefresh;
            var lastTrusted = LastTrustedEnvironment(cfg);
            root["environment_last_confirmed_ip"] = lastTrusted == null ? (cfg.EnvironmentLastConfirmedIp ?? "") : lastTrusted.Ip;
            root["environment_last_confirmed_country_code"] = lastTrusted == null ? (cfg.EnvironmentLastConfirmedCountryCode ?? "") : lastTrusted.CountryCode;
            root["environment_last_confirmed_country_name"] = lastTrusted == null ? (cfg.EnvironmentLastConfirmedCountryName ?? "") : lastTrusted.CountryName;
            root["environment_last_confirmed_region"] = lastTrusted == null ? (cfg.EnvironmentLastConfirmedRegion ?? "") : lastTrusted.Region;
            root["environment_last_confirmed_city"] = lastTrusted == null ? (cfg.EnvironmentLastConfirmedCity ?? "") : lastTrusted.City;
            root["environment_trusted_environments"] = TrustedEnvironmentDictionaries(cfg);
            if (cfg.FloatingX.HasValue) root["floating_x"] = cfg.FloatingX.Value;
            if (cfg.FloatingY.HasValue) root["floating_y"] = cfg.FloatingY.Value;
            File.WriteAllText(this.configPath, this.serializer.Serialize(root), Encoding.UTF8);
        }

        private static List<TrustedEnvironment> GetTrustedEnvironments(Dictionary<string, object> root)
        {
            var result = new List<TrustedEnvironment>();
            object value;
            if (!root.TryGetValue("environment_trusted_environments", out value) || value == null) return result;
            var items = value as IEnumerable;
            if (items == null || value is string) return result;
            foreach (var item in items)
            {
                var dict = item as Dictionary<string, object>;
                if (dict == null) continue;
                var trusted = new TrustedEnvironment {
                    Ip = GetString(dict, "ip", ""),
                    CountryCode = GetString(dict, "country_code", ""),
                    CountryName = GetString(dict, "country_name", ""),
                    Region = GetString(dict, "region", ""),
                    City = GetString(dict, "city", ""),
                    LocalizedCountryName = GetString(dict, "localized_country_name", ""),
                    LocalizedRegion = GetString(dict, "localized_region", ""),
                    LocalizedCity = GetString(dict, "localized_city", ""),
                    ConfirmedAt = GetString(dict, "confirmed_at", "")
                };
                if (!string.IsNullOrWhiteSpace(trusted.Ip)) result.Add(trusted);
            }
            return result;
        }

        private static List<Dictionary<string, object>> TrustedEnvironmentDictionaries(MonitorConfig cfg)
        {
            var result = new List<Dictionary<string, object>>();
            if (cfg == null || cfg.TrustedEnvironments == null) return result;
            foreach (var item in cfg.TrustedEnvironments)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Ip)) continue;
                result.Add(new Dictionary<string, object> {
                    { "ip", item.Ip ?? "" },
                    { "country_code", item.CountryCode ?? "" },
                    { "country_name", item.CountryName ?? "" },
                    { "region", item.Region ?? "" },
                    { "city", item.City ?? "" },
                    { "localized_country_name", item.LocalizedCountryName ?? "" },
                    { "localized_region", item.LocalizedRegion ?? "" },
                    { "localized_city", item.LocalizedCity ?? "" },
                    { "confirmed_at", item.ConfirmedAt ?? "" }
                });
            }
            return result;
        }

        private static TrustedEnvironment LastTrustedEnvironment(MonitorConfig cfg)
        {
            if (cfg == null || cfg.TrustedEnvironments == null || cfg.TrustedEnvironments.Count == 0) return null;
            return cfg.TrustedEnvironments[cfg.TrustedEnvironments.Count - 1];
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
            int parsed;
            if (!root.TryGetValue(key, out value) || value == null) return fallback;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        private static bool GetBool(Dictionary<string, object> root, string key, bool fallback)
        {
            object value;
            bool parsed;
            if (!root.TryGetValue(key, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        private static int? GetNullableInt(Dictionary<string, object> root, string key)
        {
            object value;
            int parsed;
            if (!root.TryGetValue(key, out value) || value == null) return null;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? (int?)parsed : null;
        }
    }

    internal enum EnvironmentRiskLevel
    {
        Safe,
        Medium,
        High
    }

    internal enum EnvironmentPromptChoice
    {
        Continue,
        Wait
    }

    internal sealed class EnvironmentGateResult
    {
        public readonly bool AllowQuery;
        public readonly MonitorSnapshot BlockedSnapshot;
        public readonly EnvironmentInfo EnvironmentToConfirmAfterSuccess;

        private EnvironmentGateResult(bool allowQuery, MonitorSnapshot blockedSnapshot, EnvironmentInfo environmentToConfirmAfterSuccess)
        {
            this.AllowQuery = allowQuery;
            this.BlockedSnapshot = blockedSnapshot;
            this.EnvironmentToConfirmAfterSuccess = environmentToConfirmAfterSuccess;
        }

        public static EnvironmentGateResult Allow()
        {
            return new EnvironmentGateResult(true, null, null);
        }

        public static EnvironmentGateResult AllowWithConfirmation(EnvironmentInfo info)
        {
            return new EnvironmentGateResult(true, null, info);
        }

        public static EnvironmentGateResult Block(MonitorSnapshot snapshot)
        {
            return new EnvironmentGateResult(false, snapshot, null);
        }
    }

    internal sealed class EnvironmentInfo
    {
        public string Ip = "";
        public string CountryCode = "";
        public string CountryName = "";
        public string Region = "";
        public string City = "";
        public string LocalizedCountryName = "";
        public string LocalizedRegion = "";
        public string LocalizedCity = "";

        public bool IsHighRiskCountry()
        {
            return string.Equals(this.CountryCode, "CN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(this.CountryCode, "HK", StringComparison.OrdinalIgnoreCase)
                || ContainsIgnoreCase(this.CountryName, "China")
                || ContainsIgnoreCase(this.CountryName, "Hong Kong");
        }

        public string DisplayText()
        {
            var text = "IP: " + EmptyAsDash(this.Ip);
            if (!this.HasUsableNetworkAddress())
            {
                return text + "\r\n" + T.Text("env_info_unavailable");
            }
            text += "\r\n" + T.Text("env_location_label") + ": " + EmptyAsDash(this.EnglishLocation());
            if (T.IsChinese)
            {
                var chinese = this.ChineseLocation();
                if (!string.IsNullOrWhiteSpace(chinese)) text += "\r\n" + chinese;
            }
            return text;
        }

        public bool HasUsableNetworkAddress()
        {
            return !string.IsNullOrWhiteSpace(this.Ip)
                && (!string.IsNullOrWhiteSpace(this.CountryName) || !string.IsNullOrWhiteSpace(this.CountryCode) || !string.IsNullOrWhiteSpace(this.Region) || !string.IsNullOrWhiteSpace(this.City));
        }

        public string EnglishLocation()
        {
            var location = string.Join(", ", new[] { this.City, this.Region, this.CountryName }.WhereNotBlank());
            return string.IsNullOrWhiteSpace(location) ? this.CountryCode : location;
        }

        public string ChineseLocation()
        {
            var parts = new List<string>();
            AddDistinct(parts, FirstNotBlank(this.LocalizedCity, ChineseCityName(this.CountryCode, this.City)));
            AddDistinct(parts, FirstNotBlank(this.LocalizedRegion, ChineseRegionName(this.CountryCode, this.Region)));
            AddDistinct(parts, FirstNotBlank(this.LocalizedCountryName, ChineseCountryName(this.CountryCode, this.CountryName)));
            return string.Join("，", parts.ToArray());
        }

        private static string FirstNotBlank(params string[] values)
        {
            if (values == null) return "";
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return "";
        }

        private static bool ContainsIgnoreCase(string value, string fragment)
        {
            return value != null && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
        }

        private static void AddDistinct(List<string> parts, string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) return;
            foreach (var part in parts)
            {
                if (string.Equals(part, value, StringComparison.OrdinalIgnoreCase)) return;
            }
            parts.Add(value);
        }

        private static string ChineseCountryName(string countryCode, string fallback)
        {
            switch ((countryCode ?? "").Trim().ToUpperInvariant())
            {
                case "US": return "美国";
                case "CN": return "中国";
                case "HK": return "中国香港";
                case "TW": return "中国台湾";
                case "JP": return "日本";
                case "SG": return "新加坡";
                case "MY": return "马来西亚";
                case "KR": return "韩国";
                case "GB": return "英国";
                case "CA": return "加拿大";
                case "AU": return "澳大利亚";
                case "DE": return "德国";
                case "FR": return "法国";
                case "NL": return "荷兰";
                case "SE": return "瑞典";
                case "CH": return "瑞士";
                case "IN": return "印度";
                case "BR": return "巴西";
                default: return fallback;
            }
        }

        private static string ChineseRegionName(string countryCode, string fallback)
        {
            var value = (fallback ?? "").Trim();
            if (value.Length == 0) return "";
            if (string.Equals(countryCode, "US", StringComparison.OrdinalIgnoreCase))
            {
                switch (value.ToLowerInvariant())
                {
                    case "california": return "加利福尼亚州";
                    case "new york": return "纽约州";
                    case "washington": return "华盛顿州";
                    case "oregon": return "俄勒冈州";
                    case "virginia": return "弗吉尼亚州";
                    case "texas": return "得克萨斯州";
                    case "illinois": return "伊利诺伊州";
                    case "arizona": return "亚利桑那州";
                    case "florida": return "佛罗里达州";
                    case "georgia": return "佐治亚州";
                    case "new jersey": return "新泽西州";
                    case "massachusetts": return "马萨诸塞州";
                    case "nevada": return "内华达州";
                }
            }
            if (string.Equals(countryCode, "CN", StringComparison.OrdinalIgnoreCase))
            {
                switch (value.ToLowerInvariant())
                {
                    case "guangdong": return "广东";
                    case "beijing": return "北京";
                    case "shanghai": return "上海";
                    case "zhejiang": return "浙江";
                    case "jiangsu": return "江苏";
                    case "sichuan": return "四川";
                    case "fujian": return "福建";
                    case "hubei": return "湖北";
                    case "hunan": return "湖南";
                    case "henan": return "河南";
                    case "shandong": return "山东";
                    case "hebei": return "河北";
                    case "liaoning": return "辽宁";
                    case "tianjin": return "天津";
                    case "chongqing": return "重庆";
                }
            }
            if (string.Equals(countryCode, "MY", StringComparison.OrdinalIgnoreCase))
            {
                switch (value.ToLowerInvariant())
                {
                    case "johor": return "柔佛州";
                    case "kuala lumpur": return "吉隆坡";
                    case "selangor": return "雪兰莪州";
                    case "penang": return "槟城州";
                    case "perak": return "霹雳州";
                    case "sabah": return "沙巴州";
                    case "sarawak": return "砂拉越州";
                    case "malacca": return "马六甲州";
                    case "melaka": return "马六甲州";
                }
            }
            return value;
        }

        private static string ChineseCityName(string countryCode, string fallback)
        {
            var value = (fallback ?? "").Trim();
            if (value.Length == 0) return "";
            switch (value.ToLowerInvariant())
            {
                case "san jose": return "圣何塞";
                case "san francisco": return "旧金山";
                case "los angeles": return "洛杉矶";
                case "seattle": return "西雅图";
                case "new york": return "纽约";
                case "chicago": return "芝加哥";
                case "dallas": return "达拉斯";
                case "ashburn": return "阿什本";
                case "phoenix": return "凤凰城";
                case "portland": return "波特兰";
                case "shenzhen": return "深圳";
                case "guangzhou": return "广州";
                case "beijing": return "北京";
                case "shanghai": return "上海";
                case "hangzhou": return "杭州";
                case "nanjing": return "南京";
                case "chengdu": return "成都";
                case "wuhan": return "武汉";
                case "tokyo": return "东京";
                case "osaka": return "大阪";
                case "singapore": return "新加坡";
                case "hong kong": return "香港";
                case "bukit batu": return "武吉峇都";
                case "kuala lumpur": return "吉隆坡";
                case "johor bahru": return "新山";
                case "malacca": return "马六甲";
                case "melaka": return "马六甲";
                default: return value;
            }
        }
    }

    internal sealed class TrustedEnvironment
    {
        public string Ip = "";
        public string CountryCode = "";
        public string CountryName = "";
        public string Region = "";
        public string City = "";
        public string LocalizedCountryName = "";
        public string LocalizedRegion = "";
        public string LocalizedCity = "";
        public string ConfirmedAt = "";

        public static TrustedEnvironment FromInfo(EnvironmentInfo info)
        {
            if (info == null) return new TrustedEnvironment();
            return new TrustedEnvironment {
                Ip = info.Ip ?? "",
                CountryCode = info.CountryCode ?? "",
                CountryName = info.CountryName ?? "",
                Region = info.Region ?? "",
                City = info.City ?? "",
                LocalizedCountryName = info.LocalizedCountryName ?? "",
                LocalizedRegion = info.LocalizedRegion ?? "",
                LocalizedCity = info.LocalizedCity ?? "",
                ConfirmedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        public TrustedEnvironment Clone()
        {
            return new TrustedEnvironment {
                Ip = this.Ip ?? "",
                CountryCode = this.CountryCode ?? "",
                CountryName = this.CountryName ?? "",
                Region = this.Region ?? "",
                City = this.City ?? "",
                LocalizedCountryName = this.LocalizedCountryName ?? "",
                LocalizedRegion = this.LocalizedRegion ?? "",
                LocalizedCity = this.LocalizedCity ?? "",
                ConfirmedAt = this.ConfirmedAt ?? ""
            };
        }

        public EnvironmentInfo ToInfo()
        {
            return new EnvironmentInfo {
                Ip = this.Ip ?? "",
                CountryCode = this.CountryCode ?? "",
                CountryName = this.CountryName ?? "",
                Region = this.Region ?? "",
                City = this.City ?? "",
                LocalizedCountryName = this.LocalizedCountryName ?? "",
                LocalizedRegion = this.LocalizedRegion ?? "",
                LocalizedCity = this.LocalizedCity ?? ""
            };
        }

        public bool Matches(EnvironmentInfo info)
        {
            if (info == null) return false;
            return Same(this.Ip, info.Ip)
                && Same(this.CountryCode, info.CountryCode)
                && Same(this.Region, info.Region)
                && Same(this.City, info.City);
        }

        public void UpdateFrom(EnvironmentInfo info)
        {
            var next = FromInfo(info);
            this.Ip = next.Ip;
            this.CountryCode = next.CountryCode;
            this.CountryName = next.CountryName;
            this.Region = next.Region;
            this.City = next.City;
            this.LocalizedCountryName = next.LocalizedCountryName;
            this.LocalizedRegion = next.LocalizedRegion;
            this.LocalizedCity = next.LocalizedCity;
            this.ConfirmedAt = next.ConfirmedAt;
        }

        public string ConfirmedAtText()
        {
            DateTime parsed;
            if (DateTime.TryParse(this.ConfirmedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            return string.IsNullOrWhiteSpace(this.ConfirmedAt) ? "--" : this.ConfirmedAt;
        }

        private static bool Same(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class EnvironmentSafetyClient
    {
        private const string Endpoint = "https://ipapi.co/json/";
        private const string ChineseLocationEndpoint = "http://ip-api.com/json/";

        public static EnvironmentInfo Fetch()
        {
            var root = ReadJson(Endpoint);
            var info = new EnvironmentInfo {
                Ip = GetString(root, "ip"),
                CountryCode = GetString(root, "country_code"),
                CountryName = GetString(root, "country_name"),
                Region = GetString(root, "region"),
                City = GetString(root, "city")
            };
            if (T.IsChinese) TryFetchChineseLocation(info);
            return info;
        }

        private static Dictionary<string, object> ReadJson(string endpoint)
        {
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = "CodexFloat/1.0";
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            request.KeepAlive = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var json = reader.ReadToEnd();
                var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) throw new InvalidOperationException("Environment API returned an unreadable response.");
                return root;
            }
        }

        private static void TryFetchChineseLocation(EnvironmentInfo info)
        {
            if (info == null || !info.HasUsableNetworkAddress()) return;
            try
            {
                var query = string.IsNullOrWhiteSpace(info.Ip) ? "" : Uri.EscapeDataString(info.Ip.Trim());
                var endpoint = ChineseLocationEndpoint + query + "?fields=status,message,query,country,countryCode,regionName,city&lang=zh-CN";
                var root = ReadJson(endpoint);
                var status = GetString(root, "status");
                if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)) return;

                var localizedCountryCode = GetString(root, "countryCode");
                if (!string.IsNullOrWhiteSpace(localizedCountryCode)
                    && !string.IsNullOrWhiteSpace(info.CountryCode)
                    && !string.Equals(localizedCountryCode, info.CountryCode, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                info.LocalizedCountryName = GetString(root, "country");
                info.LocalizedRegion = GetString(root, "regionName");
                info.LocalizedCity = GetString(root, "city");
            }
            catch
            {
                // Localization is display-only; keep the primary environment result if this public API is unavailable.
            }
        }

        private static string GetString(Dictionary<string, object> root, string key)
        {
            object value;
            if (!root.TryGetValue(key, out value) || value == null) return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }
    }

    internal static class StringEnumerableExtensions
    {
        public static IEnumerable<string> WhereNotBlank(this IEnumerable<string> values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) yield return value.Trim();
            }
        }
    }

    internal static class EnvironmentRiskVisual
    {
        public static Color Accent(EnvironmentRiskLevel level)
        {
            switch (level)
            {
                case EnvironmentRiskLevel.Safe: return Color.FromArgb(22, 163, 74);
                case EnvironmentRiskLevel.High: return Color.FromArgb(220, 38, 38);
                default: return Color.FromArgb(217, 119, 6);
            }
        }

        public static Color Soft(EnvironmentRiskLevel level)
        {
            switch (level)
            {
                case EnvironmentRiskLevel.Safe: return Color.FromArgb(232, 248, 239);
                case EnvironmentRiskLevel.High: return Color.FromArgb(254, 232, 232);
                default: return Color.FromArgb(255, 246, 224);
            }
        }

        public static string Badge(EnvironmentRiskLevel level)
        {
            switch (level)
            {
                case EnvironmentRiskLevel.Safe: return T.Text("env_badge_safe");
                case EnvironmentRiskLevel.High: return T.Text("env_badge_high");
                default: return T.Text("env_badge_medium");
            }
        }
    }

    internal sealed class EnvironmentStatusPopupForm : Form
    {
        private readonly EnvironmentRiskLevel riskLevel;
        private readonly string title;
        private readonly string bodyText;
        private readonly EnvironmentInfo info;
        private readonly WinFormsTimer closeTimer = new WinFormsTimer();
        private readonly Button confirmButton = new Button();
        private int secondsRemaining;

        public EnvironmentStatusPopupForm(EnvironmentRiskLevel riskLevel, string title, string message, EnvironmentInfo info)
        {
            this.riskLevel = riskLevel;
            this.title = title ?? "";
            this.bodyText = message ?? "";
            this.info = info;
            this.secondsRemaining = riskLevel == EnvironmentRiskLevel.High ? 9 : 6;
            this.Size = new Size(440, 286);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.DoubleBuffered = true;
            this.BackColor = Color.White;
            this.closeTimer.Interval = 1000;
            this.closeTimer.Tick += delegate {
                this.secondsRemaining--;
                this.UpdateConfirmButtonText();
                if (this.secondsRemaining <= 0)
                {
                    this.closeTimer.Stop();
                    if (!this.IsDisposed) this.Close();
                }
            };
            ConfigureConfirmButton(this.confirmButton);
            this.confirmButton.Click += delegate { this.Close(); };
            this.Controls.Add(this.confirmButton);
            this.UpdateConfirmButtonText();
        }

        public void PlaceBottomRight(Rectangle workingArea)
        {
            this.Location = new Point(workingArea.Right - this.Width - 18, workingArea.Bottom - this.Height - 18);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            this.closeTimer.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            this.closeTimer.Stop();
            this.closeTimer.Dispose();
            base.OnClosed(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            this.Close();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (this.Width <= 0 || this.Height <= 0) return;
            using (var path = MiniBarForm.RoundRect(new Rectangle(0, 0, this.Width, this.Height), 14))
            {
                this.Region = new Region(path);
            }
            this.confirmButton.Bounds = new Rectangle((this.ClientSize.Width - 156) / 2, this.ClientSize.Height - 50, 156, 34);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var accent = EnvironmentRiskVisual.Accent(this.riskLevel);
            using (var bg = new SolidBrush(Color.FromArgb(250, 252, 255)))
            using (var border = new Pen(Color.FromArgb(214, 226, 238)))
            using (var path = MiniBarForm.RoundRect(new Rectangle(0, 0, this.Width - 1, this.Height - 1), 14))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }
            using (var accentBrush = new SolidBrush(accent))
            {
                g.FillRectangle(accentBrush, 0, 0, 7, this.Height);
                g.FillEllipse(accentBrush, 23, 25, 14, 14);
            }
            DrawBadge(g, new Rectangle(this.Width - 96, 20, 70, 26), this.riskLevel);
            using (var titleFont = UiFont(10.8f, FontStyle.Bold))
            using (var bodyFont = UiFont(9.4f, FontStyle.Regular))
            {
                TextRenderer.DrawText(g, this.title, titleFont, new Rectangle(48, 17, this.Width - 156, 30), Color.FromArgb(15, 23, 42), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                var bodyRect = new Rectangle(28, 66, this.Width - 56, 72);
                var bodyHeight = MeasureWrappedTextHeight(this.bodyText, bodyFont, bodyRect.Width, bodyRect.Height);
                TextRenderer.DrawText(g, this.bodyText, bodyFont, new Rectangle(bodyRect.Left, bodyRect.Top, bodyRect.Width, bodyHeight + 4), Color.FromArgb(51, 65, 85), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

                var statusInfoHeight = 82;
                var statusInfoTop = CenteredTopBetween(bodyRect.Top + bodyHeight, this.confirmButton.Top, statusInfoHeight, 8);
                DrawEnvironmentInfoBlock(g, new Rectangle(28, statusInfoTop, this.Width - 56, statusInfoHeight), this.info, this.riskLevel, Color.FromArgb(39, 54, 72));
            }
        }

        internal static void DrawBadge(Graphics g, Rectangle bounds, EnvironmentRiskLevel level)
        {
            var accent = EnvironmentRiskVisual.Accent(level);
            using (var path = MiniBarForm.RoundRect(bounds, 13))
            using (var fill = new SolidBrush(EnvironmentRiskVisual.Soft(level)))
            using (var border = new Pen(Color.FromArgb(150, accent)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using (var badgeFont = UiFont(8.6f, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, EnvironmentRiskVisual.Badge(level), badgeFont, bounds, accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private void UpdateConfirmButtonText()
        {
            this.confirmButton.Text = T.Text("env_popup_confirm") + " (" + Math.Max(0, this.secondsRemaining).ToString(CultureInfo.InvariantCulture) + "s)";
        }

        internal static void DrawEnvironmentInfoBlock(Graphics g, Rectangle infoRect, EnvironmentInfo info, EnvironmentRiskLevel level, Color textColor)
        {
            var accent = EnvironmentRiskVisual.Accent(level);
            using (var path = MiniBarForm.RoundRect(infoRect, 9))
            using (var fill = new SolidBrush(EnvironmentRiskVisual.Soft(level)))
            using (var pen = new Pen(Color.FromArgb(120, accent)))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            var labelFont = UiFont(9.2f, FontStyle.Bold);
            var valueFont = UiFont(9.2f, FontStyle.Regular);
            var labelColor = Color.FromArgb(88, textColor);
            var left = infoRect.Left + 12;
            var labelWidth = T.IsChinese ? 54 : 68;
            var valueLeft = left + labelWidth;
            var lineHeight = 20;
            var lineGap = 4;

            if (info == null || !info.HasUsableNetworkAddress())
            {
                var contentHeight = lineHeight * 2 + lineGap;
                var emptyTop = infoRect.Top + Math.Max(0, (infoRect.Height - contentHeight) / 2);
                TextRenderer.DrawText(g, "IP:", labelFont, new Rectangle(left, emptyTop, labelWidth, lineHeight), labelColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "--", valueFont, new Rectangle(valueLeft, emptyTop, infoRect.Right - valueLeft - 12, lineHeight), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                using (var explainFont = UiFont(9.2f, FontStyle.Regular))
                {
                    TextRenderer.DrawText(g, T.Text("env_info_unavailable"), explainFont, new Rectangle(valueLeft, emptyTop + lineHeight + lineGap, infoRect.Right - valueLeft - 12, lineHeight), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                labelFont.Dispose();
                valueFont.Dispose();
                return;
            }

            var chinese = T.IsChinese ? info.ChineseLocation() : "";
            var lineCount = T.IsChinese && !string.IsNullOrWhiteSpace(chinese) ? 3 : 2;
            var totalHeight = lineHeight * lineCount + lineGap * (lineCount - 1);
            var top = infoRect.Top + Math.Max(0, (infoRect.Height - totalHeight) / 2);
            TextRenderer.DrawText(g, "IP:", labelFont, new Rectangle(left, top, labelWidth, lineHeight), labelColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, info.Ip, valueFont, new Rectangle(valueLeft, top, infoRect.Right - valueLeft - 12, lineHeight), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var locationTop = top + lineHeight + lineGap;
            TextRenderer.DrawText(g, T.Text("env_location_label") + ":", labelFont, new Rectangle(left, locationTop, labelWidth, lineHeight), labelColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, info.EnglishLocation(), valueFont, new Rectangle(valueLeft, locationTop, infoRect.Right - valueLeft - 12, lineHeight), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (T.IsChinese && !string.IsNullOrWhiteSpace(chinese))
            {
                TextRenderer.DrawText(g, chinese, valueFont, new Rectangle(valueLeft, locationTop + lineHeight + lineGap, infoRect.Right - valueLeft - 12, lineHeight), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            labelFont.Dispose();
            valueFont.Dispose();
        }

        internal static int MeasureWrappedTextHeight(string text, Font font, int width, int maxHeight)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var measured = TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            return Math.Max(18, Math.Min(maxHeight, measured.Height));
        }

        internal static int CenteredTopBetween(int upperContentBottom, int lowerContentTop, int blockHeight, int margin)
        {
            var start = upperContentBottom + margin;
            var end = lowerContentTop - margin;
            if (end <= start + blockHeight) return start;
            return start + (end - start - blockHeight) / 2;
        }

        internal static Font UiFont(float size, FontStyle style)
        {
            return new Font(T.IsChinese ? "Microsoft YaHei UI" : "Segoe UI", size, style);
        }

        private static void ConfigureConfirmButton(Button button)
        {
            button.Font = UiFont(9.0f, FontStyle.Regular);
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.UseCompatibleTextRendering = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(30, 41, 59);
            button.UseVisualStyleBackColor = false;
        }
    }

    internal sealed class EnvironmentInitialTrustPromptForm : Form
    {
        public EnvironmentPromptChoice Choice = EnvironmentPromptChoice.Wait;
        private readonly EnvironmentInfo info;
        private readonly Button trustButton = new Button();
        private readonly Button waitButton = new Button();

        public EnvironmentInitialTrustPromptForm(EnvironmentInfo info)
        {
            this.info = info;
            this.Text = T.Text("env_safety_title");
            this.Size = new Size(600, 338);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.BackColor = Color.White;
            this.DoubleBuffered = true;

            ConfigureButton(this.trustButton, T.Text("env_initial_trust_continue"), EnvironmentRiskVisual.Accent(EnvironmentRiskLevel.Safe), Color.White, EnvironmentRiskVisual.Accent(EnvironmentRiskLevel.Safe));
            this.trustButton.Click += delegate {
                this.Choice = EnvironmentPromptChoice.Continue;
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            ConfigureButton(this.waitButton, T.Text("env_initial_trust_wait"), Color.White, Color.FromArgb(51, 65, 85), Color.FromArgb(203, 213, 225));
            this.waitButton.Click += delegate {
                this.Choice = EnvironmentPromptChoice.Wait;
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };
            this.Controls.Add(this.trustButton);
            this.Controls.Add(this.waitButton);

            this.AcceptButton = this.trustButton;
            this.CancelButton = this.waitButton;
        }

        public void PlaceBottomRight(Rectangle workingArea)
        {
            this.Location = new Point(workingArea.Right - this.Width - 18, workingArea.Bottom - this.Height - 18);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (this.Width <= 0 || this.Height <= 0) return;
            using (var path = MiniBarForm.RoundRect(new Rectangle(0, 0, this.Width, this.Height), 16))
            {
                this.Region = new Region(path);
            }

            const int buttonWidth = 238;
            const int buttonHeight = 50;
            const int gap = 16;
            var total = buttonWidth * 2 + gap;
            var x = (this.ClientSize.Width - total) / 2;
            var y = this.ClientSize.Height - 76;
            this.trustButton.Bounds = new Rectangle(x, y, buttonWidth, buttonHeight);
            this.waitButton.Bounds = new Rectangle(x + buttonWidth + gap, y, buttonWidth, buttonHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var accent = EnvironmentRiskVisual.Accent(EnvironmentRiskLevel.Safe);
            using (var bg = new SolidBrush(Color.FromArgb(250, 252, 255)))
            using (var border = new Pen(Color.FromArgb(213, 225, 236)))
            using (var path = MiniBarForm.RoundRect(new Rectangle(0, 0, this.Width - 1, this.Height - 1), 16))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }
            using (var accentBrush = new SolidBrush(accent))
            {
                g.FillRectangle(accentBrush, 0, 0, 8, this.Height);
                g.FillEllipse(accentBrush, 26, 29, 16, 16);
            }
            EnvironmentStatusPopupForm.DrawBadge(g, new Rectangle(this.Width - 110, 24, 78, 28), EnvironmentRiskLevel.Safe);
            using (var titleFont = EnvironmentStatusPopupForm.UiFont(10.8f, FontStyle.Bold))
            using (var bodyFont = EnvironmentStatusPopupForm.UiFont(9.4f, FontStyle.Regular))
            {
                TextRenderer.DrawText(g, T.Text("env_initial_trust_title"), titleFont, new Rectangle(54, 19, this.Width - 180, 38), Color.FromArgb(15, 23, 42), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                var bodyRect = new Rectangle(28, 72, this.Width - 56, 78);
                var bodyText = T.Text("env_initial_trust_message");
                var bodyHeight = EnvironmentStatusPopupForm.MeasureWrappedTextHeight(bodyText, bodyFont, bodyRect.Width, bodyRect.Height);
                TextRenderer.DrawText(g, bodyText, bodyFont, new Rectangle(bodyRect.Left, bodyRect.Top, bodyRect.Width, bodyHeight + 4), Color.FromArgb(51, 65, 85), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

                var promptInfoHeight = 90;
                var promptInfoTop = EnvironmentStatusPopupForm.CenteredTopBetween(bodyRect.Top + bodyHeight, this.trustButton.Top, promptInfoHeight, 10);
                EnvironmentStatusPopupForm.DrawEnvironmentInfoBlock(g, new Rectangle(28, promptInfoTop, this.Width - 56, promptInfoHeight), this.info, EnvironmentRiskLevel.Safe, Color.FromArgb(20, 83, 45));
            }
        }

        private static void ConfigureButton(Button button, string text, Color backColor, Color foreColor, Color borderColor)
        {
            button.Text = text;
            button.Font = EnvironmentStatusPopupForm.UiFont(9.0f, FontStyle.Regular);
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.UseCompatibleTextRendering = false;
            button.Padding = new Padding(6, 0, 6, 1);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = borderColor;
            button.BackColor = backColor;
            button.ForeColor = foreColor;
            button.UseVisualStyleBackColor = false;
        }
    }

    internal sealed class EnvironmentRiskPromptForm : Form
    {
        public EnvironmentPromptChoice Choice = EnvironmentPromptChoice.Wait;
        private readonly EnvironmentInfo info;
        private readonly string message;
        private readonly Button continueButton = new Button();
        private readonly Button waitButton = new Button();

        public EnvironmentRiskPromptForm(EnvironmentInfo info, string message)
        {
            this.info = info;
            this.message = message ?? "";
            this.Text = T.Text("env_safety_title");
            this.Size = new Size(600, 348);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.BackColor = Color.White;
            this.DoubleBuffered = true;

            ConfigureButton(this.continueButton, T.Text("env_confirm_continue"), EnvironmentRiskVisual.Accent(EnvironmentRiskLevel.Medium), Color.White, EnvironmentRiskVisual.Accent(EnvironmentRiskLevel.Medium));
            this.continueButton.Click += delegate {
                this.Choice = EnvironmentPromptChoice.Continue;
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            ConfigureButton(this.waitButton, T.Text("env_wait_manual"), Color.White, Color.FromArgb(51, 65, 85), Color.FromArgb(203, 213, 225));
            this.waitButton.Click += delegate {
                this.Choice = EnvironmentPromptChoice.Wait;
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };
            this.Controls.Add(this.continueButton);
            this.Controls.Add(this.waitButton);

            this.AcceptButton = this.continueButton;
            this.CancelButton = this.waitButton;
        }

        public void PlaceBottomRight(Rectangle workingArea)
        {
            this.Location = new Point(workingArea.Right - this.Width - 18, workingArea.Bottom - this.Height - 18);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (this.Width <= 0 || this.Height <= 0) return;
            using (var path = MiniBarForm.RoundRect(new Rectangle(0, 0, this.Width, this.Height), 16))
            {
                this.Region = new Region(path);
            }

            const int buttonWidth = 238;
            const int buttonHeight = 50;
            const int gap = 16;
            var total = buttonWidth * 2 + gap;
            var x = (this.ClientSize.Width - total) / 2;
            var y = this.ClientSize.Height - 76;
            this.continueButton.Bounds = new Rectangle(x, y, buttonWidth, buttonHeight);
            this.waitButton.Bounds = new Rectangle(x + buttonWidth + gap, y, buttonWidth, buttonHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var accent = EnvironmentRiskVisual.Accent(EnvironmentRiskLevel.Medium);
            using (var bg = new SolidBrush(Color.FromArgb(250, 252, 255)))
            using (var border = new Pen(Color.FromArgb(213, 225, 236)))
            using (var path = MiniBarForm.RoundRect(new Rectangle(0, 0, this.Width - 1, this.Height - 1), 16))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }
            using (var accentBrush = new SolidBrush(accent))
            {
                g.FillRectangle(accentBrush, 0, 0, 8, this.Height);
                g.FillEllipse(accentBrush, 26, 29, 16, 16);
            }
            EnvironmentStatusPopupForm.DrawBadge(g, new Rectangle(this.Width - 110, 24, 78, 28), EnvironmentRiskLevel.Medium);
            using (var titleFont = EnvironmentStatusPopupForm.UiFont(10.8f, FontStyle.Bold))
            using (var bodyFont = EnvironmentStatusPopupForm.UiFont(9.4f, FontStyle.Regular))
            {
                TextRenderer.DrawText(g, T.Text("env_medium_risk_title"), titleFont, new Rectangle(54, 19, this.Width - 180, 38), Color.FromArgb(15, 23, 42), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                var bodyRect = new Rectangle(28, 74, this.Width - 56, 82);
                var bodyHeight = EnvironmentStatusPopupForm.MeasureWrappedTextHeight(this.message, bodyFont, bodyRect.Width, bodyRect.Height);
                TextRenderer.DrawText(g, this.message, bodyFont, new Rectangle(bodyRect.Left, bodyRect.Top, bodyRect.Width, bodyHeight + 4), Color.FromArgb(51, 65, 85), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

                var promptInfoHeight = 90;
                var promptInfoTop = EnvironmentStatusPopupForm.CenteredTopBetween(bodyRect.Top + bodyHeight, this.continueButton.Top, promptInfoHeight, 10);
                EnvironmentStatusPopupForm.DrawEnvironmentInfoBlock(g, new Rectangle(28, promptInfoTop, this.Width - 56, promptInfoHeight), this.info, EnvironmentRiskLevel.Medium, Color.FromArgb(92, 62, 10));
            }
        }

        private static void ConfigureButton(Button button, string text, Color backColor, Color foreColor, Color borderColor)
        {
            button.Text = text;
            button.Font = EnvironmentStatusPopupForm.UiFont(9.0f, FontStyle.Regular);
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.UseCompatibleTextRendering = false;
            button.Padding = new Padding(6, 0, 6, 1);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = borderColor;
            button.BackColor = backColor;
            button.ForeColor = foreColor;
            button.UseVisualStyleBackColor = false;
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
            if ((hex.Length % 2) != 0) throw new FormatException("Invalid DPAPI payload.");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
    }

    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CodexFloat";
        private const string LegacyValueName = "CodexResetMonitor";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    var value = key == null ? null : key.GetValue(ValueName) as string;
                    var legacyValue = key == null ? null : key.GetValue(LegacyValueName) as string;
                    return !string.IsNullOrWhiteSpace(value) || !string.IsNullOrWhiteSpace(legacyValue);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) return;
                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    key.DeleteValue(LegacyValueName, false);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                    key.DeleteValue(LegacyValueName, false);
                }
            }
        }
    }

    internal static class AppLinks
    {
        public const string AuthorGithub = "https://github.com/RayOhmie";
        public const string AuthorEmail = "RayOhmie@gmail.com";
        public const string AuthorMailto = "mailto:RayOhmie@gmail.com";
        public const string ProjectGithub = "https://github.com/RayOhmie/CodexFloat";
        public const string Releases = "https://github.com/RayOhmie/CodexFloat/releases";
        public const string CodexRadar = "https://codexradar.com/";
        public const string CodexRadarEnglish = "https://codexradar.com/en/";
    }

    internal static class LinkOpener
    {
        public static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(T.Text("无法打开链接") + ": " + ex.Message, "CodexFloat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    internal sealed class ImportedCredentials
    {
        public string AccessToken = "";
        public string AccountId = "";
        public string SourcePath = "";

        public bool HasBoth()
        {
            return !string.IsNullOrWhiteSpace(this.AccessToken) && !string.IsNullOrWhiteSpace(this.AccountId);
        }
    }

    internal static class CredentialImporter
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };

        public static bool TryFind(out ImportedCredentials result, out string message)
        {
            result = null;
            message = T.Text("未找到凭据");
            var state = new ImportScanState();
            foreach (var root in CandidateRoots())
            {
                foreach (var path in EnumerateJsonFiles(root, 0, 6, state))
                {
                    ImportedCredentials found;
                    if (TryReadFile(path, out found) && found.HasBoth())
                    {
                        result = found;
                        message = T.Text("找到凭据") + ": " + path;
                        return true;
                    }
                    if (state.FilesChecked >= 900)
                    {
                        message = T.Text("扫描过多");
                        return false;
                    }
                }
            }
            return false;
        }

        private static IEnumerable<string> CandidateRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            foreach (var path in AddBasePaths(appData, localData, user))
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && seen.Add(path)) yield return path;
            }

            var packages = Path.Combine(localData, "Packages");
            if (Directory.Exists(packages))
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(packages); }
                catch { dirs = new string[0]; }
                foreach (var dir in dirs)
                {
                    var name = Path.GetFileName(dir).ToLowerInvariant();
                    if ((name.Contains("openai") || name.Contains("chatgpt") || name.Contains("codex")) && seen.Add(dir)) yield return dir;
                }
            }
        }

        private static IEnumerable<string> AddBasePaths(string appData, string localData, string user)
        {
            yield return Path.Combine(appData, "Codex");
            yield return Path.Combine(appData, "OpenAI");
            yield return Path.Combine(appData, "ChatGPT");
            yield return Path.Combine(appData, "ChatGPT Desktop");
            yield return Path.Combine(localData, "Codex");
            yield return Path.Combine(localData, "OpenAI");
            yield return Path.Combine(localData, "ChatGPT");
            yield return Path.Combine(localData, "ChatGPT Desktop");
            yield return Path.Combine(user, ".codex");
            yield return Path.Combine(user, ".config", "codex");
        }

        private sealed class ImportScanState
        {
            public int FilesChecked;
        }

        private static IEnumerable<string> EnumerateJsonFiles(string dir, int depth, int maxDepth, ImportScanState state)
        {
            if (depth > maxDepth || state.FilesChecked >= 900) yield break;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); }
            catch { files = new string[0]; }

            foreach (var file in files)
            {
                state.FilesChecked++;
                yield return file;
                if (state.FilesChecked >= 900) yield break;
            }

            if (depth >= maxDepth || state.FilesChecked >= 900) yield break;

            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch { dirs = new string[0]; }

            foreach (var child in dirs)
            {
                foreach (var file in EnumerateJsonFiles(child, depth + 1, maxDepth, state)) yield return file;
                if (state.FilesChecked >= 900) yield break;
            }
        }

        private static bool TryReadFile(string path, out ImportedCredentials result)
        {
            result = new ImportedCredentials { SourcePath = path };
            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > 2 * 1024 * 1024) return false;
                var json = File.ReadAllText(path, Encoding.UTF8);
                var root = Serializer.DeserializeObject(json);
                Walk(root, "", result);
                return result.HasBoth();
            }
            catch
            {
                return false;
            }
        }

        private static void Walk(object value, string key, ImportedCredentials result)
        {
            if (value == null || result.HasBoth()) return;

            var text = value as string;
            if (text != null)
            {
                var normalized = NormalizeValue(text);
                if (string.IsNullOrWhiteSpace(result.AccessToken) && IsAccessTokenKey(key) && LooksLikeToken(normalized)) result.AccessToken = normalized;
                if (string.IsNullOrWhiteSpace(result.AccountId) && IsAccountIdKey(key) && LooksLikeAccountId(normalized)) result.AccountId = normalized;
                return;
            }

            var dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (var kv in dict)
                {
                    Walk(kv.Value, kv.Key, result);
                    if (result.HasBoth()) return;
                }
                return;
            }

            var arr = value as object[];
            if (arr != null)
            {
                foreach (var item in arr)
                {
                    Walk(item, key, result);
                    if (result.HasBoth()) return;
                }
            }
        }

        private static bool IsAccessTokenKey(string key)
        {
            var k = NormalizeKey(key);
            if (k.Contains("refresh")) return false;
            return k == "token" || k == "accesstoken" || k.Contains("authorization") || (k.Contains("access") && k.Contains("token")) || (k.Contains("auth") && k.Contains("token")) || k.Contains("bearertoken");
        }

        private static bool IsAccountIdKey(string key)
        {
            var k = NormalizeKey(key);
            return k == "accountid" || k == "chatgptaccountid" || k == "currentaccountid" || (k.Contains("account") && k.Contains("id"));
        }

        private static string NormalizeKey(string key)
        {
            if (key == null) return "";
            var sb = new StringBuilder();
            foreach (var ch in key.ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string NormalizeValue(string text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7).Trim();
            return text;
        }

        private static bool LooksLikeToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 30 || text.Length > 8000) return false;
            return text.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0;
        }

        private static bool LooksLikeAccountId(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 3 || text.Length > 240) return false;
            if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
            return text.IndexOfAny(new[] { '\r', '\n', '\t', ' ' }) < 0;
        }
    }

    internal static class CodexClient
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        private const string CodexRadarCurrentJson = "https://codexradar.com/current.json";

        public static MonitorSnapshot Fetch(MonitorConfig config)
        {
            var token = SecretBox.Unprotect(config.AccessTokenDpapi);
            var accountId = config.GetAccountId();
            var usageJson = Get(config.UsageEndpoint, config, token);
            var resetJson = Get(config.ResetCardsEndpoint, config, token, accountId);
            var usageRoot = Serializer.DeserializeObject(usageJson) as Dictionary<string, object>;
            var resetRoot = Serializer.DeserializeObject(resetJson) as Dictionary<string, object>;
            return new MonitorSnapshot(UsageInfo.FromJson(usageRoot), ResetCardInfo.FromJson(resetRoot), DateTime.Now, null) { ModelIq = FetchModelIq() };
        }

        private static ModelIqRadar FetchModelIq()
        {
            try
            {
                var json = GetPublic(CodexRadarCurrentJson);
                var root = Serializer.DeserializeObject(json) as Dictionary<string, object>;
                var radar = ModelIqRadar.FromJson(root);
                if (!radar.HasAnyScore())
                {
                    radar.ErrorCode = "CODEX_RADAR_EMPTY";
                    radar.ErrorMessage = "CodexRadar IQ response did not contain usable scores.";
                    ErrorLogStore.Write(radar.ErrorCode, radar.ErrorMessage, "Endpoint: " + CodexRadarCurrentJson + Environment.NewLine + "Response preview: " + Preview(json));
                }
                return radar;
            }
            catch (Exception ex)
            {
                var radar = ModelIqRadar.Empty();
                radar.ErrorCode = "CODEX_RADAR_UNAVAILABLE";
                radar.ErrorMessage = "CodexRadar IQ request failed.";
                ErrorLogStore.Write(radar.ErrorCode, radar.ErrorMessage, "Endpoint: " + CodexRadarCurrentJson + Environment.NewLine + ex);
                return radar;
            }
        }

        private static string GetPublic(string uri)
        {
            WebException lastTransient = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    return GetPublicOnce(uri);
                }
                catch (WebException ex)
                {
                    if (!IsTransientConnectionError(ex) || attempt == 2) throw;
                    lastTransient = ex;
                    Thread.Sleep(350 * (attempt + 1));
                    try { ServicePointManager.FindServicePoint(new Uri(uri)).CloseConnectionGroup(""); }
                    catch { }
                }
            }
            throw lastTransient;
        }

        private static string GetPublicOnce(string uri)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.KeepAlive = false;
            request.Accept = "application/json";
            request.UserAgent = "CodexFloat";
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static string Preview(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= 1200 ? value : value.Substring(0, 1200);
        }

        private static string Get(string uri, MonitorConfig config, string token)
        {
            return Get(uri, config, token, config.GetAccountId());
        }

        private static string Get(string uri, MonitorConfig config, string token, string accountId)
        {
            WebException lastTransient = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    return GetOnce(uri, config, token, accountId);
                }
                catch (WebException ex)
                {
                    if (!IsTransientConnectionError(ex) || attempt == 2) throw;
                    lastTransient = ex;
                    Thread.Sleep(350 * (attempt + 1));
                    try { ServicePointManager.FindServicePoint(new Uri(uri)).CloseConnectionGroup(""); }
                    catch { }
                }
            }
            throw lastTransient;
        }

        private static string GetOnce(string uri, MonitorConfig config, string token, string accountId)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.KeepAlive = false;
            request.Accept = "application/json";
            request.UserAgent = "CodexFloat";
            request.Headers["Authorization"] = "Bearer " + token;
            if (!string.IsNullOrWhiteSpace(config.OpenAIBeta)) request.Headers["OpenAI-Beta"] = config.OpenAIBeta;
            if (!string.IsNullOrWhiteSpace(config.Originator)) request.Headers["originator"] = config.Originator;
            if (!string.IsNullOrWhiteSpace(accountId)) request.Headers["ChatGPT-Account-ID"] = accountId;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        internal static bool IsTransientConnectionError(WebException ex)
        {
            if (ex == null || ex.Response != null) return false;
            if (ex.Status == WebExceptionStatus.Timeout ||
                ex.Status == WebExceptionStatus.ConnectionClosed ||
                ex.Status == WebExceptionStatus.KeepAliveFailure ||
                ex.Status == WebExceptionStatus.SendFailure ||
                ex.Status == WebExceptionStatus.ReceiveFailure)
            {
                return true;
            }
            var message = ex.Message ?? "";
            return message.IndexOf("underlying connection was closed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("unexpected error occurred on a send", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class ErrorAdvisor
    {
        public static MonitorSnapshot MissingCredentials()
        {
            return MonitorSnapshot.Error("CREDENTIALS_MISSING", T.Text("error_missing_credentials_summary"), T.Text("error_missing_credentials_detail"));
        }

        public static MonitorSnapshot FromException(Exception ex)
        {
            string summary;
            string detail;

            var crypto = ex as CryptographicException;
            if (crypto != null)
            {
                summary = T.Text("error_credential_config_summary");
                detail = T.Text("error_credential_config_detail");
                return MonitorSnapshot.Error("CREDENTIALS_DECRYPT_FAILED", summary, WithTechnicalDetail(detail, ex));
            }

            var web = ex as WebException;
            if (web != null)
            {
                return FromWebException(web);
            }

            if (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException)
            {
                summary = T.Text("error_api_changed_summary");
                detail = T.Text("error_api_changed_detail");
                return MonitorSnapshot.Error("API_CHANGED", summary, WithTechnicalDetail(detail, ex));
            }

            summary = T.Text("error_unknown_summary");
            detail = T.Text("error_unknown_detail");
            return MonitorSnapshot.Error("UNKNOWN_ERROR", summary, WithTechnicalDetail(detail, ex));
        }

        private static MonitorSnapshot FromWebException(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                var code = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return MonitorSnapshot.Error("AUTH_EXPIRED", T.Text("error_auth_summary"), WithTechnicalDetail(T.Text("error_auth_detail"), ex));
                }
                if (code == 404)
                {
                    return MonitorSnapshot.Error("API_CHANGED", T.Text("error_api_changed_summary"), WithTechnicalDetail(T.Text("error_api_changed_detail"), ex));
                }
                if (code >= 500)
                {
                    return MonitorSnapshot.Error("SERVER_UNAVAILABLE", T.Text("error_server_summary"), WithTechnicalDetail(T.Text("error_server_detail"), ex));
                }
                return MonitorSnapshot.Error("HTTP_" + code.ToString(CultureInfo.InvariantCulture), T.Text("error_http_summary"), WithTechnicalDetail(T.Text("error_http_detail") + " HTTP " + code.ToString(CultureInfo.InvariantCulture), ex));
            }

            if (ex.Status == WebExceptionStatus.Timeout)
            {
                return MonitorSnapshot.Error("REQUEST_TIMEOUT", T.Text("error_timeout_summary"), WithTechnicalDetail(T.Text("error_timeout_detail"), ex));
            }

            if (ex.Status == WebExceptionStatus.NameResolutionFailure ||
                ex.Status == WebExceptionStatus.ConnectFailure ||
                ex.Status == WebExceptionStatus.ProxyNameResolutionFailure ||
                ex.Status == WebExceptionStatus.SecureChannelFailure ||
                ex.Status == WebExceptionStatus.TrustFailure ||
                ex.Status == WebExceptionStatus.ReceiveFailure ||
                ex.Status == WebExceptionStatus.SendFailure)
            {
                var interrupted = CodexClient.IsTransientConnectionError(ex);
                return MonitorSnapshot.Error(interrupted ? "CONNECTION_INTERRUPTED" : "NETWORK_UNAVAILABLE", interrupted ? T.Text("error_connection_interrupted_summary") : T.Text("error_network_summary"), WithTechnicalDetail(interrupted ? T.Text("error_connection_interrupted_detail") : T.Text("error_network_detail"), ex));
            }

            return MonitorSnapshot.Error("UNKNOWN_ERROR", T.Text("error_unknown_summary"), WithTechnicalDetail(T.Text("error_unknown_detail"), ex));
        }

        private static string WithTechnicalDetail(string detail, Exception ex)
        {
            return detail + "\r\n\r\n" + T.Text("error_technical_detail") + ": " + ex.Message;
        }
    }

    internal static class ErrorLogStore
    {
        public static string LogDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexFloat", "logs");
            }
        }

        public static void Write(MonitorSnapshot snapshot)
        {
            if (snapshot == null || snapshot.ErrorMessage == null) return;
            Write(snapshot.ErrorCode, snapshot.ErrorMessage, snapshot.ErrorDetail);
        }

        public static void Write(string errorCode, string errorMessage, string errorDetail)
        {
            if (string.IsNullOrWhiteSpace(errorMessage)) return;
            try
            {
                Directory.CreateDirectory(LogDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var code = string.IsNullOrWhiteSpace(errorCode) ? "UNKNOWN" : SafeFilePart(errorCode);
                var path = Path.Combine(LogDir, stamp + "-" + code + ".log");
                var sb = new StringBuilder();
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                var bilingual = ErrorTextForCode(errorCode, errorMessage);
                sb.AppendLine("CodexFloat Error Log / CodexFloat 错误日志");
                sb.AppendLine("Time / 时间: " + now);
                sb.AppendLine("Code / 错误代码: " + (errorCode ?? "--"));
                sb.AppendLine();
                sb.AppendLine("[中文]");
                sb.AppendLine("摘要: " + bilingual.ChineseSummary);
                sb.AppendLine("处理建议: " + bilingual.ChineseAdvice);
                sb.AppendLine();
                sb.AppendLine("[English]");
                sb.AppendLine("Summary: " + bilingual.EnglishSummary);
                sb.AppendLine("Suggested action: " + bilingual.EnglishAdvice);
                sb.AppendLine();
                sb.AppendLine("[Original detail / 原始详情]");
                sb.AppendLine("Original summary / 原始摘要: " + errorMessage);
                sb.AppendLine(errorDetail ?? "");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        public static void OpenFolder()
        {
            Directory.CreateDirectory(LogDir);
            LinkOpener.OpenUrl(LogDir);
        }

        private static string SafeFilePart(string value)
        {
            var sb = new StringBuilder();
            foreach (var ch in value)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-') sb.Append(ch);
            }
            return sb.Length == 0 ? "UNKNOWN" : sb.ToString();
        }

        private static ErrorLogText ErrorTextForCode(string errorCode, string fallback)
        {
            var code = (errorCode ?? "").Trim().ToUpperInvariant();
            switch (code)
            {
                case "MISSING_CREDENTIALS":
                    return new ErrorLogText("未找到 Codex 登录凭据。", "请先启动 Codex 并确认已登录，然后在设置中使用自动读取，或手动保存 ACCESS_TOKEN 和 ACCOUNT_ID。", "No Codex login credentials were found.", "Start Codex and make sure you are signed in, then use Auto Import in Settings or manually save ACCESS_TOKEN and ACCOUNT_ID.");
                case "AUTH_EXPIRED":
                case "UNAUTHORIZED":
                    return new ErrorLogText("登录凭据已失效或被服务端拒绝。", "请启动 Codex 刷新登录状态，然后在设置中重新自动读取并保存凭据。", "The saved login credentials are expired or were rejected by the service.", "Start Codex to refresh the login state, then auto-import and save credentials again in Settings.");
                case "NETWORK_UNAVAILABLE":
                    return new ErrorLogText("网络不可用，无法连接 ChatGPT/Codex 后端。", "请检查网络、代理或防火墙，然后刷新。", "Network is unavailable and CodexFloat cannot connect to the ChatGPT/Codex backend.", "Check your network, proxy, or firewall, then refresh.");
                case "CONNECTION_INTERRUPTED":
                    return new ErrorLogText("连接被临时中断。", "这通常和电脑睡眠唤醒、网络/代理切换或 HTTPS 连接复用有关；请稍后刷新。", "The backend connection was interrupted.", "This is often related to sleep/wake, network or proxy switching, or HTTPS connection reuse. Refresh again later.");
                case "REQUEST_TIMEOUT":
                    return new ErrorLogText("请求超时。", "请检查网络或代理状态，然后刷新。", "The request timed out.", "Check your network or proxy status, then refresh.");
                case "SERVER_UNAVAILABLE":
                    return new ErrorLogText("服务暂时不可用。", "ChatGPT/Codex 后端可能暂时不可用，请稍后刷新。", "The service is temporarily unavailable.", "The ChatGPT/Codex backend may be temporarily unavailable. Refresh again later.");
                case "API_CHANGED":
                    return new ErrorLogText("接口可能已经变化。", "当前版本无法解析接口返回内容，请检查更新。", "The API may have changed.", "The current version cannot parse the response. Check for updates.");
                case "CREDENTIAL_CONFIG_ERROR":
                    return new ErrorLogText("本地加密凭据无法解密。", "请在设置中重新自动读取或手动保存凭据。", "The locally encrypted credentials could not be decrypted.", "Auto-import or manually save credentials again in Settings.");
                case "HTTP_ERROR":
                    return new ErrorLogText("请求被服务端拒绝。", "请确认 Codex 登录状态和账号权限，然后刷新。", "The request was rejected by the server.", "Check Codex sign-in status and account access, then refresh.");
                case "CODEX_RADAR_UNAVAILABLE":
                    return new ErrorLogText("无法读取 Codex Radar 模型 IQ 数据。", "请检查网络；这不会影响 Codex 用量数据读取。", "Codex Radar model IQ data could not be loaded.", "Check your network. This does not affect Codex usage data.");
                case "CODEX_RADAR_EMPTY":
                    return new ErrorLogText("Codex Radar 返回内容里没有可用的模型 IQ 分数。", "请稍后刷新，或等待 Codex Radar 数据恢复。", "The Codex Radar response did not contain usable model IQ scores.", "Refresh later or wait for Codex Radar data to recover.");
                default:
                    var message = string.IsNullOrWhiteSpace(fallback) ? "未知错误。" : fallback;
                    return new ErrorLogText(message, "请根据原始详情排查；如果问题持续存在，请查看是否有新版。", message, "Check the original detail below. If the issue persists, check for updates.");
            }
        }

        private struct ErrorLogText
        {
            public readonly string ChineseSummary;
            public readonly string ChineseAdvice;
            public readonly string EnglishSummary;
            public readonly string EnglishAdvice;

            public ErrorLogText(string chineseSummary, string chineseAdvice, string englishSummary, string englishAdvice)
            {
                this.ChineseSummary = chineseSummary;
                this.ChineseAdvice = chineseAdvice;
                this.EnglishSummary = englishSummary;
                this.EnglishAdvice = englishAdvice;
            }
        }
    }

    internal sealed class UsageInfo
    {
        public string Plan = "";
        public UsageWindow FiveHour = UsageWindow.Unavailable("5h");
        public UsageWindow Weekly = UsageWindow.Unavailable("Weekly");
        public string CreditsBalance = "";
        public string UserName = "";
        public bool HasCredits;

        public static UsageInfo FromJson(Dictionary<string, object> root)
        {
            var info = new UsageInfo();
            if (root == null) return info;
            info.Plan = Text(root, "plan_type") ?? Text(root, "plan") ?? "";
            info.UserName = FindUserName(root);
            var source = Dict(root, "rate_limit_status") ?? root;
            var rateLimit = Dict(source, "rate_limit") ?? Dict(root, "rate_limit");
            if (rateLimit != null)
            {
                info.FiveHour = UsageWindow.FromJson("5h", Dict(rateLimit, "primary_window") ?? Dict(rateLimit, "primary"), 18000);
                info.Weekly = UsageWindow.FromJson("Weekly", Dict(rateLimit, "secondary_window") ?? Dict(rateLimit, "secondary"), 604800);
            }
            var credits = Dict(root, "credits");
            if (credits != null)
            {
                info.CreditsBalance = Text(credits, "balance") ?? "";
                info.HasCredits = Bool(credits, "has_credits") || Bool(credits, "hasCredits");
            }
            return info;
        }

        private static string FindUserName(Dictionary<string, object> root)
        {
            var direct = FirstText(root, "display_name", "displayName", "name", "email", "username", "login", "user_email", "account_email");
            if (IsUsableUserName(direct)) return direct.Trim();

            foreach (var key in new[] { "user", "account", "profile", "viewer", "owner", "organization" })
            {
                var nested = Dict(root, key);
                if (nested == null) continue;
                var value = FirstText(nested, "display_name", "displayName", "name", "email", "username", "login");
                if (IsUsableUserName(value)) return value.Trim();
            }

            return "";
        }

        private static string FirstText(Dictionary<string, object> root, params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = Text(root, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static bool IsUsableUserName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            if (value.Length < 2 || value.Length > 120) return false;
            if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
            return value.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0;
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
            bool parsed;
            if (!root.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) && parsed;
        }

        internal static double? Number(Dictionary<string, object> root, params string[] keys)
        {
            if (root == null) return null;
            foreach (var key in keys)
            {
                object value;
                double parsed;
                if (!root.TryGetValue(key, out value) || value == null) continue;
                if (value is int) return (int)value;
                if (value is long) return (long)value;
                if (value is double) return (double)value;
                if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)) return parsed;
            }
            return null;
        }
    }

    internal sealed class UsageWindow
    {
        public string Label = "";
        public bool Available;
        public double UsedPercent;
        public double RemainingPercent;
        public DateTime? ResetAtLocal;
        public int WindowSeconds;

        public static UsageWindow Unavailable(string label)
        {
            return new UsageWindow { Label = label, Available = false };
        }

        public static UsageWindow FromJson(string label, Dictionary<string, object> root, int defaultSeconds)
        {
            if (root == null) return Unavailable(label);
            var remainingPct = NormalizeRemainingPercent(UsageInfo.Number(root, "remaining_percent", "remaining_percentage"));
            var usedPctValue = NormalizeUsedPercent(UsageInfo.Number(root, "used_percent", "used_percentage", "utilization"));
            double? used = null;
            if (remainingPct.HasValue) used = 100.0 - remainingPct.Value;
            else if (usedPctValue.HasValue) used = usedPctValue.Value;
            else used = DeriveUsedPercent(root);
            var usedPct = ClampPercent(used ?? 0.0);

            var resetAt = UsageInfo.Number(root, "reset_at", "resets_at", "reset_time", "expires_at");
            var resetAfter = UsageInfo.Number(root, "reset_after_seconds");
            DateTime? resetLocal = null;
            if (resetAt.HasValue) resetLocal = DateText.UnixToLocal(resetAt.Value);
            else if (resetAfter.HasValue) resetLocal = DateTime.Now.AddSeconds(Math.Max(0, resetAfter.Value));

            var seconds = UsageInfo.Number(root, "limit_window_seconds");
            var minutes = UsageInfo.Number(root, "window_minutes");
            var windowSeconds = seconds.HasValue ? (int)Math.Round(seconds.Value) : (minutes.HasValue ? (int)Math.Round(minutes.Value * 60.0) : defaultSeconds);

            return new UsageWindow { Label = label, Available = true, UsedPercent = usedPct, RemainingPercent = 100.0 - usedPct, ResetAtLocal = resetLocal, WindowSeconds = windowSeconds };
        }

        private static double? NormalizeRemainingPercent(double? value)
        {
            if (!value.HasValue) return null;
            var pct = value.Value;
            if (pct >= 0.0 && pct <= 1.0) pct *= 100.0;
            return ClampPercent(pct);
        }

        private static double? NormalizeUsedPercent(double? value)
        {
            if (!value.HasValue) return null;
            var pct = value.Value;
            if (pct >= 0.0 && pct < 1.0) pct *= 100.0;
            return ClampPercent(pct);
        }

        private static double? DeriveUsedPercent(Dictionary<string, object> root)
        {
            var limit = UsageInfo.Number(root, "limit", "total", "total_limit", "quota", "max", "maximum");
            var remaining = UsageInfo.Number(root, "remaining", "remaining_count", "remaining_uses", "remaining_requests", "available", "available_count");
            var used = UsageInfo.Number(root, "used", "used_count", "used_uses", "used_requests", "consumed", "consumed_count");
            if (limit.HasValue && limit.Value > 0.0)
            {
                if (remaining.HasValue) return 100.0 - (remaining.Value / limit.Value * 100.0);
                if (used.HasValue) return used.Value / limit.Value * 100.0;
            }

            if (remaining.HasValue && used.HasValue && remaining.Value + used.Value > 0.0)
            {
                return used.Value / (remaining.Value + used.Value) * 100.0;
            }

            return null;
        }

        private static double ClampPercent(double value)
        {
            return Math.Max(0.0, Math.Min(100.0, value));
        }

        public string BarLine()
        {
            if (!this.Available) return this.Label + " " + T.Text("剩余") + " --";
            return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2:0.#}%  {3}", this.Label, T.Text("剩余"), this.RemainingPercent, DateText.ResetDescription(this.ResetAtLocal));
        }

        public string DetailLine()
        {
            if (!this.Available) return this.Label + ": --";
            return string.Format(CultureInfo.InvariantCulture, "{0}: {1} {2:0.0}% ({3} {4:0.0}%)  {5}", this.Label, T.Text("剩余"), this.RemainingPercent, T.Text("已用"), this.UsedPercent, DateText.ResetDescription(this.ResetAtLocal));
        }
    }

    internal sealed class ResetCardInfo
    {
        public readonly List<DateTime> Expirations = new List<DateTime>();

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
            return info;
        }

        public IEnumerable<string> BarLines()
        {
            if (this.Expirations.Count == 0)
            {
                yield return T.Text("重置卡") + " --";
                yield break;
            }
            for (int i = 0; i < this.Expirations.Count; i++)
            {
                yield return T.Text("重置卡") + " " + (i + 1).ToString(CultureInfo.InvariantCulture) + "  " + DateText.ResetDescription(this.Expirations[i]);
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
                        if (DateText.TryParseDate(kv.Value, out dt)) dates.Add(dt);
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
    }

    internal sealed class ModelIqRadar
    {
        public readonly List<ModelIqScore> Scores = new List<ModelIqScore>();
        public DateTime UpdatedAt;
        public string ErrorCode = "";
        public string ErrorMessage = "";

        public static ModelIqRadar Empty()
        {
            var radar = new ModelIqRadar();
            radar.Scores.Add(new ModelIqScore("GPT-5.5-XHigh"));
            radar.Scores.Add(new ModelIqScore("GPT-5.5-High"));
            radar.Scores.Add(new ModelIqScore("GPT-5.5-Medium"));
            radar.Scores.Add(new ModelIqScore("GPT-5.4-XHigh"));
            return radar;
        }

        public static ModelIqRadar FromJson(Dictionary<string, object> root)
        {
            var radar = Empty();
            if (root == null) return radar;
            DateTime updated;
            if (DateText.TryParseDate(UsageInfo.Text(root, "monitored_at"), out updated)) radar.UpdatedAt = updated;

            var modelIq = UsageInfo.Dict(root, "model_iq");
            if (modelIq == null) return radar;
            radar.Scores[0] = ScoreFromLatest("GPT-5.5-XHigh", UsageInfo.Dict(modelIq, "latest"));

            var comparisons = UsageInfo.Dict(modelIq, "comparisons");
            radar.Scores[1] = ScoreFromComparison("GPT-5.5-High", UsageInfo.Dict(comparisons, "gpt_55_high"));
            radar.Scores[2] = ScoreFromComparison("GPT-5.5-Medium", UsageInfo.Dict(comparisons, "gpt_55_medium"));
            radar.Scores[3] = ScoreFromComparison("GPT-5.4-XHigh", UsageInfo.Dict(comparisons, "gpt_54_xhigh"));
            return radar;
        }

        public bool HasAnyScore()
        {
            foreach (var score in this.Scores)
            {
                if (score != null && score.Available) return true;
            }
            return false;
        }

        private static ModelIqScore ScoreFromComparison(string label, Dictionary<string, object> root)
        {
            return ScoreFromLatest(label, UsageInfo.Dict(root, "latest"));
        }

        private static ModelIqScore ScoreFromLatest(string label, Dictionary<string, object> latest)
        {
            var score = new ModelIqScore(label);
            if (latest == null) return score;
            var value = UsageInfo.Number(latest, "score");
            if (value.HasValue)
            {
                score.Available = true;
                score.Score = value.Value;
            }
            score.Status = UsageInfo.Text(latest, "status") ?? "";
            DateTime date;
            if (DateText.TryParseDate(UsageInfo.Text(latest, "date"), out date)) score.Date = date;
            return score;
        }
    }

    internal sealed class ModelIqScore
    {
        public string Label;
        public bool Available;
        public double Score;
        public string Status = "";
        public DateTime Date;

        public ModelIqScore(string label)
        {
            this.Label = label;
        }
    }

    internal sealed class MonitorSnapshot
    {
        public UsageInfo Usage;
        public ResetCardInfo ResetCards;
        public ModelIqRadar ModelIq = ModelIqRadar.Empty();
        public DateTime UpdatedAt;
        public string ErrorCode;
        public string ErrorMessage;
        public string ErrorDetail;

        public MonitorSnapshot(UsageInfo usage, ResetCardInfo cards, DateTime updatedAt, string error)
        {
            this.Usage = usage ?? new UsageInfo();
            this.ResetCards = cards ?? new ResetCardInfo();
            this.UpdatedAt = updatedAt;
            this.ErrorMessage = error;
        }

        public static MonitorSnapshot Loading()
        {
            return new MonitorSnapshot(new UsageInfo(), new ResetCardInfo(), DateTime.Now, null);
        }

        public static MonitorSnapshot Error(string code, string message, string detail)
        {
            return new MonitorSnapshot(new UsageInfo(), new ResetCardInfo(), DateTime.Now, message) { ErrorCode = code, ErrorDetail = detail };
        }

        public List<string> BarMessages()
        {
            var lines = new List<string>();
            if (this.ErrorMessage != null)
            {
                lines.Add(T.Text("连接失败") + " - " + this.ErrorMessage);
                return lines;
            }
            lines.Add(this.Usage.FiveHour.BarLine());
            lines.Add(this.Usage.Weekly.BarLine());
            lines.AddRange(this.ResetCards.BarLines());
            return lines;
        }

        public string Details()
        {
            if (this.ErrorMessage != null) return T.Text("错误代码") + ": " + (this.ErrorCode ?? "--") + "\r\n" + (this.ErrorDetail ?? this.ErrorMessage);
            var sb = new StringBuilder();
            sb.AppendLine(T.Text("详情标题"));
            if (!string.IsNullOrWhiteSpace(this.Usage.UserName)) sb.AppendLine(T.Text("用户") + ": " + this.Usage.UserName);
            sb.AppendLine(T.Text("更新时间") + ": " + this.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            if (!string.IsNullOrWhiteSpace(this.Usage.Plan)) sb.AppendLine(T.Text("计划") + ": " + this.Usage.Plan);
            sb.AppendLine(this.Usage.FiveHour.DetailLine());
            sb.AppendLine(this.Usage.Weekly.DetailLine());
            if (this.Usage.HasCredits) sb.AppendLine(T.Text("积分") + ": " + (string.IsNullOrWhiteSpace(this.Usage.CreditsBalance) ? "--" : this.Usage.CreditsBalance));
            sb.AppendLine();
            if (this.ResetCards.Expirations.Count == 0)
            {
                sb.AppendLine(T.Text("重置卡") + ": --");
            }
            else
            {
                for (int i = 0; i < this.ResetCards.Expirations.Count; i++)
                {
                    sb.AppendLine(T.Text("重置卡") + " " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + DateText.ResetDescription(this.ResetCards.Expirations[i]));
                }
            }
            return sb.ToString().TrimEnd();
        }

        public string TrayText()
        {
            var text = this.ErrorMessage != null ? "Codex: " + this.ErrorMessage : this.Usage.FiveHour.BarLine();
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }
    }

    internal sealed class PointEventArgs : EventArgs
    {
        public Point Location;
        public PointEventArgs(Point location) { this.Location = location; }
    }

    internal sealed class ThemeEventArgs : EventArgs
    {
        public string ThemeName;
        public ThemeEventArgs(string themeName) { this.ThemeName = themeName; }
    }

    internal sealed class BehaviorEventArgs : EventArgs
    {
        public MonitorConfig Config;
        public BehaviorEventArgs(MonitorConfig config) { this.Config = config; }
    }

    internal sealed class LanguageEventArgs : EventArgs
    {
        public string Language;
        public LanguageEventArgs(string language) { this.Language = language; }
    }

    internal sealed class MonitorTheme
    {
        public string Name;
        public Color BackTop;
        public Color BackBottom;
        public Color PanelTop;
        public Color PanelBottom;
        public Color WaterTop;
        public Color WaterBottom;
        public Color Accent;
        public Color CountdownSafe;
        public Color Text;
        public Color Muted;
        public Color RingBase;
    }

    internal static class ThemePalette
    {
        public static readonly string[] Names = new[] { "Traffic Classic", "Traffic Blue", "Minimal Dark", "Warm Amber" };
        private static readonly Color CountdownSafeColor = Color.FromArgb(64, 230, 178);

        public static MonitorTheme Get(string name)
        {
            if (string.Equals(name, "Traffic Blue", StringComparison.OrdinalIgnoreCase))
            {
                return new MonitorTheme {
                    Name = "Traffic Blue",
                    BackTop = Color.FromArgb(14, 24, 43),
                    BackBottom = Color.FromArgb(18, 52, 88),
                    PanelTop = Color.FromArgb(18, 29, 49),
                    PanelBottom = Color.FromArgb(12, 41, 72),
                    WaterTop = Color.FromArgb(138, 204, 255),
                    WaterBottom = Color.FromArgb(43, 111, 202),
                    Accent = Color.FromArgb(116, 190, 255),
                    CountdownSafe = CountdownSafeColor,
                    Text = Color.White,
                    Muted = Color.FromArgb(210, 220, 234, 248),
                    RingBase = Color.FromArgb(62, 174, 205, 236)
                };
            }
            if (string.Equals(name, "Minimal Dark", StringComparison.OrdinalIgnoreCase))
            {
                return new MonitorTheme {
                    Name = "Minimal Dark",
                    BackTop = Color.FromArgb(17, 18, 21),
                    BackBottom = Color.FromArgb(30, 31, 35),
                    PanelTop = Color.FromArgb(23, 24, 28),
                    PanelBottom = Color.FromArgb(13, 14, 17),
                    WaterTop = Color.FromArgb(190, 197, 205),
                    WaterBottom = Color.FromArgb(82, 92, 104),
                    Accent = Color.FromArgb(196, 204, 214),
                    CountdownSafe = CountdownSafeColor,
                    Text = Color.FromArgb(248, 250, 252),
                    Muted = Color.FromArgb(204, 206, 214, 222),
                    RingBase = Color.FromArgb(58, 195, 202, 210)
                };
            }
            if (string.Equals(name, "Warm Amber", StringComparison.OrdinalIgnoreCase))
            {
                return new MonitorTheme {
                    Name = "Warm Amber",
                    BackTop = Color.FromArgb(35, 28, 36),
                    BackBottom = Color.FromArgb(76, 46, 68),
                    PanelTop = Color.FromArgb(41, 33, 42),
                    PanelBottom = Color.FromArgb(66, 41, 58),
                    WaterTop = Color.FromArgb(213, 186, 255),
                    WaterBottom = Color.FromArgb(129, 88, 199),
                    Accent = Color.FromArgb(191, 156, 255),
                    CountdownSafe = CountdownSafeColor,
                    Text = Color.White,
                    Muted = Color.FromArgb(214, 232, 220, 244),
                    RingBase = Color.FromArgb(58, 218, 200, 236)
                };
            }
            return new MonitorTheme {
                Name = "Traffic Classic",
                BackTop = Color.FromArgb(15, 25, 27),
                BackBottom = Color.FromArgb(9, 76, 72),
                PanelTop = Color.FromArgb(20, 31, 33),
                PanelBottom = Color.FromArgb(8, 64, 61),
                WaterTop = Color.FromArgb(132, 232, 224),
                WaterBottom = Color.FromArgb(34, 164, 156),
                Accent = Color.FromArgb(154, 220, 214),
                CountdownSafe = CountdownSafeColor,
                Text = Color.White,
                Muted = Color.FromArgb(210, 224, 238, 234),
                RingBase = Color.FromArgb(66, 197, 220, 216)
            };
        }
    }

    internal sealed class MiniBarForm : Form
    {
        public event EventHandler<PointEventArgs> LocationSaved;
        public event EventHandler RefreshRequested;
        public event EventHandler SettingsRequested;
        public event EventHandler TrustedEnvironmentsRequested;
        public event EventHandler ResetPositionRequested;
        public event EventHandler HelpRequested;
        public event EventHandler ErrorLogRequested;
        public event EventHandler AboutRequested;
        public event EventHandler CheckUpdatesRequested;
        public event EventHandler<ThemeEventArgs> ThemeChanged;
        public event EventHandler<LanguageEventArgs> LanguageChanged;
        public event EventHandler<BehaviorEventArgs> BehaviorChanged;
        public event EventHandler ExitRequested;

        private readonly WinFormsTimer rotateTimer = new WinFormsTimer();
        private readonly WinFormsTimer waveTimer = new WinFormsTimer();
        private readonly WinFormsTimer autoCollapseTimer = new WinFormsTimer();
        private readonly Size miniSize = new Size(96, 96);
        private readonly Size loadingMiniSize = new Size(96, 114);
        private const int LoadingTextPad = 18;
        private readonly Size detailSize = new Size(500, 330);
        private ToolStripMenuItem detailMenuItem;
        private ToolStripMenuItem autoStartMenuItem;
        private ToolStripMenuItem topMostMenuItem;
        private ToolStripMenuItem clickThroughMenuItem;
        private ToolStripMenuItem lockPositionMenuItem;
        private ToolStripMenuItem opacityMenuItem;
        private MonitorConfig behaviorConfig = new MonitorConfig();
        private MonitorSnapshot snapshot = MonitorSnapshot.Loading();
        private MonitorTheme theme = ThemePalette.Get("Traffic Classic");
        private string statusText = "";
        private int messageIndex;
        private float wavePhase;
        private float loadingPhase;
        private bool expanded;
        private bool refreshing;
        private bool dragging;
        private Point dragStartMouse;
        private Point dragStartLocation;
        private bool movedWhileDragging;
        private bool mouseClickThrough;
        private bool lockPosition;
        private byte layerOpacity = 255;

        public MiniBarForm()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.DoubleBuffered = true;
            this.Size = this.miniSize;
            this.BackColor = Color.FromArgb(18, 20, 24);
            this.Font = new Font("Segoe UI", 9f);
            this.Cursor = Cursors.SizeAll;
            this.ContextMenuStrip = this.BuildMenu();

            this.MouseDown += this.OnMouseDownDrag;
            this.MouseMove += this.OnMouseMoveDrag;
            this.MouseUp += this.OnMouseUpDrag;
            this.MouseLeave += delegate { this.QueueAutoCollapse(); };
            this.ContextMenuStrip.Closed += delegate { this.QueueAutoCollapse(); };

            this.rotateTimer.Interval = 4200;
            this.rotateTimer.Tick += delegate { this.StartRoll(); };
            this.rotateTimer.Start();

            this.waveTimer.Interval = 55;
            this.waveTimer.Tick += delegate {
                this.wavePhase += 0.28f;
                if (this.refreshing) this.loadingPhase += 5.8f;
                if (this.loadingPhase > 3600f) this.loadingPhase -= 3600f;
                if (this.wavePhase > 10000f) this.wavePhase = 0f;
                this.Invalidate();
            };
            this.waveTimer.Start();

            this.autoCollapseTimer.Interval = 180;
            this.autoCollapseTimer.Tick += delegate {
                this.autoCollapseTimer.Stop();
                if (this.expanded && !this.dragging && !this.ContainsCursor() && !this.ContextMenuStrip.Visible)
                {
                    this.Collapse();
                }
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x80000;    // WS_EX_LAYERED
                if (this.mouseClickThrough) cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_CONTEXTMENU = 0x007B;
            if (m.Msg == WM_CONTEXTMENU && this.ContextMenuStrip != null)
            {
                this.ShowContextMenuInsideCurrentScreen();
                return;
            }
            base.WndProc(ref m);
        }

        public void ApplyBehavior(MonitorConfig cfg)
        {
            this.behaviorConfig.AutoStart = cfg.AutoStart;
            this.behaviorConfig.OpacityPercent = cfg.OpacityPercent;
            this.behaviorConfig.AlwaysOnTop = cfg.AlwaysOnTop;
            this.behaviorConfig.MouseClickThrough = cfg.MouseClickThrough;
            this.behaviorConfig.LockPosition = cfg.LockPosition;
            this.behaviorConfig.ShowUserNameInDetails = cfg.ShowUserNameInDetails;
            this.behaviorConfig.ScrollShowFiveHour = cfg.ScrollShowFiveHour;
            this.behaviorConfig.ScrollShowWeekly = cfg.ScrollShowWeekly;
            this.behaviorConfig.ScrollShowGpt55XHigh = cfg.ScrollShowGpt55XHigh;
            this.behaviorConfig.ScrollShowGpt55High = cfg.ScrollShowGpt55High;
            this.behaviorConfig.ScrollShowGpt55Medium = cfg.ScrollShowGpt55Medium;
            this.behaviorConfig.ScrollShowGpt54XHigh = cfg.ScrollShowGpt54XHigh;
            this.behaviorConfig.EnvironmentCheckOnStartup = cfg.EnvironmentCheckOnStartup;
            this.behaviorConfig.EnvironmentConfirmMediumRiskOnManualRefresh = cfg.EnvironmentConfirmMediumRiskOnManualRefresh;
            this.behaviorConfig.EnvironmentRecheckHighRiskOnManualRefresh = cfg.EnvironmentRecheckHighRiskOnManualRefresh;
            this.TopMost = cfg.AlwaysOnTop;
            this.Opacity = 1.0;
            this.layerOpacity = (byte)Math.Max(89, Math.Min(255, (int)Math.Round(255.0 * Math.Max(35, Math.Min(100, cfg.OpacityPercent)) / 100.0)));
            this.lockPosition = cfg.LockPosition;
            this.Cursor = this.lockPosition ? Cursors.Hand : Cursors.SizeAll;
            this.UpdateBehaviorMenuChecks();
            if (this.mouseClickThrough != cfg.MouseClickThrough)
            {
                this.mouseClickThrough = cfg.MouseClickThrough;
                if (this.IsHandleCreated) this.RecreateHandle();
            }
            this.UpdateLayeredSurface();
        }

        public void SetInitialLocation(int? x, int? y)
        {
            var area = x.HasValue && y.HasValue
                ? this.WorkingAreaForPoint(new Point(x.Value + this.miniSize.Width / 2, y.Value + this.miniSize.Height / 2))
                : Screen.PrimaryScreen.WorkingArea;
            var px = x.HasValue ? x.Value : area.Right - this.Width - 24;
            var py = y.HasValue ? y.Value : area.Bottom - this.Height - 24;
            this.Location = this.ClampLocation(px, py, this.Size, area, 0);
        }

        public void SetStatusText(string text)
        {
            this.statusText = text ?? "";
            this.Invalidate();
        }

        public void SetRefreshing(bool isRefreshing)
        {
            var ballLocation = this.LocationForSave();
            this.refreshing = isRefreshing;
            if (!isRefreshing) this.loadingPhase = 0f;
            if (!this.expanded) this.ResizeMiniForState(ballLocation);
            this.Invalidate();
        }

        public void SetSnapshot(MonitorSnapshot snapshot)
        {
            this.snapshot = snapshot ?? MonitorSnapshot.Loading();
            if (this.messageIndex >= this.ScrollItems().Count) this.messageIndex = 0;
            this.Invalidate();
        }

        public void SetTheme(string themeName)
        {
            this.theme = ThemePalette.Get(themeName);
            this.UpdateThemeChecks();
            this.Invalidate();
        }

        public void SetLanguage(string language)
        {
            T.SetLanguage(language);
            var oldMenu = this.ContextMenuStrip;
            this.ContextMenuStrip = this.BuildMenu();
            this.ContextMenuStrip.Closed += delegate { this.QueueAutoCollapse(); };
            if (oldMenu != null) oldMenu.Dispose();
            this.UpdateThemeChecks();
            this.UpdateBehaviorMenuChecks();
            this.UpdateDetailMenuText();
            this.Invalidate();
        }

        public void ShowExpandedTemporarily()
        {
            this.Expand();
            this.BringToFront();
        }

        public bool ContainsCursor()
        {
            return this.Visible && this.ClientRectangle.Contains(this.PointToClient(Cursor.Position));
        }

        private Rectangle CurrentScreenBounds()
        {
            return Screen.FromControl(this).WorkingArea;
        }

        private void ShowContextMenuInsideCurrentScreen()
        {
            var menu = this.ContextMenuStrip;
            if (menu == null) return;
            this.UpdateThemeChecks();
            this.UpdateBehaviorMenuChecks();

            var area = this.CurrentScreenBounds();
            var size = menu.GetPreferredSize(Size.Empty);
            if (size.Width <= 0 || size.Height <= 0) size = new Size(180, 260);

            var point = Cursor.Position;
            if (point.X + size.Width > area.Right) point.X = area.Right - size.Width - 2;
            if (point.Y + size.Height > area.Bottom) point.Y = area.Bottom - size.Height - 2;
            if (point.X < area.Left) point.X = area.Left + 2;
            if (point.Y < area.Top) point.Y = area.Top + 2;

            menu.Show(this, this.PointToClient(point));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            this.Region = null;
            this.UpdateLayeredSurface();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            ConfigureGraphics(g);
            if (this.expanded) this.DrawDetail(g);
            else this.DrawMini(g);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            this.UpdateLayeredSurface();
        }

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            base.OnInvalidated(e);
            this.UpdateLayeredSurface();
        }

        private void UpdateLayeredSurface()
        {
            if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0) return;
            if (this.expanded && (this.Width < 260 || this.Height < 180)) return;

            using (var bitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    ConfigureGraphics(g);
                    g.Clear(Color.Transparent);
                    if (this.expanded) this.DrawDetail(g);
                    else this.DrawMini(g);
                }

                IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
                IntPtr memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
                IntPtr hBitmap = IntPtr.Zero;
                IntPtr oldBitmap = IntPtr.Zero;
                try
                {
                    hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                    oldBitmap = NativeMethods.SelectObject(memoryDc, hBitmap);

                    var top = new NativeMethods.PointStruct(this.Left, this.Top);
                    var size = new NativeMethods.SizeStruct(this.Width, this.Height);
                    var source = new NativeMethods.PointStruct(0, 0);
                    var blend = new NativeMethods.BlendFunction();
                    blend.BlendOp = NativeMethods.AC_SRC_OVER;
                    blend.BlendFlags = 0;
                    blend.SourceConstantAlpha = this.layerOpacity;
                    blend.AlphaFormat = NativeMethods.AC_SRC_ALPHA;

                    NativeMethods.UpdateLayeredWindow(this.Handle, screenDc, ref top, ref size, memoryDc, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
                }
                finally
                {
                    if (oldBitmap != IntPtr.Zero) NativeMethods.SelectObject(memoryDc, oldBitmap);
                    if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
                    NativeMethods.DeleteDC(memoryDc);
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        private static void ConfigureGraphics(Graphics g)
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            this.detailMenuItem = new ToolStripMenuItem(T.Text("显示详情"), null, delegate { this.ToggleDetails(); });
            menu.Items.Add(this.detailMenuItem);
            menu.Items.Add(T.Text("刷新"), null, delegate { Raise(this.RefreshRequested); });
            menu.Items.Add(this.BuildAppearanceMenu());
            menu.Items.Add(this.BuildBehaviorMenu());
            menu.Items.Add(this.BuildEnvironmentSafetyMenu());
            menu.Items.Add(this.BuildScrollDataMenu());
            menu.Items.Add(this.BuildLanguageMenu());
            menu.Items.Add(T.Text("设置"), null, delegate { Raise(this.SettingsRequested); });
            menu.Items.Add(T.Text("重置悬浮窗位置"), null, delegate { Raise(this.ResetPositionRequested); });
            menu.Items.Add(T.Text("帮助"), null, delegate { Raise(this.HelpRequested); });
            menu.Items.Add(T.Text("关于"), null, delegate { Raise(this.AboutRequested); });
            menu.Items.Add(T.Text("检查更新"), null, delegate { Raise(this.CheckUpdatesRequested); });
            menu.Items.Add(T.Text("查看错误日志"), null, delegate { Raise(this.ErrorLogRequested); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(T.Text("退出"), null, delegate { Raise(this.ExitRequested); });
            MenuDropDownPlacer.Attach(menu, this.CurrentScreenBounds);
            return menu;
        }

        private ToolStripMenuItem BuildBehaviorMenu()
        {
            var root = new ToolStripMenuItem(T.Text("快捷设置"));
            this.autoStartMenuItem = new ToolStripMenuItem(T.Text("开机自动启动"), null, delegate { this.ToggleAutoStart(); });
            this.opacityMenuItem = this.BuildOpacityMenu();
            this.topMostMenuItem = new ToolStripMenuItem(T.Text("总是置顶"), null, delegate { this.ToggleAlwaysOnTop(); });
            this.clickThroughMenuItem = new ToolStripMenuItem(T.Text("鼠标穿透"), null, delegate { this.ToggleMouseClickThrough(); });
            this.lockPositionMenuItem = new ToolStripMenuItem(T.Text("锁定窗口位置"), null, delegate { this.ToggleLockPosition(); });
            root.DropDownItems.Add(this.autoStartMenuItem);
            root.DropDownItems.Add(this.opacityMenuItem);
            root.DropDownItems.Add(this.topMostMenuItem);
            root.DropDownItems.Add(this.clickThroughMenuItem);
            root.DropDownItems.Add(this.lockPositionMenuItem);
            return root;
        }

        private ToolStripMenuItem BuildEnvironmentSafetyMenu()
        {
            var root = new ToolStripMenuItem(T.Text("env_safety_title"));
            root.DropDownItems.Add(new ToolStripMenuItem(T.Text("env_check_on_startup")));
            root.DropDownOpening += delegate {
                root.DropDownItems.Clear();
                root.DropDownItems.Add(this.BuildEnvironmentToggle(T.Text("env_check_on_startup"), "startup", this.behaviorConfig.EnvironmentCheckOnStartup));
                root.DropDownItems.Add(this.BuildEnvironmentToggle(T.Text("env_confirm_medium_on_manual"), "medium", this.behaviorConfig.EnvironmentConfirmMediumRiskOnManualRefresh));
                root.DropDownItems.Add(this.BuildEnvironmentToggle(T.Text("env_recheck_high_on_manual"), "high", this.behaviorConfig.EnvironmentRecheckHighRiskOnManualRefresh));
                root.DropDownItems.Add(new ToolStripSeparator());
                root.DropDownItems.Add(T.Text("env_trusted_library"), null, delegate { Raise(this.TrustedEnvironmentsRequested); });
                MenuDropDownPlacer.AttachChildren(root, this.CurrentScreenBounds);
            };
            return root;
        }

        private ToolStripMenuItem BuildEnvironmentToggle(string text, string key, bool isChecked)
        {
            var item = new ToolStripMenuItem(text);
            item.Tag = key;
            item.Checked = isChecked;
            item.Click += delegate { this.ToggleEnvironmentSetting(key); };
            return item;
        }

        private ToolStripMenuItem BuildScrollDataMenu()
        {
            var root = new ToolStripMenuItem(T.Text("滚动数据"));
            root.DropDownItems.Add(this.BuildScrollToggle(T.Text("scroll_5h"), "5h", this.behaviorConfig.ScrollShowFiveHour));
            root.DropDownItems.Add(this.BuildScrollToggle(T.Text("scroll_weekly"), "weekly", this.behaviorConfig.ScrollShowWeekly));
            root.DropDownItems.Add(this.BuildScrollToggle(T.Text("scroll_gpt_55_xhigh"), "gpt55xhigh", this.behaviorConfig.ScrollShowGpt55XHigh));
            root.DropDownItems.Add(this.BuildScrollToggle(T.Text("scroll_gpt_55_high"), "gpt55high", this.behaviorConfig.ScrollShowGpt55High));
            root.DropDownItems.Add(this.BuildScrollToggle(T.Text("scroll_gpt_55_medium"), "gpt55medium", this.behaviorConfig.ScrollShowGpt55Medium));
            root.DropDownItems.Add(this.BuildScrollToggle(T.Text("scroll_gpt_54_xhigh"), "gpt54xhigh", this.behaviorConfig.ScrollShowGpt54XHigh));
            root.DropDownOpening += delegate {
                this.UpdateScrollMenuChecks(root);
                MenuDropDownPlacer.AttachChildren(root, this.CurrentScreenBounds);
            };
            return root;
        }

        private ToolStripMenuItem BuildScrollToggle(string text, string key, bool isChecked)
        {
            var item = new ToolStripMenuItem(text);
            item.Tag = key;
            item.Checked = isChecked;
            item.Click += delegate { this.ToggleScrollSelection(key); };
            return item;
        }

        private void UpdateScrollMenuChecks(ToolStripMenuItem root)
        {
            foreach (ToolStripMenuItem item in root.DropDownItems)
            {
                var key = item.Tag as string;
                switch (key)
                {
                    case "5h": item.Checked = this.behaviorConfig.ScrollShowFiveHour; break;
                    case "weekly": item.Checked = this.behaviorConfig.ScrollShowWeekly; break;
                    case "gpt55xhigh": item.Checked = this.behaviorConfig.ScrollShowGpt55XHigh; break;
                    case "gpt55high": item.Checked = this.behaviorConfig.ScrollShowGpt55High; break;
                    case "gpt55medium": item.Checked = this.behaviorConfig.ScrollShowGpt55Medium; break;
                    case "gpt54xhigh": item.Checked = this.behaviorConfig.ScrollShowGpt54XHigh; break;
                }
            }
        }

        private ToolStripMenuItem BuildOpacityMenu()
        {
            var root = new ToolStripMenuItem(T.Text("不透明度"));
            foreach (var value in new[] { 100, 90, 80, 70, 60, 50, 40, 35 })
            {
                var item = new ToolStripMenuItem(value.ToString(CultureInfo.InvariantCulture) + "%");
                item.Tag = value;
                item.Click += delegate(object sender, EventArgs e) {
                    this.behaviorConfig.OpacityPercent = (int)((ToolStripMenuItem)sender).Tag;
                    this.RaiseBehaviorChanged();
                };
                root.DropDownItems.Add(item);
            }
            return root;
        }

        private ToolStripMenuItem BuildLanguageMenu()
        {
            var root = new ToolStripMenuItem(T.Text("语言"));
            var zh = new ToolStripMenuItem("简体中文");
            zh.Checked = T.IsChinese;
            zh.Click += delegate {
                if (this.LanguageChanged != null) this.LanguageChanged(this, new LanguageEventArgs("zh-CN"));
            };
            var en = new ToolStripMenuItem("English");
            en.Checked = !T.IsChinese;
            en.Click += delegate {
                if (this.LanguageChanged != null) this.LanguageChanged(this, new LanguageEventArgs("en-US"));
            };
            root.DropDownItems.Add(zh);
            root.DropDownItems.Add(en);
            return root;
        }

        private void ToggleAutoStart()
        {
            this.behaviorConfig.AutoStart = !this.behaviorConfig.AutoStart;
            this.RaiseBehaviorChanged();
        }

        private void ToggleAlwaysOnTop()
        {
            this.behaviorConfig.AlwaysOnTop = !this.behaviorConfig.AlwaysOnTop;
            this.RaiseBehaviorChanged();
        }

        private void ToggleMouseClickThrough()
        {
            this.behaviorConfig.MouseClickThrough = !this.behaviorConfig.MouseClickThrough;
            this.RaiseBehaviorChanged();
        }

        private void ToggleLockPosition()
        {
            this.behaviorConfig.LockPosition = !this.behaviorConfig.LockPosition;
            this.RaiseBehaviorChanged();
        }

        private void ToggleScrollSelection(string key)
        {
            switch (key)
            {
                case "5h": this.behaviorConfig.ScrollShowFiveHour = !this.behaviorConfig.ScrollShowFiveHour; break;
                case "weekly": this.behaviorConfig.ScrollShowWeekly = !this.behaviorConfig.ScrollShowWeekly; break;
                case "gpt55xhigh": this.behaviorConfig.ScrollShowGpt55XHigh = !this.behaviorConfig.ScrollShowGpt55XHigh; break;
                case "gpt55high": this.behaviorConfig.ScrollShowGpt55High = !this.behaviorConfig.ScrollShowGpt55High; break;
                case "gpt55medium": this.behaviorConfig.ScrollShowGpt55Medium = !this.behaviorConfig.ScrollShowGpt55Medium; break;
                case "gpt54xhigh": this.behaviorConfig.ScrollShowGpt54XHigh = !this.behaviorConfig.ScrollShowGpt54XHigh; break;
            }
            this.messageIndex = 0;
            this.RaiseBehaviorChanged();
            this.Invalidate();
        }

        private void ToggleEnvironmentSetting(string key)
        {
            switch (key)
            {
                case "startup": this.behaviorConfig.EnvironmentCheckOnStartup = !this.behaviorConfig.EnvironmentCheckOnStartup; break;
                case "medium": this.behaviorConfig.EnvironmentConfirmMediumRiskOnManualRefresh = !this.behaviorConfig.EnvironmentConfirmMediumRiskOnManualRefresh; break;
                case "high": this.behaviorConfig.EnvironmentRecheckHighRiskOnManualRefresh = !this.behaviorConfig.EnvironmentRecheckHighRiskOnManualRefresh; break;
            }
            this.RaiseBehaviorChanged();
        }

        private void RaiseBehaviorChanged()
        {
            if (this.BehaviorChanged != null) this.BehaviorChanged(this, new BehaviorEventArgs(this.behaviorConfig));
            this.UpdateBehaviorMenuChecks();
        }

        private void UpdateBehaviorMenuChecks()
        {
            if (this.autoStartMenuItem != null) this.autoStartMenuItem.Checked = this.behaviorConfig.AutoStart;
            if (this.topMostMenuItem != null) this.topMostMenuItem.Checked = this.behaviorConfig.AlwaysOnTop;
            if (this.clickThroughMenuItem != null) this.clickThroughMenuItem.Checked = this.behaviorConfig.MouseClickThrough;
            if (this.lockPositionMenuItem != null) this.lockPositionMenuItem.Checked = this.behaviorConfig.LockPosition;
            if (this.opacityMenuItem != null)
            {
                foreach (ToolStripMenuItem item in this.opacityMenuItem.DropDownItems)
                {
                    item.Checked = (int)item.Tag == this.behaviorConfig.OpacityPercent;
                }
            }
        }

        private ToolStripMenuItem BuildAppearanceMenu()
        {
            var root = new ToolStripMenuItem(T.Text("外观"));
            foreach (var name in ThemePalette.Names)
            {
                var item = new ToolStripMenuItem(name);
                item.Click += delegate(object sender, EventArgs e) {
                    var selected = ((ToolStripMenuItem)sender).Text;
                    this.SetTheme(selected);
                    if (this.ThemeChanged != null) this.ThemeChanged(this, new ThemeEventArgs(selected));
                };
                root.DropDownItems.Add(item);
            }
            return root;
        }

        private void UpdateThemeChecks()
        {
            foreach (ToolStripItem item in this.ContextMenuStrip.Items)
            {
                var parent = item as ToolStripMenuItem;
                if (parent == null || parent.Text != T.Text("外观")) continue;
                foreach (ToolStripMenuItem child in parent.DropDownItems)
                {
                    child.Checked = child.Text == this.theme.Name;
                }
            }
        }

        private void StartRoll()
        {
            if (this.snapshot.ErrorMessage != null) return;
            var items = this.ScrollItems();
            this.messageIndex = (this.messageIndex + 1) % items.Count;
            this.Invalidate();
        }

        private void OnMouseDownDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            this.dragging = true;
            this.movedWhileDragging = false;
            this.dragStartMouse = Cursor.Position;
            this.dragStartLocation = this.Location;
        }

        private void OnMouseMoveDrag(object sender, MouseEventArgs e)
        {
            if (!this.dragging || this.lockPosition) return;
            var dx = Cursor.Position.X - this.dragStartMouse.X;
            var dy = Cursor.Position.Y - this.dragStartMouse.Y;
            if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2) this.movedWhileDragging = true;
            var area = this.WorkingAreaForPoint(Cursor.Position);
            this.Location = this.ClampLocation(this.dragStartLocation.X + dx, this.dragStartLocation.Y + dy, this.Size, area, 0);
        }

        private void OnMouseUpDrag(object sender, MouseEventArgs e)
        {
            if (!this.dragging) return;
            this.dragging = false;
            if (this.movedWhileDragging && this.LocationSaved != null)
            {
                this.LocationSaved(this, new PointEventArgs(this.LocationForSave()));
            }
            else if (e.Button == MouseButtons.Left)
            {
                this.ToggleDetails();
            }
        }

        private void Expand()
        {
            if (this.expanded) return;
            this.expanded = true;
            this.ResizeKeepingCenter(this.detailSize);
            this.UpdateDetailMenuText();
        }

        private void Collapse()
        {
            if (!this.expanded) return;
            this.expanded = false;
            this.ResizeKeepingCenter(this.CurrentMiniSize());
            this.UpdateDetailMenuText();
        }

        private void ToggleDetails()
        {
            this.autoCollapseTimer.Stop();
            if (this.expanded) this.Collapse();
            else this.Expand();
            this.BringToFront();
        }

        private void QueueAutoCollapse()
        {
            if (!this.expanded || this.dragging) return;
            this.autoCollapseTimer.Stop();
            this.autoCollapseTimer.Start();
        }

        private void UpdateDetailMenuText()
        {
            if (this.detailMenuItem != null) this.detailMenuItem.Text = this.expanded ? T.Text("隐藏详情") : T.Text("显示详情");
        }

        public Point LocationForSave()
        {
            if (!this.expanded)
            {
                return new Point(this.Left, this.Top + this.MiniTopPad());
            }
            return new Point(this.Left + (this.Width - this.miniSize.Width) / 2, this.Top + (this.Height - this.miniSize.Height) / 2);
        }

        private Size CurrentMiniSize()
        {
            return this.refreshing ? this.loadingMiniSize : this.miniSize;
        }

        private int MiniTopPad()
        {
            return this.refreshing && !this.expanded ? LoadingTextPad : 0;
        }

        private void ResizeMiniForState(Point ballLocation)
        {
            var size = this.CurrentMiniSize();
            var pad = this.MiniTopPad();
            var area = this.WorkingAreaForPoint(new Point(ballLocation.X + this.miniSize.Width / 2, ballLocation.Y + this.miniSize.Height / 2));
            var location = this.ClampLocation(ballLocation.X, ballLocation.Y - pad, size, area, 0);
            if (this.Bounds != new Rectangle(location, size)) this.Bounds = new Rectangle(location, size);
        }

        private void ResizeKeepingCenter(Size newSize)
        {
            var center = new Point(this.Left + this.Width / 2, this.Top + this.Height / 2);
            var area = this.WorkingAreaForPoint(center);
            var x = center.X - newSize.Width / 2;
            var y = center.Y - newSize.Height / 2;
            var location = this.ClampLocation(x, y, newSize, area, 4);
            this.Bounds = new Rectangle(location, newSize);
            this.Invalidate();
        }

        private Rectangle WorkingAreaForPoint(Point point)
        {
            return Screen.FromPoint(point).WorkingArea;
        }

        private Point ClampLocation(int x, int y, Size size, Rectangle area, int margin)
        {
            var minX = area.Left + margin;
            var minY = area.Top + margin;
            var maxX = area.Right - size.Width - margin;
            var maxY = area.Bottom - size.Height - margin;
            if (maxX < minX) maxX = minX;
            if (maxY < minY) maxY = minY;
            return new Point(Math.Max(minX, Math.Min(maxX, x)), Math.Max(minY, Math.Min(maxY, y)));
        }

        private UsageWindow CurrentUsage()
        {
            return this.CurrentScrollItem() == "weekly" ? this.snapshot.Usage.Weekly : this.snapshot.Usage.FiveHour;
        }

        private string CurrentScrollItem()
        {
            var items = this.ScrollItems();
            if (this.messageIndex >= items.Count) this.messageIndex = 0;
            return items[this.messageIndex];
        }

        private string MiniErrorText()
        {
            var code = this.snapshot == null ? "" : (this.snapshot.ErrorCode ?? "");
            if (string.Equals(code, "ENV_HIGH_RISK", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "ENV_MEDIUM_RISK_PENDING", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "ENV_INITIAL_TRUST_PENDING", StringComparison.OrdinalIgnoreCase))
            {
                return T.Text("env_mini_risk");
            }
            if (string.Equals(code, "ENV_CHECK_FAILED", StringComparison.OrdinalIgnoreCase))
            {
                return T.Text("env_mini_check_failed");
            }
            return Trim(this.snapshot == null ? "" : this.snapshot.ErrorMessage, 10);
        }

        private List<string> ScrollItems()
        {
            var items = new List<string>();
            if (this.behaviorConfig.ScrollShowFiveHour) items.Add("5h");
            if (this.behaviorConfig.ScrollShowWeekly) items.Add("weekly");
            if (this.behaviorConfig.ScrollShowGpt55XHigh) items.Add("iq0");
            if (this.behaviorConfig.ScrollShowGpt55High) items.Add("iq1");
            if (this.behaviorConfig.ScrollShowGpt55Medium) items.Add("iq2");
            if (this.behaviorConfig.ScrollShowGpt54XHigh) items.Add("iq3");
            if (items.Count == 0) items.Add("5h");
            return items;
        }

        private ModelIqScore CurrentIqScore(string item)
        {
            int index;
            switch (item)
            {
                case "iq1": index = 1; break;
                case "iq2": index = 2; break;
                case "iq3": index = 3; break;
                default: index = 0; break;
            }
            var radar = this.snapshot.ModelIq ?? ModelIqRadar.Empty();
            return index < radar.Scores.Count ? radar.Scores[index] : new ModelIqScore("--");
        }

        private void DrawMini(Graphics g)
        {
            if (this.refreshing)
            {
                var ballBounds = new RectangleF(0f, LoadingTextPad, this.miniSize.Width, this.miniSize.Height);
                this.DrawLoadingMini(g, ballBounds);
                this.DrawCurvedStatusText(g, string.IsNullOrWhiteSpace(this.statusText) ? T.Text("更新中省略") : this.statusText, ballBounds);
                return;
            }

            var rect = new RectangleF(3.5f, 3.5f, this.Width - 7f, this.Height - 7f);
            using (var bg = new LinearGradientBrush(rect, this.theme.BackTop, this.theme.BackBottom, 90f))
            {
                g.FillEllipse(bg, rect);
            }
            using (var gloss = new Pen(Color.FromArgb(58, 255, 255, 255), 1.2f))
            {
                g.DrawArc(gloss, 10.5f, 9.5f, this.Width - 21f, this.Height - 21f, 206, 126);
            }

            if (this.snapshot.ErrorMessage != null)
            {
                this.DrawCenteredText(g, "Codex", new Font("Segoe UI Semibold", 10f), this.theme.Muted, new RectangleF(0, 18, this.Width, 20));
                this.DrawCenteredText(g, this.MiniErrorText(), new Font("Segoe UI Semibold", 10.2f), this.theme.Text, new RectangleF(6, 37, this.Width - 12, 28));
                this.DrawCenteredText(g, T.Text("error_click_details"), new Font("Segoe UI", 8.2f), this.theme.Muted, new RectangleF(0, 64, this.Width, 18));
                using (var errPen = new Pen(Color.FromArgb(235, 235, 86, 86), 6f))
                {
                    errPen.StartCap = LineCap.Round;
                    errPen.EndCap = LineCap.Round;
                    g.DrawEllipse(errPen, 6.5f, 6.5f, this.Width - 13f, this.Height - 13f);
                }
                return;
            }

            var item = this.CurrentScrollItem();
            if (item.StartsWith("iq", StringComparison.OrdinalIgnoreCase))
            {
                this.DrawMiniIq(g, this.CurrentIqScore(item));
                return;
            }

            var usage = this.CurrentUsage();
            var pct = usage.Available ? usage.RemainingPercent : 0.0;
            var waterCircle = new Rectangle(12, 12, 72, 72);
            this.DrawWater(g, waterCircle, pct);
            this.DrawMiniCountdownRing(g, usage);

            this.DrawMiniText(g, usage.Label, new Font("Segoe UI Semibold", 10f), this.theme.Muted, new RectangleF(0, 15, this.Width, 18));
            this.DrawMiniText(g, usage.Available ? pct.ToString("0", CultureInfo.InvariantCulture) + "%" : "--", new Font("Segoe UI Semibold", 19f), this.theme.Text, new RectangleF(0, 29, this.Width, 38));
            this.DrawMiniText(g, this.ShortResetText(usage), new Font("Segoe UI", 7.7f), this.theme.Muted, new RectangleF(0, 65, this.Width, 15));
        }

        private void DrawMiniIq(Graphics g, ModelIqScore score)
        {
            this.DrawMiniIqRing(g);
            var label = score == null ? "--" : score.Label;
            if (label.StartsWith("GPT-", StringComparison.OrdinalIgnoreCase)) label = label.Substring(4);
            this.DrawMiniText(g, "IQ", new Font("Segoe UI Semibold", 9.5f), this.theme.Muted, new RectangleF(0, 16, this.Width, 17));
            this.DrawMiniText(g, score != null && score.Available ? score.Score.ToString("0.#", CultureInfo.InvariantCulture) : "--", new Font("Segoe UI Semibold", 19f), this.theme.Text, new RectangleF(0, 29, this.Width, 38));
            this.DrawMiniText(g, label, new Font("Segoe UI", 7.4f), this.theme.Muted, new RectangleF(3, 65, this.Width - 6, 15));
        }

        private void DrawMiniIqRing(Graphics g)
        {
            const int scale = 3;
            using (var layer = new Bitmap(this.Width * scale, this.Height * scale, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (var lg = Graphics.FromImage(layer))
                {
                    ConfigureGraphics(lg);
                    lg.Clear(Color.Transparent);
                    lg.ScaleTransform(scale, scale);
                    var outer = new RectangleF(4.0f, 4.0f, this.Width - 8.0f, this.Height - 8.0f);
                    var inner = new RectangleF(10.8f, 10.8f, this.Width - 21.6f, this.Height - 21.6f);
                    using (var ring = new SolidBrush(Color.FromArgb(198, this.theme.Accent.R, this.theme.Accent.G, this.theme.Accent.B)))
                    {
                        this.FillDonut(lg, outer, inner, ring);
                    }
                    using (var sheen = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
                    {
                        this.FillDonut(lg, outer, inner, sheen);
                    }
                }
                g.DrawImage(layer, new Rectangle(0, 0, this.Width, this.Height));
            }
        }

        private void DrawLoadingMini(Graphics g, RectangleF ballBounds)
        {
            var pulseAmount = (1.0 + Math.Sin(this.loadingPhase * Math.PI / 90.0)) / 2.0;
            using (var bg = new LinearGradientBrush(ballBounds, this.theme.BackTop, this.theme.BackBottom, 90f))
            {
                g.FillEllipse(bg, ballBounds);
            }

            const int scale = 3;
            using (var layer = new Bitmap(this.miniSize.Width * scale, this.miniSize.Height * scale, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (var lg = Graphics.FromImage(layer))
                {
                    ConfigureGraphics(lg);
                    lg.Clear(Color.Transparent);
                    lg.ScaleTransform(scale, scale);

                    var outer = new RectangleF(4.0f, 4.0f, this.miniSize.Width - 8.0f, this.miniSize.Height - 8.0f);
                    var inner = new RectangleF(10.8f, 10.8f, this.miniSize.Width - 21.6f, this.miniSize.Height - 21.6f);
                    var alpha = 132 + (int)(72 * pulseAmount);
                    using (var ring = new SolidBrush(Color.FromArgb(alpha, this.theme.Accent.R, this.theme.Accent.G, this.theme.Accent.B)))
                    {
                        this.FillDonut(lg, outer, inner, ring);
                    }
                    using (var sheen = new SolidBrush(Color.FromArgb(18 + (int)(28 * pulseAmount), 255, 255, 255)))
                    {
                        this.FillDonut(lg, outer, inner, sheen);
                    }
                }
                g.DrawImage(layer, Rectangle.Round(ballBounds));
            }
            this.DrawLoadingCenterPulse(g, pulseAmount, ballBounds);
        }

        private void DrawLoadingCenterPulse(Graphics g, double pulseAmount, RectangleF ballBounds)
        {
            var center = new PointF(ballBounds.Left + ballBounds.Width / 2f, ballBounds.Top + ballBounds.Height / 2f);
            var glowRadius = 25.5f + (float)(5.5 * pulseAmount);
            var glowRect = new RectangleF(center.X - glowRadius, center.Y - glowRadius, glowRadius * 2f, glowRadius * 2f);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(glowRect);
                using (var glow = new PathGradientBrush(path))
                {
                    glow.CenterColor = Color.FromArgb(92 + (int)(58 * pulseAmount), this.theme.Accent.R, this.theme.Accent.G, this.theme.Accent.B);
                    glow.SurroundColors = new[] { Color.FromArgb(0, this.theme.Accent.R, this.theme.Accent.G, this.theme.Accent.B) };
                    g.FillPath(glow, path);
                }
            }

            var coreRadius = 15.5f + (float)(3.5 * pulseAmount);
            var coreRect = new RectangleF(center.X - coreRadius, center.Y - coreRadius, coreRadius * 2f, coreRadius * 2f);
            using (var core = new SolidBrush(Color.FromArgb(54 + (int)(42 * pulseAmount), 255, 255, 255)))
            {
                g.FillEllipse(core, coreRect);
            }
            var accentRadius = 9.5f + (float)(2.5 * pulseAmount);
            using (var accent = new SolidBrush(Color.FromArgb(82 + (int)(76 * pulseAmount), this.theme.Accent.R, this.theme.Accent.G, this.theme.Accent.B)))
            {
                g.FillEllipse(accent, center.X - accentRadius, center.Y - accentRadius, accentRadius * 2f, accentRadius * 2f);
            }
        }

        private void DrawCurvedStatusText(Graphics g, string text, RectangleF ballBounds)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            if (text.Length > 14) text = Trim(text, 14);

            var fontSize = text.Length > 10 ? 6.8f : 7.6f;
            using (var font = new Font("Segoe UI Semibold", fontSize, FontStyle.Regular, GraphicsUnit.Point))
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
            using (var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            using (var fill = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            {
                format.FormatFlags |= StringFormatFlags.NoClip;
                var radius = ballBounds.Width / 2f + 8.5f;
                var spacing = 0.85f;
                var widths = new float[text.Length];
                var total = 0f;
                for (int i = 0; i < text.Length; i++)
                {
                    widths[i] = Math.Max(3.2f, g.MeasureString(text[i].ToString(), font, PointF.Empty, format).Width);
                    total += widths[i] + spacing;
                }

                var maxArc = radius * 2.28f;
                var fitScale = total > maxArc ? maxArc / total : 1f;
                var angle = -Math.PI / 2.0 - (total * fitScale / radius) / 2.0;
                var center = new PointF(ballBounds.Left + ballBounds.Width / 2f, ballBounds.Top + ballBounds.Height / 2f);
                var height = font.GetHeight(g);

                for (int i = 0; i < text.Length; i++)
                {
                    var charArc = (widths[i] + spacing) * fitScale / radius;
                    angle += charArc / 2.0;
                    var x = center.X + (float)(Math.Cos(angle) * radius);
                    var y = center.Y + (float)(Math.Sin(angle) * radius);
                    var state = g.Save();
                    g.TranslateTransform(x, y);
                    g.RotateTransform((float)(angle * 180.0 / Math.PI + 90.0));
                    g.DrawString(text[i].ToString(), font, shadow, -widths[i] / 2f + 0.55f, -height / 2f + 0.9f, format);
                    g.DrawString(text[i].ToString(), font, fill, -widths[i] / 2f, -height / 2f, format);
                    g.Restore(state);
                    angle += charArc / 2.0;
                }
            }
        }

        private void DrawMiniCountdownRing(Graphics g, UsageWindow usage)
        {
            const int scale = 3;
            using (var layer = new Bitmap(this.Width * scale, this.Height * scale, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (var lg = Graphics.FromImage(layer))
                {
                    ConfigureGraphics(lg);
                    lg.Clear(Color.Transparent);
                    lg.ScaleTransform(scale, scale);

                    var outer = new RectangleF(4.0f, 4.0f, this.Width - 8.0f, this.Height - 8.0f);
                    var inner = new RectangleF(10.8f, 10.8f, this.Width - 21.6f, this.Height - 21.6f);

                    var state = this.CountdownColor(usage);
                    using (var stateBrush = new SolidBrush(Color.FromArgb(205, state.R, state.G, state.B)))
                    {
                        this.FillDonut(lg, outer, inner, stateBrush);
                    }
                }
                g.DrawImage(layer, new Rectangle(0, 0, this.Width, this.Height));
            }
        }

        private void FillDonut(Graphics g, RectangleF outer, RectangleF inner, Brush brush)
        {
            using (var path = new GraphicsPath(FillMode.Alternate))
            {
                path.AddEllipse(outer);
                path.AddEllipse(inner);
                g.FillPath(brush, path);
            }
        }

        private void DrawDetail(Graphics g)
        {
            var bounds = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            using (var path = RoundRect(bounds, 24))
            using (var bg = new LinearGradientBrush(bounds, this.theme.PanelTop, this.theme.PanelBottom, 90f))
            {
                g.FillPath(bg, path);
            }
            using (var pen = new Pen(Color.FromArgb(105, 255, 255, 255)))
            using (var path = RoundRect(bounds, 24))
            {
                g.DrawPath(pen, path);
            }

            using (var accent = new SolidBrush(this.theme.Accent))
            using (var titleFont = new Font("Segoe UI Semibold", 12f))
            using (var topUpdateFont = new Font("Segoe UI", 9f))
            using (var bodyFont = new Font("Segoe UI Semibold", 11.2f))
            using (var smallFont = new Font("Segoe UI Semibold", 9.8f))
            using (var titleTextBrush = new SolidBrush(this.theme.Text))
            using (var topMutedBrush = new SolidBrush(this.theme.Muted))
            using (var detailTextBrush = new SolidBrush(EmphasizeDetailTextColor(this.theme.Text)))
            using (var muted = new SolidBrush(EmphasizeDetailMutedColor(this.theme.Muted, this.theme.Text)))
            {
                var contentWidth = 424;
                var contentLeft = (this.Width - contentWidth) / 2;
                var titleText = T.Text("详情标题");
                g.FillEllipse(accent, 20, 22, 10, 10);
                this.SafeDrawString(g, titleText, titleFont, titleTextBrush, new RectangleF(38, 14, this.Width - 230, 24));
                this.SafeDrawString(g, T.Text("更新") + " " + this.snapshot.UpdatedAt.ToString("HH:mm:ss"), topUpdateFont, topMutedBrush, new RectangleF(this.Width - 168, 18, 140, 18), StringAlignment.Far);
                if (this.behaviorConfig.ShowUserNameInDetails && !string.IsNullOrWhiteSpace(this.snapshot.Usage.UserName))
                {
                    this.SafeDrawString(g, T.Text("账户标签") + " " + this.snapshot.Usage.UserName, smallFont, muted, new RectangleF(38, 38, this.Width - 76, 18));
                }

                if (this.snapshot.ErrorMessage != null)
                {
                    this.SafeDrawString(g, T.Text("连接失败"), bodyFont, detailTextBrush, new RectangleF(contentLeft, 68, contentWidth, 24));
                    this.SafeDrawString(g, this.snapshot.ErrorMessage, bodyFont, detailTextBrush, new RectangleF(contentLeft, 94, contentWidth, 24));
                    this.SafeDrawString(g, T.Text("错误代码") + ": " + (this.snapshot.ErrorCode ?? "--"), smallFont, muted, new RectangleF(contentLeft, 118, contentWidth, 20));
                    this.SafeDrawString(g, Trim(this.snapshot.ErrorDetail ?? "", 150), smallFont, muted, new RectangleF(contentLeft, 144, contentWidth, 80));
                    this.SafeDrawString(g, T.Text("查看错误日志提示"), smallFont, detailTextBrush, new RectangleF(0, 238, this.Width, 18), true);
                    return;
                }

                var leftCircle = new Rectangle(contentLeft + 20, 86, 112, 112);
                var usageX = contentLeft + 164;
                var usageBarWidth = contentWidth - 164;
                this.DrawWater(g, leftCircle, this.CurrentUsage().RemainingPercent);
                using (var baseRing = new Pen(Color.FromArgb(46, 255, 255, 255), 7f))
                {
                    baseRing.StartCap = LineCap.Round;
                    baseRing.EndCap = LineCap.Round;
                    g.DrawEllipse(baseRing, leftCircle.Left - 5.5f, leftCircle.Top - 5.5f, leftCircle.Width + 11f, leftCircle.Height + 11f);
                }
                using (var ring = new Pen(this.CountdownColor(this.CurrentUsage()), 7f))
                {
                    ring.StartCap = LineCap.Round;
                    ring.EndCap = LineCap.Round;
                    ring.LineJoin = LineJoin.Round;
                    g.DrawArc(ring, leftCircle.Left - 5.5f, leftCircle.Top - 5.5f, leftCircle.Width + 11f, leftCircle.Height + 11f, -90, (float)(360.0 * this.ResetRatio(this.CurrentUsage())));
                }
                this.DrawCenteredText(g, this.CurrentUsage().Label, new Font("Segoe UI Semibold", 11f), this.theme.Muted, new RectangleF(leftCircle.Left, leftCircle.Top + 22, leftCircle.Width, 22));
                this.DrawCenteredText(g, this.CurrentUsage().RemainingPercent.ToString("0", CultureInfo.InvariantCulture) + "%", new Font("Segoe UI Semibold", 24f), this.theme.Text, new RectangleF(leftCircle.Left, leftCircle.Top + 46, leftCircle.Width, 38));

                var y = 86;
                this.DrawUsageLine(g, this.snapshot.Usage.FiveHour, usageX, y, usageBarWidth, bodyFont, smallFont, false); y += 55;
                this.DrawUsageLine(g, this.snapshot.Usage.Weekly, usageX, y, usageBarWidth, bodyFont, smallFont, true); y += 61;

                var cardX = contentLeft;
                var cardWidth = contentWidth;
                var cardCount = Math.Max(1, this.snapshot.ResetCards.Expirations.Count);
                var iqTop = this.Height - 14 - 38;
                var cardYStart = iqTop - 12 - 18 - (cardCount - 1) * 23;
                cardYStart = Math.Max(y + 18, cardYStart);
                if (this.snapshot.ResetCards.Expirations.Count == 0)
                {
                    this.SafeDrawString(g, "--", smallFont, muted, new RectangleF(cardX, cardYStart, cardWidth, 18), StringAlignment.Center);
                }
                else
                {
                    var resetCardLines = new List<string>();
                    var resetCardTextWidth = 0f;
                    for (int i = 0; i < this.snapshot.ResetCards.Expirations.Count; i++)
                    {
                        var line = T.Text("重置卡") + " " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + DateText.ResetDescriptionDetailed(this.snapshot.ResetCards.Expirations[i]);
                        resetCardLines.Add(line);
                        resetCardTextWidth = Math.Max(resetCardTextWidth, g.MeasureString(line, smallFont).Width);
                    }

                    var resetCardGroupWidth = Math.Min(cardWidth, (int)Math.Ceiling(resetCardTextWidth) + 2);
                    var resetCardGroupX = cardX + (cardWidth - resetCardGroupWidth) / 2;
                    var cardY = cardYStart;
                    for (int i = 0; i < resetCardLines.Count; i++)
                    {
                        this.SafeDrawString(g, resetCardLines[i], smallFont, muted, new RectangleF(resetCardGroupX, cardY, resetCardGroupWidth, 18), StringAlignment.Near);
                        cardY += 23;
                    }
                }
                this.DrawModelIqRow(g, contentLeft, iqTop, contentWidth, 38, smallFont, detailTextBrush, muted);
            }
        }

        private void DrawModelIqRow(Graphics g, int x, int y, int width, int height, Font font, Brush text, Brush muted)
        {
            var radar = this.snapshot.ModelIq ?? ModelIqRadar.Empty();
            var gap = 8;
            var cellWidth = (width - gap * 3) / 4;
            using (var border = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
            using (var labelFont = new Font("Segoe UI Semibold", 7.8f))
            using (var scoreFont = new Font("Segoe UI Semibold", 11.4f, FontStyle.Bold))
            {
                for (int i = 0; i < 4; i++)
                {
                    var score = i < radar.Scores.Count ? radar.Scores[i] : new ModelIqScore("--");
                    var left = x + i * (cellWidth + gap);
                    var rect = new Rectangle(left, y, cellWidth, height);
                    using (var path = RoundRect(rect, 8))
                    using (var fill = new LinearGradientBrush(rect, ModelIqTopColor(i), ModelIqBottomColor(i), LinearGradientMode.Vertical))
                    {
                        g.FillPath(fill, path);
                        using (var sheen = new LinearGradientBrush(rect, Color.FromArgb(44, 255, 255, 255), Color.FromArgb(5, 255, 255, 255), LinearGradientMode.Vertical))
                        {
                            g.FillPath(sheen, path);
                        }
                        g.DrawPath(border, path);
                    }

                    this.SafeDrawString(g, score.Label, labelFont, muted, new RectangleF(left + 5, y + 3, cellWidth - 10, 14), true);
                    this.SafeDrawString(g, score.Available ? score.Score.ToString("0.#", CultureInfo.InvariantCulture) : "--", scoreFont, text, new RectangleF(left + 5, y + 17, cellWidth - 10, 19), true);
                }
            }
        }

        private static Color ModelIqTopColor(int index)
        {
            switch (index)
            {
                case 0: return Color.FromArgb(128, 123, 92, 224);
                case 1: return Color.FromArgb(128, 34, 151, 132);
                case 2: return Color.FromArgb(128, 79, 126, 206);
                default: return Color.FromArgb(128, 179, 122, 62);
            }
        }

        private static Color ModelIqBottomColor(int index)
        {
            switch (index)
            {
                case 0: return Color.FromArgb(178, 90, 60, 181);
                case 1: return Color.FromArgb(178, 20, 105, 94);
                case 2: return Color.FromArgb(178, 46, 83, 160);
                default: return Color.FromArgb(178, 129, 83, 40);
            }
        }

        private static Color EmphasizeDetailTextColor(Color color)
        {
            return Color.FromArgb(Math.Max((int)color.A, 245), color.R, color.G, color.B);
        }

        private static Color EmphasizeDetailMutedColor(Color muted, Color text)
        {
            return Color.FromArgb(
                Math.Max((int)muted.A, 238),
                BlendChannel(muted.R, text.R, 0.32),
                BlendChannel(muted.G, text.G, 0.32),
                BlendChannel(muted.B, text.B, 0.32));
        }

        private static int BlendChannel(int from, int to, double ratio)
        {
            return Math.Max(0, Math.Min(255, (int)Math.Round(from + (to - from) * ratio)));
        }

        private void DrawUsageLine(Graphics g, UsageWindow usage, int x, int y, int barWidth, Font bodyFont, Font smallFont, bool detailedReset)
        {
            if (usage == null) usage = UsageWindow.Unavailable("--");
            barWidth = Math.Max(80, barWidth);
            using (var text = new SolidBrush(this.theme.Text))
            using (var muted = new SolidBrush(this.theme.Muted))
            using (var fill = new SolidBrush(Color.FromArgb(170, this.theme.Accent)))
            using (var barBack = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
            {
                var pct = usage.Available ? usage.RemainingPercent : 0.0;
                if (double.IsNaN(pct) || double.IsInfinity(pct)) pct = 0.0;
                pct = Math.Max(0.0, Math.Min(100.0, pct));
                this.SafeDrawString(g, usage.Label + "  " + T.Text("剩余") + " " + pct.ToString("0.0", CultureInfo.InvariantCulture) + "%", bodyFont, text, new RectangleF(x, y, barWidth, 22));
                g.FillRectangle(barBack, x, y + 25, barWidth, 5);
                g.FillRectangle(fill, x, y + 25, (int)Math.Round(barWidth * pct / 100.0), 5);
                this.SafeDrawString(g, detailedReset ? DateText.ResetDescriptionDetailed(usage.ResetAtLocal) : DateText.ResetDescription(usage.ResetAtLocal), smallFont, muted, new RectangleF(x, y + 32, barWidth, 18));
            }
        }

        private void SafeDrawString(Graphics g, string value, Font font, Brush brush, RectangleF bounds)
        {
            this.SafeDrawString(g, value, font, brush, bounds, StringAlignment.Near);
        }

        private void SafeDrawString(Graphics g, string value, Font font, Brush brush, RectangleF bounds, bool centered)
        {
            this.SafeDrawString(g, value, font, brush, bounds, centered ? StringAlignment.Center : StringAlignment.Near);
        }

        private void SafeDrawString(Graphics g, string value, Font font, Brush brush, RectangleF bounds, StringAlignment alignment)
        {
            this.SafeDrawString(g, value, font, brush, bounds, alignment, StringAlignment.Near);
        }

        private void SafeDrawString(Graphics g, string value, Font font, Brush brush, RectangleF bounds, StringAlignment alignment, StringAlignment lineAlignment)
        {
            if (g == null || font == null || brush == null || bounds.Width <= 0f || bounds.Height <= 0f) return;
            var text = CleanDrawText(value);
            if (text.Length == 0) return;
            try
            {
                using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    format.Alignment = alignment;
                    format.LineAlignment = lineAlignment;
                    g.DrawString(text, font, brush, bounds, format);
                }
            }
            catch (ArgumentException)
            {
                var fallback = CleanDrawText(Trim(text, 48));
                if (fallback.Length == 0) return;
                var color = brush is SolidBrush ? ((SolidBrush)brush).Color : this.theme.Text;
                var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                if (lineAlignment == StringAlignment.Center) flags |= TextFormatFlags.VerticalCenter;
                else if (lineAlignment == StringAlignment.Far) flags |= TextFormatFlags.Bottom;
                else flags |= TextFormatFlags.Top;
                if (alignment == StringAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
                else if (alignment == StringAlignment.Far) flags |= TextFormatFlags.Right;
                else flags |= TextFormatFlags.Left;
                TextRenderer.DrawText(g, fallback, font, Rectangle.Round(bounds), color, flags);
            }
        }

        private static string CleanDrawText(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (char.IsSurrogate(ch))
                {
                    if (i + 1 < value.Length && char.IsSurrogatePair(ch, value[i + 1]))
                    {
                        sb.Append(ch);
                        sb.Append(value[++i]);
                    }
                    continue;
                }
                if (!char.IsControl(ch) || ch == '\t') sb.Append(ch);
            }
            return sb.ToString();
        }

        private void DrawWater(Graphics g, Rectangle circle, double percent)
        {
            percent = Math.Max(0.0, Math.Min(100.0, percent));
            const int scale = 3;
            using (var layer = new Bitmap(circle.Width * scale, circle.Height * scale, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (var lg = Graphics.FromImage(layer))
                {
                    ConfigureGraphics(lg);
                    lg.Clear(Color.Transparent);
                    lg.ScaleTransform(scale, scale);

                    var localCircle = new RectangleF(0.8f, 0.8f, circle.Width - 1.6f, circle.Height - 1.6f);
                    using (var clipPath = new GraphicsPath())
                    {
                        clipPath.AddEllipse(localCircle);
                        lg.SetClip(clipPath, CombineMode.Replace);

                        using (var back = new LinearGradientBrush(localCircle, Color.FromArgb(26, 255, 255, 255), Color.FromArgb(8, 255, 255, 255), 90f))
                        {
                            lg.FillEllipse(back, localCircle);
                        }

                        var waterTop = (float)(circle.Height - circle.Height * percent / 100.0);
                        using (var wave = new GraphicsPath())
                        {
                            var points = new List<PointF>();
                            for (float x = -8f; x <= circle.Width + 8f; x += 1.25f)
                            {
                                var y = waterTop + (float)Math.Sin((x * 0.12f) + this.wavePhase) * 3.2f;
                                points.Add(new PointF(x, y));
                            }
                            wave.AddLines(points.ToArray());
                            wave.AddLine(circle.Width + 8f, circle.Height + 8f, -8f, circle.Height + 8f);
                            wave.CloseFigure();

                            using (var brush = new LinearGradientBrush(new RectangleF(0, 0, circle.Width, circle.Height), this.theme.WaterTop, this.theme.WaterBottom, 90f))
                            {
                                lg.FillPath(brush, wave);
                            }
                            using (var sheen = new Pen(Color.FromArgb(50, 255, 255, 255), 1.1f))
                            {
                                lg.DrawPath(sheen, wave);
                            }
                        }
                    }
                }

                g.DrawImage(layer, circle);
            }

            using (var innerEdge = new Pen(Color.FromArgb(42, 255, 255, 255), 1.1f))
            {
                g.DrawEllipse(innerEdge, circle.Left + 0.5f, circle.Top + 0.5f, circle.Width - 1f, circle.Height - 1f);
            }
        }

        private double ResetRatio(UsageWindow usage)
        {
            if (usage == null || !usage.ResetAtLocal.HasValue || usage.WindowSeconds <= 0) return 1.0;
            var seconds = (usage.ResetAtLocal.Value - DateTime.Now).TotalSeconds;
            return Math.Max(0.0, Math.Min(1.0, seconds / usage.WindowSeconds));
        }

        private Color CountdownColor(UsageWindow usage)
        {
            var ratio = this.ResetRatio(usage);
            if (ratio <= 0.18) return Color.FromArgb(245, 82, 82);
            if (ratio <= 0.40) return Color.FromArgb(255, 155, 64);
            if (ratio <= 0.65) return Color.FromArgb(250, 212, 82);
            return this.theme.CountdownSafe;
        }

        private string ShortResetText(UsageWindow usage)
        {
            if (usage == null || !usage.ResetAtLocal.HasValue) return "--";
            var remaining = usage.ResetAtLocal.Value - DateTime.Now;
            if (remaining.TotalSeconds < 0) remaining = TimeSpan.Zero;
            if (remaining.TotalDays >= 1) return ((int)Math.Floor(remaining.TotalDays)).ToString(CultureInfo.InvariantCulture) + "d " + remaining.Hours.ToString("00", CultureInfo.InvariantCulture) + "h";
            return ((int)Math.Floor(remaining.TotalHours)).ToString(CultureInfo.InvariantCulture) + "h " + remaining.Minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
        }

        private void DrawCenteredText(Graphics g, string text, Font font, Color color, RectangleF bounds)
        {
            using (font)
            using (var brush = new SolidBrush(color))
            {
                this.SafeDrawString(g, text, font, brush, bounds, true);
            }
        }

        private void DrawMiniText(Graphics g, string text, Font font, Color color, RectangleF bounds)
        {
            using (font)
            using (var brush = new SolidBrush(color))
            {
                this.SafeDrawString(g, text, font, brush, bounds, StringAlignment.Center, StringAlignment.Center);
            }
        }

        private static void Raise(EventHandler handler)
        {
            if (handler != null) handler(null, EventArgs.Empty);
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max - 1) + "...";
        }

        internal static GraphicsPath RoundRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class DetailPopupForm : Form
    {
        public event EventHandler MouseEnteredPopup;
        public event EventHandler MouseLeftPopup;
        private MonitorSnapshot snapshot = MonitorSnapshot.Loading();

        public DetailPopupForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.DoubleBuffered = true;
            this.Size = new Size(610, 230);
            this.BackColor = Color.FromArgb(18, 20, 24);
            this.Font = new Font("Segoe UI", 9f);
            this.MouseEnter += delegate { Raise(this.MouseEnteredPopup); };
            this.MouseLeave += delegate { Raise(this.MouseLeftPopup); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;
                cp.ClassStyle |= 0x20000;
                return cp;
            }
        }

        public void SetSnapshot(MonitorSnapshot snapshot)
        {
            this.snapshot = snapshot;
            this.Invalidate();
        }

        public void PlaceNear(Form bar)
        {
            var area = Screen.FromControl(bar).WorkingArea;
            var x = Math.Max(area.Left + 8, Math.Min(area.Right - this.Width - 8, bar.Left));
            var y = bar.Top - this.Height - 10;
            if (y < area.Top + 8) y = bar.Bottom + 10;
            this.Location = new Point(x, y);
        }

        public bool ContainsCursor()
        {
            return this.Visible && this.ClientRectangle.Contains(this.PointToClient(Cursor.Position));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var path = MiniBarForm.RoundRect(new Rectangle(0, 0, this.Width, this.Height), 18))
            {
                this.Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new LinearGradientBrush(this.ClientRectangle, Color.FromArgb(28, 32, 40), Color.FromArgb(8, 92, 76), 0f))
            {
                g.FillRectangle(bg, this.ClientRectangle);
            }
            using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255)))
            using (var path = MiniBarForm.RoundRect(new Rectangle(0, 0, this.Width - 1, this.Height - 1), 18))
            {
                g.DrawPath(pen, path);
            }
            using (var accent = new SolidBrush(Color.FromArgb(58, 217, 169)))
            using (var white = new SolidBrush(Color.White))
            using (var muted = new SolidBrush(Color.FromArgb(215, 230, 236, 240)))
            using (var title = new Font("Segoe UI Semibold", 12f))
            using (var body = new Font("Segoe UI Semibold", 10.5f))
            using (var small = new Font("Segoe UI", 9f))
            {
                g.FillEllipse(accent, 20, 21, 10, 10);
                g.DrawString(T.Text("详情标题"), title, white, 39, 13);
                g.DrawString(T.Text("更新") + " " + this.snapshot.UpdatedAt.ToString("HH:mm:ss"), small, muted, this.Width - 140, 17);

                if (this.snapshot.ErrorMessage != null)
                {
                    g.DrawString(T.Text("连接失败"), body, white, 24, 58);
                    g.DrawString(this.snapshot.ErrorMessage, body, white, 24, 88);
                    g.DrawString(T.Text("错误代码") + ": " + (this.snapshot.ErrorCode ?? "--"), small, muted, 24, 114);
                    g.DrawString(Trim(this.snapshot.ErrorDetail ?? "", 150), small, muted, new RectangleF(24, 138, this.Width - 48, 56));
                    g.DrawString(T.Text("查看错误日志提示"), small, white, 24, 198);
                    return;
                }

                var y = 58;
                g.DrawString(this.snapshot.Usage.FiveHour.DetailLine(), body, white, 24, y); y += 30;
                g.DrawString(this.snapshot.Usage.Weekly.DetailLine(), body, white, 24, y); y += 38;
                g.DrawString(T.Text("重置卡"), body, white, 24, y); y += 28;
                if (this.snapshot.ResetCards.Expirations.Count == 0)
                {
                    g.DrawString("--", small, muted, 34, y);
                }
                else
                {
                    for (int i = 0; i < this.snapshot.ResetCards.Expirations.Count; i++)
                    {
                        var line = T.Text("重置卡") + " " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + DateText.ResetDescription(this.snapshot.ResetCards.Expirations[i]);
                        g.DrawString(line, small, muted, 34, y);
                        y += 24;
                    }
                }
            }
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max - 1) + "...";
        }

        private static void Raise(EventHandler handler)
        {
            if (handler != null) handler(null, EventArgs.Empty);
        }
    }

    internal sealed class TrustedEnvironmentsForm : Form
    {
        private readonly ListView list = new ListView();
        private readonly Button deleteButton = new Button();
        public List<TrustedEnvironment> TrustedEnvironments;

        public TrustedEnvironmentsForm(List<TrustedEnvironment> environments)
        {
            this.TrustedEnvironments = new List<TrustedEnvironment>();
            if (environments != null)
            {
                foreach (var item in environments)
                {
                    if (item != null) this.TrustedEnvironments.Add(item.Clone());
                }
            }

            this.Text = T.Text("env_trusted_library");
            this.Size = new Size(720, 420);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(14);
            layout.RowCount = 3;
            layout.ColumnCount = 1;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            this.Controls.Add(layout);

            var hint = new Label();
            hint.Text = T.Text("env_trusted_library_hint");
            hint.Dock = DockStyle.Fill;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(hint, 0, 0);

            this.list.Dock = DockStyle.Fill;
            this.list.View = View.Details;
            this.list.FullRowSelect = true;
            this.list.MultiSelect = true;
            this.list.HideSelection = false;
            this.list.Columns.Add("IP", 150);
            this.list.Columns.Add(T.Text("env_location_label"), 360);
            this.list.Columns.Add(T.Text("env_confirmed_at"), 160);
            this.list.SelectedIndexChanged += delegate { this.deleteButton.Enabled = this.list.SelectedItems.Count > 0; };
            layout.Controls.Add(this.list, 0, 1);

            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;
            buttons.Padding = new Padding(0, 8, 0, 0);
            layout.Controls.Add(buttons, 0, 2);

            var save = new Button();
            save.Text = T.Text("保存");
            save.Width = 90;
            save.Click += delegate { this.DialogResult = DialogResult.OK; this.Close(); };
            buttons.Controls.Add(save);

            var cancel = new Button();
            cancel.Text = T.Text("取消");
            cancel.Width = 90;
            cancel.Click += delegate { this.DialogResult = DialogResult.Cancel; this.Close(); };
            buttons.Controls.Add(cancel);

            this.deleteButton.Text = T.Text("env_delete_selected");
            this.deleteButton.Width = 110;
            this.deleteButton.Enabled = false;
            this.deleteButton.Click += delegate { this.DeleteSelected(); };
            buttons.Controls.Add(this.deleteButton);

            this.RefreshList();
        }

        private void RefreshList()
        {
            this.list.Items.Clear();
            for (int i = 0; i < this.TrustedEnvironments.Count; i++)
            {
                var trusted = this.TrustedEnvironments[i];
                var info = trusted.ToInfo();
                var location = info.EnglishLocation();
                if (T.IsChinese)
                {
                    var chinese = info.ChineseLocation();
                    if (!string.IsNullOrWhiteSpace(chinese)) location += " / " + chinese;
                }
                var item = new ListViewItem(new[] { trusted.Ip ?? "", location, trusted.ConfirmedAtText() });
                item.Tag = trusted;
                this.list.Items.Add(item);
            }
            this.deleteButton.Enabled = false;
        }

        private void DeleteSelected()
        {
            if (this.list.SelectedItems.Count == 0) return;
            var selected = new List<TrustedEnvironment>();
            foreach (ListViewItem item in this.list.SelectedItems)
            {
                var trusted = item.Tag as TrustedEnvironment;
                if (trusted != null) selected.Add(trusted);
            }
            foreach (var trusted in selected)
            {
                this.TrustedEnvironments.Remove(trusted);
            }
            this.RefreshList();
        }
    }

    internal sealed class SettingsForm : Form
    {
        private const string MaskText = "********";
        private readonly TextBox tokenBox = new TextBox();
        private readonly TextBox accountBox = new TextBox();
        private readonly TextBox usageBox = new TextBox();
        private readonly NumericUpDown refreshBox = new NumericUpDown();
        private readonly NumericUpDown opacityBox = new NumericUpDown();
        private readonly CheckBox autoStartBox = new CheckBox();
        private readonly CheckBox topMostBox = new CheckBox();
        private readonly CheckBox clickThroughBox = new CheckBox();
        private readonly CheckBox lockPositionBox = new CheckBox();
        private readonly CheckBox showUserNameBox = new CheckBox();
        private readonly CheckBox scroll5hBox = new CheckBox();
        private readonly CheckBox scrollWeeklyBox = new CheckBox();
        private readonly CheckBox scrollGpt55XHighBox = new CheckBox();
        private readonly CheckBox scrollGpt55HighBox = new CheckBox();
        private readonly CheckBox scrollGpt55MediumBox = new CheckBox();
        private readonly CheckBox scrollGpt54XHighBox = new CheckBox();
        private readonly CheckBox envCheckOnStartupBox = new CheckBox();
        private readonly CheckBox envConfirmMediumOnManualBox = new CheckBox();
        private readonly CheckBox envRecheckHighOnManualBox = new CheckBox();
        private readonly Button trustedEnvironmentsButton = new Button();
        private readonly Label hint = new Label();
        private readonly MonitorConfig initial;
        private List<TrustedEnvironment> trustedEnvironments;
        private string pendingTokenDpapi;
        private string pendingAccountIdDpapi;
        public MonitorConfig Config;

        public SettingsForm(MonitorConfig cfg)
        {
            this.initial = cfg;
            this.Config = cfg;
            this.trustedEnvironments = new List<TrustedEnvironment>();
            if (cfg.TrustedEnvironments != null)
            {
                foreach (var trusted in cfg.TrustedEnvironments)
                {
                    if (trusted != null) this.trustedEnvironments.Add(trusted.Clone());
                }
            }
            this.Text = T.Text("CodexFloat 设置");
            this.Size = new Size(860, 640);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(16);
            layout.ColumnCount = 2;
            layout.RowCount = 10;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            this.Controls.Add(layout);

            this.tokenBox.UseSystemPasswordChar = true;
            this.accountBox.UseSystemPasswordChar = true;
            this.tokenBox.Text = HasStoredToken(cfg) ? MaskText : "";
            this.accountBox.Text = HasStoredAccount(cfg) ? MaskText : "";
            this.usageBox.Text = string.IsNullOrWhiteSpace(cfg.UsageEndpoint) ? "https://chatgpt.com/backend-api/wham/usage" : cfg.UsageEndpoint;
            this.refreshBox.Minimum = 60;
            this.refreshBox.Maximum = 3600;
            this.refreshBox.Value = Math.Max(60, cfg.RefreshSeconds);
            this.opacityBox.Minimum = 35;
            this.opacityBox.Maximum = 100;
            this.opacityBox.Value = Math.Max(35, Math.Min(100, cfg.OpacityPercent));

            this.autoStartBox.Text = T.Text("开机自动启动");
            this.autoStartBox.AutoSize = true;
            this.autoStartBox.Checked = cfg.AutoStart || StartupManager.IsEnabled();
            this.topMostBox.Text = T.Text("总是置顶");
            this.topMostBox.AutoSize = true;
            this.topMostBox.Checked = cfg.AlwaysOnTop;
            this.clickThroughBox.Text = T.Text("鼠标穿透");
            this.clickThroughBox.AutoSize = true;
            this.clickThroughBox.Checked = cfg.MouseClickThrough;
            this.lockPositionBox.Text = T.Text("锁定窗口位置");
            this.lockPositionBox.AutoSize = true;
            this.lockPositionBox.Checked = cfg.LockPosition;
            this.showUserNameBox.Text = T.Text("显示账户名");
            this.showUserNameBox.AutoSize = true;
            this.showUserNameBox.Checked = cfg.ShowUserNameInDetails;
            this.scroll5hBox.Text = T.Text("scroll_5h");
            this.scroll5hBox.AutoSize = true;
            this.scroll5hBox.Checked = cfg.ScrollShowFiveHour;
            this.scrollWeeklyBox.Text = T.Text("scroll_weekly");
            this.scrollWeeklyBox.AutoSize = true;
            this.scrollWeeklyBox.Checked = cfg.ScrollShowWeekly;
            this.scrollGpt55XHighBox.Text = T.Text("scroll_gpt_55_xhigh");
            this.scrollGpt55XHighBox.AutoSize = true;
            this.scrollGpt55XHighBox.Checked = cfg.ScrollShowGpt55XHigh;
            this.scrollGpt55HighBox.Text = T.Text("scroll_gpt_55_high");
            this.scrollGpt55HighBox.AutoSize = true;
            this.scrollGpt55HighBox.Checked = cfg.ScrollShowGpt55High;
            this.scrollGpt55MediumBox.Text = T.Text("scroll_gpt_55_medium");
            this.scrollGpt55MediumBox.AutoSize = true;
            this.scrollGpt55MediumBox.Checked = cfg.ScrollShowGpt55Medium;
            this.scrollGpt54XHighBox.Text = T.Text("scroll_gpt_54_xhigh");
            this.scrollGpt54XHighBox.AutoSize = true;
            this.scrollGpt54XHighBox.Checked = cfg.ScrollShowGpt54XHigh;
            this.envCheckOnStartupBox.Text = T.Text("env_check_on_startup");
            this.envCheckOnStartupBox.AutoSize = true;
            this.envCheckOnStartupBox.Checked = cfg.EnvironmentCheckOnStartup;
            this.envConfirmMediumOnManualBox.Text = T.Text("env_confirm_medium_on_manual");
            this.envConfirmMediumOnManualBox.AutoSize = true;
            this.envConfirmMediumOnManualBox.Checked = cfg.EnvironmentConfirmMediumRiskOnManualRefresh;
            this.envRecheckHighOnManualBox.Text = T.Text("env_recheck_high_on_manual");
            this.envRecheckHighOnManualBox.AutoSize = true;
            this.envRecheckHighOnManualBox.Checked = cfg.EnvironmentRecheckHighRiskOnManualRefresh;
            this.trustedEnvironmentsButton.Text = T.Text("env_trusted_library");
            this.trustedEnvironmentsButton.Width = 150;
            this.trustedEnvironmentsButton.Height = 28;
            this.trustedEnvironmentsButton.Click += delegate { this.OpenTrustedEnvironments(); };

            AddRow(layout, 0, "ACCESS_TOKEN", this.tokenBox);
            AddRow(layout, 1, "ACCOUNT_ID", this.accountBox);
            AddRow(layout, 2, T.Text("用量接口"), this.usageBox);
            AddRow(layout, 3, T.Text("刷新秒数"), this.refreshBox);
            AddRow(layout, 4, T.Text("不透明度"), this.opacityBox);

            var behaviorPanel = new FlowLayoutPanel();
            behaviorPanel.Dock = DockStyle.Fill;
            behaviorPanel.FlowDirection = FlowDirection.LeftToRight;
            behaviorPanel.WrapContents = true;
            behaviorPanel.Padding = new Padding(0, 7, 0, 0);
            behaviorPanel.Controls.Add(this.autoStartBox);
            behaviorPanel.Controls.Add(this.topMostBox);
            behaviorPanel.Controls.Add(this.clickThroughBox);
            behaviorPanel.Controls.Add(this.lockPositionBox);
            behaviorPanel.Controls.Add(this.showUserNameBox);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.Controls.Add(new Label { Text = T.Text("快捷设置"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 5);
            layout.Controls.Add(behaviorPanel, 1, 5);

            var scrollPanel = new FlowLayoutPanel();
            scrollPanel.Dock = DockStyle.Fill;
            scrollPanel.FlowDirection = FlowDirection.LeftToRight;
            scrollPanel.WrapContents = true;
            scrollPanel.Padding = new Padding(0, 5, 0, 0);
            scrollPanel.Controls.Add(this.scroll5hBox);
            scrollPanel.Controls.Add(this.scrollWeeklyBox);
            scrollPanel.Controls.Add(this.scrollGpt55XHighBox);
            scrollPanel.Controls.Add(this.scrollGpt55HighBox);
            scrollPanel.Controls.Add(this.scrollGpt55MediumBox);
            scrollPanel.Controls.Add(this.scrollGpt54XHighBox);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.Controls.Add(new Label { Text = T.Text("滚动数据"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 6);
            layout.Controls.Add(scrollPanel, 1, 6);

            var envPanel = new FlowLayoutPanel();
            envPanel.Dock = DockStyle.Fill;
            envPanel.FlowDirection = FlowDirection.LeftToRight;
            envPanel.WrapContents = true;
            envPanel.Padding = new Padding(0, 5, 0, 0);
            envPanel.Controls.Add(this.envCheckOnStartupBox);
            envPanel.Controls.Add(this.envConfirmMediumOnManualBox);
            envPanel.Controls.Add(this.envRecheckHighOnManualBox);
            envPanel.Controls.Add(this.trustedEnvironmentsButton);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            layout.Controls.Add(new Label { Text = T.Text("env_safety_title"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 7);
            layout.Controls.Add(envPanel, 1, 7);

            var importPanel = new FlowLayoutPanel();
            importPanel.Dock = DockStyle.Fill;
            importPanel.FlowDirection = FlowDirection.LeftToRight;
            importPanel.Padding = new Padding(0, 3, 0, 0);
            var autoImport = new Button();
            autoImport.Text = T.Text("自动读取");
            autoImport.Width = 110;
            autoImport.Click += delegate { this.AutoImportCredentials(); };
            importPanel.Controls.Add(autoImport);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.Controls.Add(new Label { Text = T.Text("凭据"), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 8);
            layout.Controls.Add(importPanel, 1, 8);

            this.hint.Text = T.Text("凭据提示");
            this.hint.Dock = DockStyle.Fill;
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(this.hint, 1, 9);

            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 48;
            buttons.Padding = new Padding(0, 6, 12, 8);
            this.Controls.Add(buttons);

            var save = new Button();
            save.Text = T.Text("保存");
            save.Width = 90;
            save.Click += delegate { this.SaveAndClose(); };
            buttons.Controls.Add(save);

            var cancel = new Button();
            cancel.Text = T.Text("取消");
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
            control.Dock = DockStyle.Fill;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.Controls.Add(lbl, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private void SaveAndClose()
        {
            var next = new MonitorConfig();
            next.AccessTokenDpapi = this.pendingTokenDpapi ?? this.initial.AccessTokenDpapi;
            if (IsNewSecretInput(this.tokenBox.Text)) next.AccessTokenDpapi = SecretBox.Protect(this.tokenBox.Text.Trim());

            next.AccountIdDpapi = this.pendingAccountIdDpapi ?? this.initial.AccountIdDpapi;
            if (IsNewSecretInput(this.accountBox.Text))
            {
                next.AccountIdDpapi = SecretBox.Protect(this.accountBox.Text.Trim());
            }
            else if (string.IsNullOrWhiteSpace(next.AccountIdDpapi) && !string.IsNullOrWhiteSpace(this.initial.AccountId))
            {
                next.AccountIdDpapi = SecretBox.Protect(this.initial.AccountId.Trim());
            }
            next.AccountId = "";
            next.RefreshSeconds = (int)this.refreshBox.Value;
            next.AutoStart = this.autoStartBox.Checked;
            next.OpacityPercent = (int)this.opacityBox.Value;
            next.AlwaysOnTop = this.topMostBox.Checked;
            next.MouseClickThrough = this.clickThroughBox.Checked;
            next.LockPosition = this.lockPositionBox.Checked;
            next.ShowUserNameInDetails = this.showUserNameBox.Checked;
            next.ScrollShowFiveHour = this.scroll5hBox.Checked;
            next.ScrollShowWeekly = this.scrollWeeklyBox.Checked;
            next.ScrollShowGpt55XHigh = this.scrollGpt55XHighBox.Checked;
            next.ScrollShowGpt55High = this.scrollGpt55HighBox.Checked;
            next.ScrollShowGpt55Medium = this.scrollGpt55MediumBox.Checked;
            next.ScrollShowGpt54XHigh = this.scrollGpt54XHighBox.Checked;
            next.EnvironmentCheckOnStartup = this.envCheckOnStartupBox.Checked;
            next.EnvironmentConfirmMediumRiskOnManualRefresh = this.envConfirmMediumOnManualBox.Checked;
            next.EnvironmentRecheckHighRiskOnManualRefresh = this.envRecheckHighOnManualBox.Checked;
            next.TrustedEnvironments = new List<TrustedEnvironment>();
            foreach (var trusted in this.trustedEnvironments)
            {
                if (trusted != null) next.TrustedEnvironments.Add(trusted.Clone());
            }
            next.EnvironmentLastConfirmedIp = this.initial.EnvironmentLastConfirmedIp;
            next.EnvironmentLastConfirmedCountryCode = this.initial.EnvironmentLastConfirmedCountryCode;
            next.EnvironmentLastConfirmedCountryName = this.initial.EnvironmentLastConfirmedCountryName;
            next.EnvironmentLastConfirmedRegion = this.initial.EnvironmentLastConfirmedRegion;
            next.EnvironmentLastConfirmedCity = this.initial.EnvironmentLastConfirmedCity;
            if (next.TrustedEnvironments.Count > 0)
            {
                var lastTrusted = next.TrustedEnvironments[next.TrustedEnvironments.Count - 1];
                next.EnvironmentLastConfirmedIp = lastTrusted.Ip;
                next.EnvironmentLastConfirmedCountryCode = lastTrusted.CountryCode;
                next.EnvironmentLastConfirmedCountryName = lastTrusted.CountryName;
                next.EnvironmentLastConfirmedRegion = lastTrusted.Region;
                next.EnvironmentLastConfirmedCity = lastTrusted.City;
            }
            else
            {
                next.EnvironmentLastConfirmedIp = "";
                next.EnvironmentLastConfirmedCountryCode = "";
                next.EnvironmentLastConfirmedCountryName = "";
                next.EnvironmentLastConfirmedRegion = "";
                next.EnvironmentLastConfirmedCity = "";
            }
            next.ResetCardsEndpoint = this.initial.ResetCardsEndpoint;
            next.UsageEndpoint = this.usageBox.Text.Trim();
            next.Originator = this.initial.Originator;
            next.OpenAIBeta = this.initial.OpenAIBeta;
            next.ThemeName = this.initial.ThemeName;
            next.Language = this.initial.Language;
            next.FloatingX = this.initial.FloatingX;
            next.FloatingY = this.initial.FloatingY;
            if (string.IsNullOrWhiteSpace(next.AccessTokenDpapi) || string.IsNullOrWhiteSpace(next.AccountIdDpapi))
            {
                MessageBox.Show(T.Text("需要凭据"), "CodexFloat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            this.Config = next;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void OpenTrustedEnvironments()
        {
            using (var form = new TrustedEnvironmentsForm(this.trustedEnvironments))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    this.trustedEnvironments = form.TrustedEnvironments;
                    this.trustedEnvironmentsButton.Text = T.Text("env_trusted_library") + " (" + this.trustedEnvironments.Count.ToString(CultureInfo.InvariantCulture) + ")";
                }
            }
        }

        private void AutoImportCredentials()
        {
            ImportedCredentials imported;
            string message;
            this.Cursor = Cursors.WaitCursor;
            try
            {
                if (!CredentialImporter.TryFind(out imported, out message))
                {
                    MessageBox.Show(message, "CodexFloat", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                this.pendingTokenDpapi = SecretBox.Protect(imported.AccessToken);
                this.pendingAccountIdDpapi = SecretBox.Protect(imported.AccountId);
                this.tokenBox.Text = MaskText;
                this.accountBox.Text = MaskText;
                this.hint.Text = T.Text("自动读取成功提示") + "\r\n" + T.Text("来源") + ": " + imported.SourcePath;
                MessageBox.Show(T.Text("自动读取成功弹窗"), "CodexFloat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        private static bool HasStoredToken(MonitorConfig cfg)
        {
            return !string.IsNullOrWhiteSpace(cfg.AccessTokenDpapi);
        }

        private static bool HasStoredAccount(MonitorConfig cfg)
        {
            return !string.IsNullOrWhiteSpace(cfg.AccountIdDpapi) || !string.IsNullOrWhiteSpace(cfg.AccountId);
        }

        private static bool IsNewSecretInput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.Trim() != MaskText;
        }
    }

    internal sealed class AboutForm : Form
    {
        private readonly ToolTip linkTips = new ToolTip();

        public AboutForm()
        {
            this.Text = T.Text("关于 CodexFloat");
            this.Text = T.IsChinese ? "\u5173\u4e8e CodexFloat" : "About CodexFloat";
            this.Size = new Size(300, 286);
            this.StartPosition = FormStartPosition.Manual;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(0);
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            this.Controls.Add(layout);

            layout.Controls.Add(CreateBanner(), 0, 0);

            AddText(layout, T.Text("关于描述"), 44);
            AddLinkRow(layout);

        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var area = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(area.Left + (area.Width - this.Width) / 2, area.Top + (area.Height - this.Height) / 2);
        }

        private static Control CreateBanner()
        {
            var banner = new Panel();
            banner.Dock = DockStyle.Fill;
            banner.Margin = new Padding(0);
            banner.Paint += delegate(object sender, PaintEventArgs e) {
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, banner.Width, banner.Height), Color.FromArgb(139, 195, 246), Color.FromArgb(112, 149, 220), 0f))
                {
                    e.Graphics.FillRectangle(bg, 0, 0, banner.Width, banner.Height);
                }
            };

            var icon = new PictureBox();
            icon.Size = new Size(54, 54);
            icon.SizeMode = PictureBoxSizeMode.Zoom;
            icon.BackColor = Color.Transparent;
            icon.Image = LoadAboutLogo();
            banner.Controls.Add(icon);

            var name = new Label();
            name.Text = "CodexFloat";
            name.Font = new Font("Segoe UI Semibold", 20f);
            name.ForeColor = Color.White;
            name.BackColor = Color.Transparent;
            name.TextAlign = ContentAlignment.MiddleLeft;
            banner.Controls.Add(name);

            banner.Resize += delegate { LayoutBannerContent(banner, icon, name); };
            LayoutBannerContent(banner, icon, name);

            return banner;
        }

        private static void LayoutBannerContent(Control banner, PictureBox icon, Label name)
        {
            var nameSize = TextRenderer.MeasureText(name.Text, name.Font);
            var gap = 12;
            var groupWidth = icon.Width + gap + nameSize.Width;
            var left = Math.Max(12, (banner.Width - groupWidth) / 2);
            icon.Location = new Point(left, Math.Max(0, (banner.Height - icon.Height) / 2));
            name.Location = new Point(left + icon.Width + gap, Math.Max(0, (banner.Height - 46) / 2));
            name.Size = new Size(Math.Min(nameSize.Width + 8, Math.Max(80, banner.Width - name.Left - 8)), 46);
        }

        private static Image LoadAboutLogo()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "CodexFloat-logo-256.png");
            if (File.Exists(path))
            {
                try
                {
                    return Image.FromFile(path);
                }
                catch
                {
                }
            }

            return IconFactory.CreateTrayIcon().ToBitmap();
        }

        private static string AboutDescriptionText()
        {
            return T.IsChinese
                ? "CodexFloat \u662f\u4e00\u6b3e Windows \u60ac\u6d6e\u76d1\u63a7\u5de5\u5177\uff0c\u7528\u4e8e\u67e5\u770b Codex \u5269\u4f59\u7528\u91cf\u3001\u91cd\u7f6e\u65f6\u95f4\u3001\u91cd\u7f6e\u5361\u548c\u6a21\u578b IQ\u3002"
                : "CodexFloat is a Windows floating widget for Codex remaining usage, reset times, reset cards, and model IQ.";
        }

        private static void AddText(TableLayoutPanel layout, string text, int height)
        {
            var label = new Label();
            label.Text = AboutDescriptionText();
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(16, 12, 16, 4);
            label.AutoSize = false;
            label.Font = new Font("Segoe UI", 8.5f);
            label.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(label, 0, layout.Controls.Count);
            AddInfoBlock(layout);
        }

        private static void AddInfoBlock(TableLayoutPanel layout)
        {
            var panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.Margin = new Padding(16, 0, 16, 0);
            panel.Padding = new Padding(0);
            panel.ColumnCount = 1;
            panel.RowCount = 3;
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34f));

            AddInfoLine(panel, ReleaseVersionText());
            AddInfoLine(panel, CopyrightText());
            AddInfoLine(panel, BuildDateText());

            layout.Controls.Add(panel, 0, layout.Controls.Count);
        }

        private static void AddInfoLine(TableLayoutPanel panel, string text)
        {
            var label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.AutoSize = false;
            label.Margin = new Padding(0);
            label.Font = new Font("Segoe UI", 8.2f);
            label.ForeColor = Color.FromArgb(50, 50, 58);
            label.TextAlign = ContentAlignment.MiddleLeft;
            panel.Controls.Add(label, 0, panel.Controls.Count);
        }

        private void AddLinkRow(TableLayoutPanel layout)
        {
            var row = new Panel();
            row.Dock = DockStyle.Fill;
            row.Margin = new Padding(0);
            row.Padding = new Padding(16, 6, 16, 0);
            row.Resize += delegate { LayoutAboutLinks(row); };

            AddLinkAnchor(row, AboutLinkText("\u8054\u7cfb\u4f5c\u8005", "Contact"), AppLinks.AuthorMailto);
            AddLinkAnchor(row, AboutLinkText("Github", "GitHub"), AppLinks.ProjectGithub);
            AddLinkAnchor(row, AboutLinkText("\u611f\u8c22 Codex Radar", "Thanks: Codex Radar"), T.IsChinese ? AppLinks.CodexRadar : AppLinks.CodexRadarEnglish);
            LayoutAboutLinks(row);

            layout.Controls.Add(row, 0, layout.Controls.Count);
        }

        private void AddLinkAnchor(Panel row, string text, string url)
        {
            var link = CreateAboutLink(text, url);
            row.Controls.Add(link);
        }

        private LinkLabel CreateAboutLink(string text, string url)
        {
            var link = new LinkLabel();
            link.Text = text;
            link.AutoSize = false;
            link.TextAlign = ContentAlignment.MiddleLeft;
            link.Margin = new Padding(0);
            link.Font = new Font("Segoe UI", 8.0f);
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.LinkColor = Color.FromArgb(34, 70, 156);
            link.ActiveLinkColor = Color.FromArgb(92, 54, 190);
            link.VisitedLinkColor = link.LinkColor;
            link.Tag = url;
            if (string.Equals(url, AppLinks.AuthorMailto, StringComparison.OrdinalIgnoreCase))
            {
                var title = MailTitleText();
                link.AccessibleDescription = title;
                this.linkTips.SetToolTip(link, title);
            }
            if (string.Equals(url, AppLinks.CodexRadar, StringComparison.OrdinalIgnoreCase) || string.Equals(url, AppLinks.CodexRadarEnglish, StringComparison.OrdinalIgnoreCase))
            {
                var title = T.IsChinese ? "\u6a21\u578b IQ \u6570\u636e\u6765\u6e90\uff1aCodex Radar\uff0c\u611f\u8c22\u5176\u63d0\u4f9b\u6570\u636e\u53c2\u8003\u3002" : "Model IQ data source: Codex Radar. Thanks for the data reference.";
                link.AccessibleDescription = title;
                this.linkTips.SetToolTip(link, title);
            }
            link.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs e) {
                LinkOpener.OpenUrl(Convert.ToString(((Control)sender).Tag, CultureInfo.InvariantCulture));
            };
            return link;
        }

        private static void LayoutAboutLinks(Panel row)
        {
            if (row.Controls.Count == 0) return;

            var widths = new int[row.Controls.Count];
            var totalTextWidth = 0;
            for (var i = 0; i < row.Controls.Count; i++)
            {
                var control = row.Controls[i];
                var preferred = TextRenderer.MeasureText(control.Text, control.Font);
                widths[i] = preferred.Width + 2;
                totalTextWidth += widths[i];
            }

            var left = row.Padding.Left;
            var right = Math.Max(left, row.Width - row.Padding.Right);
            var availableGap = Math.Max(0, right - left - totalTextWidth);
            var gap = row.Controls.Count > 1 ? availableGap / (row.Controls.Count - 1) : 0;
            var x = left;
            var y = Math.Max(row.Padding.Top, (row.Height - 22) / 2);

            for (var i = 0; i < row.Controls.Count; i++)
            {
                var control = row.Controls[i];
                control.Location = new Point(x, y);
                control.Size = new Size(widths[i], 22);
                x += widths[i] + gap;
            }

            if (row.Controls.Count > 1)
            {
                var last = row.Controls[row.Controls.Count - 1];
                last.Left = right - last.Width;
            }
        }

        private void AddLinkCell(TableLayoutPanel row, int column, string text, string url)
        {
            var cell = new TableLayoutPanel();
            cell.Dock = DockStyle.Fill;
            cell.Margin = new Padding(0);
            cell.Padding = new Padding(0);
            cell.ColumnCount = 1;
            cell.RowCount = 1;
            cell.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var link = new LinkLabel();
            link.Text = text;
            link.Dock = DockStyle.Fill;
            link.AutoSize = false;
            link.TextAlign = ContentAlignment.MiddleLeft;
            link.Margin = new Padding(0);
            link.Font = new Font("Segoe UI", 8.0f);
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.LinkColor = Color.FromArgb(34, 70, 156);
            link.ActiveLinkColor = Color.FromArgb(92, 54, 190);
            link.VisitedLinkColor = link.LinkColor;
            link.Tag = url;
            if (string.Equals(url, AppLinks.AuthorMailto, StringComparison.OrdinalIgnoreCase))
            {
                var title = MailTitleText();
                link.AccessibleDescription = title;
                this.linkTips.SetToolTip(link, title);
            }
            link.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs e) {
                LinkOpener.OpenUrl(Convert.ToString(((Control)sender).Tag, CultureInfo.InvariantCulture));
            };
            cell.Controls.Add(link, 0, 0);

            row.Controls.Add(cell, column, 0);
        }

        private static string AboutLinkText(string zh, string en)
        {
            return T.IsChinese ? zh : en;
        }

        private static string ReleaseVersionText()
        {
            var attrs = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            var value = attrs.Length > 0 ? ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion : Application.ProductVersion;
            value = value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? "V" + value.Substring(1) : "V" + value;
            return T.IsChinese ? "\u5f53\u524d\u7248\u672c\uff1a" + value : "Current Version: " + value;
        }

        private static string CopyrightText()
        {
            return "Copyright (C) 2026  By RayOhmie";
        }

        private static string BuildDateText()
        {
            var text = BuildTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
            return T.IsChinese ? "\u6700\u540e\u7f16\u8bd1\u65e5\u671f\uff1a" + text : "Last Build Date: " + text;
        }

        private static DateTime BuildTime()
        {
            try
            {
                var path = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return File.GetLastWriteTime(path);
            }
            catch
            {
            }

            return DateTime.Now;
        }

        private static string MailTitleText()
        {
            return T.IsChinese
                ? "\u5411\u4f5c\u8005\u53d1\u9001\u7535\u5b50\u90ae\u4ef6mailto:RayOhmie@gmail.com"
                : "Send an email to the author: mailto:RayOhmie@gmail.com";
        }

        private static Bitmap CreateLinkIcon(int kind)
        {
            var bmp = new Bitmap(18, 18);
            using (var g = Graphics.FromImage(bmp))
            using (var pen = new Pen(Color.FromArgb(180, 138, 106, 235), 1.7f))
            using (var brush = new SolidBrush(Color.FromArgb(210, 138, 106, 235)))
            using (var light = new SolidBrush(Color.FromArgb(235, 245, 242, 255)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                switch (kind)
                {
                    case 0:
                        g.DrawEllipse(pen, 6, 3, 6, 6);
                        g.DrawArc(pen, 3, 9, 12, 8, 205, 130);
                        break;
                    case 1:
                        g.FillEllipse(brush, 2, 2, 14, 14);
                        using (var font = new Font("Segoe UI Semibold", 6.5f))
                        {
                            g.DrawString("GH", font, light, 2.2f, 4.3f);
                        }
                        break;
                    case 2:
                        g.DrawArc(pen, 3, 3, 12, 12, 28, 285);
                        g.FillPolygon(brush, new[] { new Point(13, 2), new Point(16, 3), new Point(14, 6) });
                        break;
                    case 3:
                        g.DrawEllipse(pen, 2.5f, 2.5f, 13f, 13f);
                        using (var font = new Font("Segoe UI Semibold", 9f))
                        {
                            g.DrawString("$", font, brush, 5.2f, 1.2f);
                        }
                        break;
                    default:
                        g.FillPolygon(brush, new[] { new Point(9, 2), new Point(11, 7), new Point(16, 7), new Point(12, 10), new Point(14, 15), new Point(9, 12), new Point(4, 15), new Point(6, 10), new Point(2, 7), new Point(7, 7) });
                        break;
                }
            }
            return bmp;
        }
    }

    internal sealed class StableMenuRenderer : ToolStripProfessionalRenderer
    {
        public static readonly ToolStripRenderer Instance = new StableMenuRenderer();

        private StableMenuRenderer()
        {
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            if (e.Item != null && !e.Item.Enabled)
            {
                e.ArrowColor = SystemColors.GrayText;
            }
            else
            {
                e.ArrowColor = e.Item != null && e.Item.Selected
                    ? Color.White
                    : Color.FromArgb(48, 52, 62);
            }

            base.OnRenderArrow(e);
        }
    }

    internal static class MenuDropDownPlacer
    {
        public static void Attach(ContextMenuStrip menu, Func<Rectangle> screenBoundsProvider)
        {
            ApplyRenderer(menu);
            foreach (ToolStripItem item in menu.Items)
            {
                Attach(item, screenBoundsProvider);
            }
        }

        public static void AttachChildren(ToolStripDropDownItem parent, Func<Rectangle> screenBoundsProvider)
        {
            ApplyRenderer(parent.DropDown);
            foreach (ToolStripItem item in parent.DropDownItems)
            {
                Attach(item, screenBoundsProvider);
            }
        }

        private static void Attach(ToolStripItem item, Func<Rectangle> screenBoundsProvider)
        {
            var dropDownItem = item as ToolStripDropDownItem;
            if (dropDownItem == null) return;
            ApplyRenderer(dropDownItem.DropDown);

            dropDownItem.DropDownOpening += delegate {
                AdjustDirection(dropDownItem, screenBoundsProvider);
            };

            foreach (ToolStripItem child in dropDownItem.DropDownItems)
            {
                Attach(child, screenBoundsProvider);
            }
        }

        private static void ApplyRenderer(ToolStripDropDown dropDown)
        {
            if (dropDown != null) dropDown.Renderer = StableMenuRenderer.Instance;
        }

        private static void AdjustDirection(ToolStripDropDownItem item, Func<Rectangle> screenBoundsProvider)
        {
            var ownerDropDown = item.Owner as ToolStripDropDown;
            if (ownerDropDown == null) return;

            var screenBounds = screenBoundsProvider();
            var preferredSize = item.DropDown.GetPreferredSize(Size.Empty);
            var rightEdge = ownerDropDown.Bounds.Right + preferredSize.Width;

            item.DropDownDirection = rightEdge > screenBounds.Right
                ? ToolStripDropDownDirection.Left
                : ToolStripDropDownDirection.Right;
        }
    }

    internal static class NativeMethods
    {
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;
        public const int ULW_ALPHA = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct PointStruct
        {
            public int X;
            public int Y;
            public PointStruct(int x, int y) { this.X = x; this.Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SizeStruct
        {
            public int Cx;
            public int Cy;
            public SizeStruct(int cx, int cy) { this.Cx = cx; this.Cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hDc);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool UpdateLayeredWindow(
            IntPtr hWnd,
            IntPtr hdcDst,
            ref PointStruct pptDst,
            ref SizeStruct psize,
            IntPtr hdcSrc,
            ref PointStruct pptSrc,
            int crKey,
            ref BlendFunction pblend,
            int dwFlags);
    }

    internal static class DateText
    {
        public static DateTime UnixToLocal(double value)
        {
            var seconds = value > 10000000000.0 ? value / 1000.0 : value;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
        }

        public static bool TryParseDate(object value, out DateTime dt)
        {
            dt = DateTime.MinValue;
            if (value == null) return false;
            double number;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out number) && number > 1000000000.0)
            {
                dt = UnixToLocal(number);
                return true;
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
            {
                dt = dto.ToLocalTime().DateTime;
                return true;
            }
            return false;
        }

        public static string ResetDescription(DateTime? resetAtLocal)
        {
            return ResetDescription(resetAtLocal, false);
        }

        public static string ResetDescriptionDetailed(DateTime? resetAtLocal)
        {
            return ResetDescription(resetAtLocal, true);
        }

        private static string ResetDescription(DateTime? resetAtLocal, bool includeMinuteForDays)
        {
            if (!resetAtLocal.HasValue) return T.Text("重置") + " --";
            var remaining = resetAtLocal.Value - DateTime.Now;
            if (remaining.TotalSeconds < 0) remaining = TimeSpan.Zero;
            string duration;
            if (remaining.TotalDays >= 1)
            {
                duration = includeMinuteForDays
                    ? string.Format(CultureInfo.InvariantCulture, "{0} d {1:00} h {2:00} m", (int)Math.Floor(remaining.TotalDays), remaining.Hours, remaining.Minutes)
                    : string.Format(CultureInfo.InvariantCulture, "{0} d {1:00} h", (int)Math.Floor(remaining.TotalDays), remaining.Hours);
            }
            else
            {
                duration = string.Format(CultureInfo.InvariantCulture, "{0} h {1:00} m", (int)Math.Floor(remaining.TotalHours), remaining.Minutes);
            }
            if (T.IsChinese)
            {
                return T.Text("即将于") + " " + duration + " " + T.Text("后重置") + " (" + resetAtLocal.Value.ToString("yyyy-MM-dd HH:mm") + ")";
            }
            return "Resets in " + duration + " (" + resetAtLocal.Value.ToString("yyyy-MM-dd HH:mm") + ")";
        }
    }

    internal static class T
    {
        private static string language = "zh-CN";

        public static bool IsChinese
        {
            get { return language != "en-US"; }
        }

        public static void SetLanguage(string lang)
        {
            language = lang == "en-US" ? "en-US" : "zh-CN";
        }

        public static string Text(string key)
        {
            if (IsChinese)
            {
                if (key == "关于描述") return "Windows 悬浮监控工具，用于查看 Codex 剩余用量、重置时间、重置卡和模型 IQ。";
                if (key == "联系作者") return "联系作者";
                if (key == "Github") return "Github";
                if (key == "捐助") return "捐助";
                if (key == "鸣谢") return "鸣谢";
            }
            if (!IsChinese) return English(key);
            switch (key)
            {
                case "刷新中": return "\u5237\u65b0\u4e2d";
                case "剩余": return "\u5269\u4f59";
                case "已用": return "\u5df2\u7528";
                case "重置": return "\u91cd\u7f6e";
                case "重置卡": return "\u91cd\u7f6e\u5361";
                case "连接失败": return "\u8fde\u63a5\u5931\u8d25";
                case "剩余用量": return "\u5269\u4f59\u7528\u91cf";
                case "详情标题": return "Codex 剩余用量 & 重置时间";
                case "用户": return "用户";
                case "账户标签": return "账户:";
                case "即将于": return "\u5373\u5c06\u4e8e";
                case "后重置": return "\u540e\u91cd\u7f6e";
                case "显示详情": return "显示详情";
                case "隐藏详情": return "隐藏详情";
                case "刷新": return "刷新";
                case "设置": return "设置";
                case "重置悬浮窗位置": return "重置悬浮窗位置";
                case "帮助": return "帮助";
                case "关于": return "关于";
                case "检查更新": return "检查更新";
                case "退出": return "退出";
                case "外观": return "外观";
                case "快捷设置": return "快捷设置";
                case "滚动数据": return "滚动数据";
                case "scroll_5h": return "5h 剩余 & 重置";
                case "scroll_weekly": return "Weekly 剩余 & 重置";
                case "scroll_gpt_55_xhigh": return "GPT-5.5-XHigh IQ";
                case "scroll_gpt_55_high": return "GPT-5.5-High IQ";
                case "scroll_gpt_55_medium": return "GPT-5.5-Medium IQ";
                case "scroll_gpt_54_xhigh": return "GPT-5.4-XHigh IQ";
                case "开机自动启动": return "开机自动启动";
                case "总是置顶": return "总是置顶";
                case "鼠标穿透": return "鼠标穿透";
                case "锁定窗口位置": return "锁定窗口位置";
                case "显示账户名": return "显示账户名";
                case "不透明度": return "不透明度";
                case "语言": return "语言";
                case "CodexFloat 设置": return "CodexFloat 设置";
                case "用量接口": return "用量接口";
                case "刷新秒数": return "刷新秒数";
                case "凭据": return "凭据";
                case "自动读取": return "自动读取";
                case "凭据提示": return "凭据框只显示遮盖内容。留空或保留星号表示继续使用现有加密凭据。";
                case "保存": return "保存";
                case "取消": return "取消";
                case "需要凭据": return "ACCESS_TOKEN 和 ACCOUNT_ID 必填。";
                case "自动读取成功提示": return "已自动读取并加密暂存。点击保存后写入本地配置。";
                case "来源": return "来源";
                case "自动读取成功弹窗": return "已自动读取 ACCESS_TOKEN 和 ACCOUNT_ID，并加密暂存。\r\n点击保存后写入配置。";
                case "无法打开链接": return "无法打开链接";
                case "未找到凭据": return "没有在默认 Windows 路径中找到 Codex 或 ChatGPT 凭据 JSON。";
                case "找到凭据": return "找到凭据";
                case "扫描过多": return "已扫描 900 个 JSON 文件后停止。若未找到凭据，请使用手动输入。";
                case "更新时间": return "更新时间";
                case "计划": return "计划";
                case "积分": return "积分";
                case "更新": return "更新";
                case "关于 CodexFloat": return "关于 CodexFloat";
                case "关于描述": return "一个 Windows 悬浮窗工具，用来查看 Codex 剩余用量、重置时间和重置卡到期时间。";
                case "联系作者": return "联系作者 / GitHub 主页";
                case "项目 GitHub": return "项目 GitHub";
                case "捐助说明": return "捐助：暂未配置，后续会在 GitHub 页面更新";
                case "鸣谢说明": return "鸣谢";
                case "接口注意": return "注意：backend-api 属于非公开稳定接口，字段和可用性可能随官方客户端变化。";
                case "查看错误日志": return "查看错误日志";
                case "错误代码": return "错误代码";
                case "查看错误日志提示": return "右键菜单可打开错误日志";
                case "读取中": return "读取中";
                case "连接中": return "连接中";
                case "同步短": return "同步";
                case "连接短": return "连接";
                case "更新中省略": return "更新中...";
                case "env_safety_title": return "环境安全监测";
                case "env_check_on_startup": return "启动时自动检测环境";
                case "env_confirm_medium_on_manual": return "中风险手动刷新时二次确认";
                case "env_recheck_high_on_manual": return "高风险手动刷新时再次检测";
                case "env_trusted_library": return "受信任地址库...";
                case "env_trusted_library_hint": return "已确认安全的 IP 和地址会保存在这里；选中过期记录后可删除。";
                case "env_confirmed_at": return "确认时间";
                case "env_delete_selected": return "删除选中";
                case "env_location_label": return "所在地";
                case "env_badge_safe": return "安全";
                case "env_badge_medium": return "中危";
                case "env_badge_high": return "高危";
                case "env_safe_title": return "环境检测安全";
                case "env_safe_message": return "当前网络环境与受信任环境记录一致，已继续查询。";
                case "env_initial_trust_title": return "确认默认受信任环境";
                case "env_initial_trust_message": return "当前没有受信任地址库记录。请确认是否将当前 IP 和地址设为默认受信任环境并继续查询。";
                case "env_initial_trust_pending_message": return "等待用户确认默认受信任环境后手动刷新查询。";
                case "env_initial_trust_continue": return "设为受信任环境\r\n继续查询";
                case "env_initial_trust_wait": return "待我确认网络环境后\r\n手动刷新查询";
                case "env_medium_risk_title": return "当前环境存在风险（风险等级：中）";
                case "env_medium_risk_changed": return "当前 IP 或所在地不在受信任地址库中，请确认是否继续查询。";
                case "env_medium_risk_first_confirm": return "当前没有已确认的环境记录，请确认是否将当前环境作为安全环境继续查询。";
                case "env_medium_pending_message": return "等待用户确认环境后手动刷新查询。";
                case "env_popup_confirm": return "确认";
                case "env_info_unavailable": return "当前无法查询到有效的 IP 和地址。";
                case "env_mini_risk": return "环境风险";
                case "env_mini_check_failed": return "检测失败";
                case "env_confirm_continue": return "确认网络环境无风险\r\n继续查询";
                case "env_wait_manual": return "待我确认网络环境后\r\n手动刷新查询";
                case "env_high_risk_title": return "当前环境存在风险（风险等级：高）";
                case "env_high_risk_message": return "检测到 IP 所在地为中国或香港，已停止连接和查询。请更新环境配置后手动刷新。";
                case "env_high_risk_wait_message": return "高风险环境已阻止查询。请更新环境配置后手动刷新。";
                case "env_check_failed_title": return "环境检测失败";
                case "env_check_failed_message": return "无法验证当前网络和 IP，已暂停连接 ChatGPT 接口。请检查网络后手动刷新。";
                case "error_click_details": return "点击查看";
                case "error_missing_credentials_summary": return "未找到登录";
                case "error_missing_credentials_detail": return "未找到 Codex 登录凭据。请先启动 Codex 并确认已登录，然后在设置中使用自动读取，或手动保存 ACCESS_TOKEN 和 ACCOUNT_ID。";
                case "error_auth_summary": return "登录失效";
                case "error_auth_detail": return "本地保存的 ACCESS_TOKEN 已失效或未被服务端接受。请启动 Codex 刷新登录状态，然后在设置中重新自动读取并保存凭据。";
                case "error_network_summary": return "网络不可用";
                case "error_network_detail": return "无法连接 ChatGPT/Codex 后端。请检查网络、代理或防火墙，然后刷新。";
                case "error_connection_interrupted_summary": return "连接中断";
                case "error_connection_interrupted_detail": return "ChatGPT/Codex 后端连接被临时关闭，通常与电脑睡眠唤醒、网络/代理切换或 HTTPS 连接复用有关。程序已自动重试；如果仍失败，请稍后刷新。";
                case "error_timeout_summary": return "连接超时";
                case "error_timeout_detail": return "请求 ChatGPT/Codex 后端超时。请检查网络或代理状态，然后刷新。";
                case "error_server_summary": return "服务暂不可用";
                case "error_server_detail": return "ChatGPT/Codex 后端暂时不可用。请稍后刷新。";
                case "error_api_changed_summary": return "接口可能变化";
                case "error_api_changed_detail": return "接口返回内容或地址可能已变化，当前版本无法解析。请检查更新。";
                case "error_credential_config_summary": return "凭据无法解密";
                case "error_credential_config_detail": return "本地加密凭据无法由当前 Windows 用户解密。请在设置中重新自动读取或手动保存凭据。";
                case "error_http_summary": return "请求被拒绝";
                case "error_http_detail": return "服务端拒绝了当前请求。";
                case "error_unknown_summary": return "读取失败";
                case "error_unknown_detail": return "读取 Codex 用量失败。请启动 Codex 确认已登录，然后刷新；如果仍失败，请重新自动读取凭据。";
                case "error_technical_detail": return "技术细节";
                default: return key;
            }
        }

        private static string English(string key)
        {
            if (key == "关于描述") return "A Windows floating widget for Codex remaining usage, reset time, reset cards, and model IQ.";
            if (key == "联系作者") return "Contact";
            if (key == "Github") return "GitHub";
            switch (key)
            {
                case "刷新中": return "Refreshing";
                case "剩余": return "Remaining";
                case "已用": return "Used";
                case "重置": return "Reset";
                case "重置卡": return "Reset Card";
                case "连接失败": return "Connection Failed";
                case "剩余用量": return "Remaining Usage";
                case "详情标题": return "Codex Remaining & Reset";
                case "用户": return "User";
                case "账户标签": return "Account:";
                case "即将于": return "Resets in";
                case "后重置": return "";
                case "显示详情": return "Show Details";
                case "隐藏详情": return "Hide Details";
                case "刷新": return "Refresh";
                case "设置": return "Settings";
                case "重置悬浮窗位置": return "Reset Floating Position";
                case "帮助": return "Help";
                case "关于": return "About";
                case "检查更新": return "Check Updates";
                case "退出": return "Exit";
                case "外观": return "Appearance";
                case "快捷设置": return "Behavior";
                case "滚动数据": return "Scroll Data";
                case "scroll_5h": return "5h Remaining & Reset";
                case "scroll_weekly": return "Weekly Remaining & Reset";
                case "scroll_gpt_55_xhigh": return "GPT-5.5-XHigh IQ";
                case "scroll_gpt_55_high": return "GPT-5.5-High IQ";
                case "scroll_gpt_55_medium": return "GPT-5.5-Medium IQ";
                case "scroll_gpt_54_xhigh": return "GPT-5.4-XHigh IQ";
                case "开机自动启动": return "Auto Start";
                case "总是置顶": return "Always On Top";
                case "鼠标穿透": return "Mouse Click-Through";
                case "锁定窗口位置": return "Lock Position";
                case "显示账户名": return "Show Account";
                case "不透明度": return "Opacity";
                case "语言": return "Language";
                case "CodexFloat 设置": return "CodexFloat Settings";
                case "用量接口": return "Usage Endpoint";
                case "刷新秒数": return "Refresh Seconds";
                case "凭据": return "Credentials";
                case "自动读取": return "Auto Import";
                case "凭据提示": return "Credential fields are masked. Leave blank or keep the mask to use existing encrypted credentials.";
                case "保存": return "Save";
                case "取消": return "Cancel";
                case "需要凭据": return "ACCESS_TOKEN and ACCOUNT_ID are required.";
                case "自动读取成功提示": return "Credentials were imported and encrypted in memory. Click Save to write them to local config.";
                case "来源": return "Source";
                case "自动读取成功弹窗": return "ACCESS_TOKEN and ACCOUNT_ID were imported and encrypted in memory.\r\nClick Save to write config.";
                case "无法打开链接": return "Unable to Open Link";
                case "未找到凭据": return "No Codex or ChatGPT credential JSON was found in the default Windows locations.";
                case "找到凭据": return "Credentials Found";
                case "扫描过多": return "Stopped after scanning 900 JSON files. Use manual input if credentials were not found.";
                case "更新时间": return "Updated";
                case "计划": return "Plan";
                case "积分": return "Credits";
                case "更新": return "Updated";
                case "关于 CodexFloat": return "About CodexFloat";
                case "关于描述": return "A Windows floating widget for Codex remaining usage, reset time, and reset-card expiry.";
                case "联系作者": return "Contact / GitHub Profile";
                case "项目 GitHub": return "Project GitHub";
                case "接口注意": return "Note: backend-api is not a public stable API contract. Fields and availability may change with official clients.";
                case "查看错误日志": return "View Error Logs";
                case "错误代码": return "Error Code";
                case "查看错误日志提示": return "Right-click to open error logs";
                case "读取中": return "Loading";
                case "连接中": return "Connecting";
                case "同步短": return "SYNC";
                case "连接短": return "API";
                case "更新中省略": return "Updating...";
                case "env_safety_title": return "Environment Safety";
                case "env_check_on_startup": return "Check Environment On Startup";
                case "env_confirm_medium_on_manual": return "Confirm Medium Risk On Manual Refresh";
                case "env_recheck_high_on_manual": return "Recheck High Risk On Manual Refresh";
                case "env_trusted_library": return "Trusted Address Library...";
                case "env_trusted_library_hint": return "Confirmed safe IP addresses and locations are stored here. Select stale records to delete them.";
                case "env_confirmed_at": return "Confirmed At";
                case "env_delete_selected": return "Delete Selected";
                case "env_location_label": return "Location";
                case "env_badge_safe": return "Safe";
                case "env_badge_medium": return "Medium";
                case "env_badge_high": return "High";
                case "env_safe_title": return "Environment Check Safe";
                case "env_safe_message": return "The current network environment matches a trusted record. Querying will continue.";
                case "env_initial_trust_title": return "Confirm Default Trusted Environment";
                case "env_initial_trust_message": return "No trusted address library record exists yet. Confirm whether to set the current IP and location as trusted and continue querying.";
                case "env_initial_trust_pending_message": return "Waiting for default trusted environment confirmation before manual refresh querying.";
                case "env_initial_trust_continue": return "Trust this environment\r\nContinue querying";
                case "env_initial_trust_wait": return "Verify network first\r\nRefresh manually";
                case "env_medium_risk_title": return "Current Environment Risk: Medium";
                case "env_medium_risk_changed": return "The current IP or location is not in the trusted address library. Confirm whether to continue querying.";
                case "env_medium_risk_first_confirm": return "No confirmed environment record exists yet. Confirm whether to trust the current environment and continue querying.";
                case "env_medium_pending_message": return "Waiting for manual environment confirmation before querying.";
                case "env_popup_confirm": return "OK";
                case "env_info_unavailable": return "No valid IP or location could be detected.";
                case "env_mini_risk": return "Risk Env.";
                case "env_mini_check_failed": return "Env Fail";
                case "env_confirm_continue": return "Confirm network is safe\r\nContinue querying";
                case "env_wait_manual": return "Verify network first\r\nRefresh manually";
                case "env_high_risk_title": return "Current Environment Risk: High";
                case "env_high_risk_message": return "The IP location is China or Hong Kong, so connections and queries have been stopped. Update the environment and refresh manually.";
                case "env_high_risk_wait_message": return "High-risk environment blocked querying. Update the environment and refresh manually.";
                case "env_check_failed_title": return "Environment Check Failed";
                case "env_check_failed_message": return "CodexFloat could not verify the current network and IP, so it paused ChatGPT API connections. Check the network and refresh manually.";
                case "error_click_details": return "Details";
                case "error_missing_credentials_summary": return "Not Signed In";
                case "error_missing_credentials_detail": return "No Codex login credentials were found. Start Codex and make sure you are signed in, then use Auto Import in Settings or manually save ACCESS_TOKEN and ACCOUNT_ID.";
                case "error_auth_summary": return "Login Expired";
                case "error_auth_detail": return "The saved ACCESS_TOKEN is expired or was rejected by the service. Start Codex to refresh the login state, then auto-import and save credentials again in Settings.";
                case "error_network_summary": return "Network Unavailable";
                case "error_network_detail": return "Unable to connect to the ChatGPT/Codex backend. Check network, proxy, or firewall settings, then refresh.";
                case "error_connection_interrupted_summary": return "Connection Interrupted";
                case "error_connection_interrupted_detail": return "The ChatGPT/Codex backend connection was closed temporarily. This is often caused by sleep/wake, network or proxy changes, or HTTPS connection reuse. The app already retried automatically; refresh again later if it still fails.";
                case "error_timeout_summary": return "Connection Timed Out";
                case "error_timeout_detail": return "The request to the ChatGPT/Codex backend timed out. Check network or proxy status, then refresh.";
                case "error_server_summary": return "Service Unavailable";
                case "error_server_detail": return "The ChatGPT/Codex backend is temporarily unavailable. Refresh again later.";
                case "error_api_changed_summary": return "API May Have Changed";
                case "error_api_changed_detail": return "The response format or endpoint may have changed, and this version cannot parse it. Check for updates.";
                case "error_credential_config_summary": return "Credential Decrypt Failed";
                case "error_credential_config_detail": return "The local encrypted credentials cannot be decrypted by the current Windows user. Auto-import or manually save credentials again in Settings.";
                case "error_http_summary": return "Request Rejected";
                case "error_http_detail": return "The service rejected the current request.";
                case "error_unknown_summary": return "Read Failed";
                case "error_unknown_detail": return "Failed to read Codex usage. Start Codex and confirm you are signed in, then refresh; if it still fails, auto-import credentials again.";
                case "error_technical_detail": return "Technical Detail";
                default: return key;
            }
        }
    }

    internal static class IconFactory
    {
        public static Icon CreateTrayIcon()
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "CodexFloat.ico");
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath, new Size(32, 32));
            }

            try
            {
                var associated = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (associated != null) return new Icon(associated, new Size(32, 32));
            }
            catch
            {
            }

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
