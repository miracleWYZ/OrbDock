using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrbDock.Models;
using OrbDock.Services;

namespace OrbDock.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly SettingsStore _store;
        private readonly ThemeService _theme;
        private readonly DockSettings _s;
        private readonly List<DockItem> _items = new List<DockItem>();
        private bool _loading;
        private bool _recording;
        private readonly DispatcherTimer _saveTimer;

        public event Action HotkeyChanged;

        public SettingsWindow(SettingsStore store, ThemeService theme)
        {
            _store = store;
            _theme = theme;
            _s = store.Current;

            InitializeComponent();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); Commit(); };

            LoadMonitors();
            _loading = true;
            try { WireAll(); }
            finally { _loading = false; }
            ApplyAccentBrush();
            Nav.SelectionChanged += (s, e) => ShowPage(Nav.SelectedIndex);
            Nav.SelectedIndex = 0;
            ShowPage(0);
            Loaded += (s, e) => UiUtil.BringToFront(this);
            Reload();

            BtnClose.Click += (s, e) => Close();
            BtnReset.Click += (s, e) => ResetAppearance();
            BtnRecord.Click += (s, e) => ToggleRecord();

            LstItems.SelectionChanged += (s, e) => LoadEditor(LstItems.SelectedItem as DockItem);
            BtnAddApp.Click += (s, e) => AddFromCatalog();
            BtnAddFile.Click += (s, e) => AddFromDialog(false);
            BtnAddFolder.Click += (s, e) => AddFromDialog(true);
            BtnAddUrl.Click += (s, e) => AddUrl();
            BtnUp.Click += (s, e) => MoveItem(-1);
            BtnDown.Click += (s, e) => MoveItem(1);
            BtnDel.Click += (s, e) => DeleteItem();
            BtnApplyItem.Click += (s, e) => ApplyEditor();
            BtnImportDesktop.Click += (s, e) => ImportDesktopIcons();
            BtnBrowseTarget.Click += (s, e) => BrowseTarget();
            BtnBrowseIcon.Click += (s, e) => BrowseIcon();

            BtnAccent.Click += (s, e) => PickColor(AccentSwatch, TxtAccentHex, () => _s.AccentColor, v =>
            {
                _s.AccentColor = v;
                _s.FollowSystemAccent = false;
                ChkFollowAccent.IsChecked = false;
            });
            BtnAccentPick.Click += (s, e) =>
            {
                _s.AccentColor = ColorUtil.ToHex(_theme.SystemAccent);
                AccentSwatch.Background = new SolidColorBrush(_theme.SystemAccent);
                TxtAccentHex.Text = _s.AccentColor;
                ChkFollowAccent.IsChecked = true;
                ScheduleSave();
            };
            BtnLabelFg.Click += (s, e) => PickColor(null, TxtLabelFg, () => _s.LabelForeground, v => _s.LabelForeground = v);
            BtnLabelBg.Click += (s, e) => PickColor(null, TxtLabelBg, () => _s.LabelBackground, v => _s.LabelBackground = v);

            BtnBgImage.Click += (s, e) => PickBackgroundImage();
            BtnClearBgImage.Click += (s, e) =>
            {
                _s.BackgroundImage = "";
                TxBgImage.Text = "（未设置）";
                ScheduleSave();
            };
        }

        // ───────────── 绑定工具 ─────────────

        /// <summary>Win10 风格强调色：用系统主题色，太暗则退回 Windows 默认蓝，保证可读。</summary>
        private void ApplyAccentBrush()
        {
            var c = _theme.SystemAccent;
            if (ColorUtil.Luminance(c) < 0.12) c = Color.FromRgb(0x00, 0x78, 0xD4);
            Resources["Accent"] = new SolidColorBrush(c);
        }

        private void ShowPage(int index)
        {
            var pages = new FrameworkElement[]
            {
                PagePosition, PageSize, PageContent, PageColor, PageLook, PageGeneral
            };
            for (int i = 0; i < pages.Length; i++)
            {
                if (pages[i] == null) continue;
                pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private readonly List<Action> _refreshers = new List<Action>();

        private void RefreshAllControls()
        {
            bool old = _loading;
            _loading = true;
            try { foreach (var r in _refreshers) r(); }
            finally { _loading = old; }
        }

        private void Bind(Slider sl, TextBlock tx, double min, double max,
            Func<double> get, Action<double> set, string fmt = "0.##", string suffix = "")
        {
            sl.Minimum = min;
            sl.Maximum = max;
            sl.SmallChange = (max - min) / 100.0;
            Action refresh = () =>
            {
                double v = Math.Max(min, Math.Min(max, get()));
                sl.Value = v;
                tx.Text = v.ToString(fmt) + suffix;
            };
            refresh();
            _refreshers.Add(refresh);
            sl.ValueChanged += (s, e) =>
            {
                if (_loading) return;
                set(sl.Value);
                tx.Text = sl.Value.ToString(fmt) + suffix;
                ScheduleSave();
            };
        }

        private void BindCheck(CheckBox c, Func<bool> get, Action<bool> set)
        {
            Action refresh = () => c.IsChecked = get();
            refresh();
            _refreshers.Add(refresh);
            c.Checked += (s, e) => { if (_loading) return; set(true); ScheduleSave(); };
            c.Unchecked += (s, e) => { if (_loading) return; set(false); ScheduleSave(); };
        }

        private void BindCombo(ComboBox cmb, Func<string> get, Action<string> set)
        {
            Action refresh = () =>
            {
                string cur = get();
                cmb.SelectedItem = null;
                foreach (var obj in cmb.Items)
                {
                    if (obj is ComboBoxItem it && (it.Tag as string) == cur) { cmb.SelectedItem = it; break; }
                }
                if (cmb.SelectedItem == null && cmb.Items.Count > 0) cmb.SelectedIndex = 0;
            };
            refresh();
            _refreshers.Add(refresh);
            cmb.SelectionChanged += (s, e) =>
            {
                if (_loading) return;
                if (cmb.SelectedItem is ComboBoxItem it) { set(it.Tag as string); ScheduleSave(); }
            };
        }

        private void LoadMonitors()
        {
            CmbMonitor.Items.Clear();
            int n = ScreenHelper.ScreenCount;
            for (int i = 0; i < n; i++)
            {
                CmbMonitor.Items.Add(new ComboBoxItem { Content = ScreenHelper.Describe(i), Tag = i.ToString() });
            }
        }

        private void WireAll()
        {
            BindCombo(CmbEdge, () => _s.Edge.ToString(), v => _s.Edge = ParseEnum(v, DockEdge.Bottom));
            BindCombo(CmbMonitor, () => _s.MonitorIndex.ToString(), v =>
            {
                int i;
                if (int.TryParse(v, out i)) _s.MonitorIndex = i;
            });
            BindCombo(CmbAlign, () => _s.Align.ToString(), v => _s.Align = ParseEnum(v, DockAlign.Center));
            BindCombo(CmbThemeMode, () => _s.ThemeMode, v => _s.ThemeMode = v);
            BindCombo(CmbImageStretch, () => _s.ImageStretch, v => _s.ImageStretch = v);
            BindCombo(CmbBlurMode, () => _s.BlurMode.ToString(), v =>
            {
                int i;
                if (int.TryParse(v, out i)) _s.BlurMode = i;
            });

            Bind(SlOffset, TxOffset, -900, 900, () => _s.Offset, v => _s.Offset = v, "0", " px");
            Bind(SlMargin, TxMargin, 0, 80, () => _s.EdgeMargin, v => _s.EdgeMargin = v, "0", " px");
            Bind(SlHideDelay, TxHideDelay, 0, 2000, () => _s.HideDelayMs, v => _s.HideDelayMs = (int)v, "0", " ms");
            Bind(SlReveal, TxReveal, 0, 16, () => _s.RevealWhenHidden, v => _s.RevealWhenHidden = v, "0", " px");
            BindCheck(ChkAutoHide, () => _s.AutoHide, v => _s.AutoHide = v);

            Bind(SlIconSize, TxIconSize, 20, 120, () => _s.IconSize, v => _s.IconSize = v, "0", " px");
            Bind(SlIconGap, TxIconGap, 0, 40, () => _s.IconGap, v => _s.IconGap = v, "0", " px");
            Bind(SlPadding, TxPadding, 0, 40, () => _s.Padding, v => _s.Padding = v, "0", " px");
            Bind(SlCorner, TxCorner, 0, 60, () => _s.CornerRadius, v => _s.CornerRadius = v, "0");
            Bind(SlMaxVisible, TxMaxVisible, 2, 30, () => _s.MaxVisible, v => _s.MaxVisible = (int)v, "0", " 个");
            Bind(SlHoverScale, TxHoverScale, 1, 1.6, () => _s.HoverScale, v => _s.HoverScale = v, "0.00", "×");
            BindCheck(ChkNeighbor, () => _s.NeighborMagnify, v => _s.NeighborMagnify = v);

            BindCheck(ChkShowLabels, () => _s.ShowLabels, v => _s.ShowLabels = v);
            BindCheck(ChkShowDot, () => _s.ShowRunningDot, v => _s.ShowRunningDot = v);
            BindCheck(ChkActivateRunning, () => _s.ActivateRunning, v => _s.ActivateRunning = v);
            BindCheck(ChkShowSettingsBtn, () => _s.ShowSettingsButton, v => _s.ShowSettingsButton = v);

            BindCheck(ChkFollowAccent, () => _s.FollowSystemAccent, v => _s.FollowSystemAccent = v);
            BindCheck(ChkFollowTheme, () => _s.FollowSystemTheme, v =>
            {
                _s.FollowSystemTheme = v;
                if (v) _s.ThemeMode = "auto";
            });
            Bind(SlAccentStrength, TxAccentStrength, 0, 1, () => _s.AccentStrength, v => _s.AccentStrength = v, "0.00");
            Bind(SlTintStrength, TxTintStrength, 0, 1, () => _s.TintStrength, v => _s.TintStrength = v, "0.00");

            BindCheck(ChkRealBlur, () => _s.EnableRealBlur, v => _s.EnableRealBlur = v);
            Bind(SlOpacity, TxOpacity, 0, 1, () => _s.GlassOpacity, v => _s.GlassOpacity = v, "0.00");
            Bind(SlHighlight, TxHighlight, 0, 2, () => _s.HighlightStrength, v => _s.HighlightStrength = v, "0.00");
            Bind(SlNoise, TxNoise, 0, 0.3, () => _s.NoiseAmount, v => _s.NoiseAmount = v, "0.000");
            BindCheck(ChkShadow, () => _s.ShowShadow, v => _s.ShowShadow = v);
            BindCheck(ChkInnerGlow, () => _s.ShowInnerGlow, v => _s.ShowInnerGlow = v);
            Bind(SlImageOpacity, TxImageOpacity, 0, 1, () => _s.ImageOpacity, v => _s.ImageOpacity = v, "0.00");
            Bind(SlImageBlur, TxImageBlur, 0, 60, () => _s.ImageBlur, v => _s.ImageBlur = v, "0", " px");

            BindCombo(CmbLayer, () => _s.Layer.ToString(), v => _s.Layer = ParseEnum(v, DockLayer.Desktop));
            BindCheck(ChkHideDesktopIcons, () => _s.HideDesktopIcons, v =>
            {
                _s.HideDesktopIcons = v;
                DesktopIcons.SetHidden(v);
            });
            BindCheck(ChkAutoStart, () => _s.AutoStart, v => { _s.AutoStart = v; AutoStart.Set(v); });
            BindCheck(ChkTray, () => _s.ShowTrayIcon, v => _s.ShowTrayIcon = v);
        }

        private static T ParseEnum<T>(string v, T fallback) where T : struct
        {
            T r;
            return Enum.TryParse<T>(v, true, out r) ? r : fallback;
        }

        public void Reload()
        {
            _loading = true;
            try
            {
                if (CmbMonitor.Items.Count > 0)
                    CmbMonitor.SelectedIndex = Math.Max(0, Math.Min(CmbMonitor.Items.Count - 1, _s.MonitorIndex));

                AccentSwatch.Background = new SolidColorBrush(ColorUtil.Parse(_s.AccentColor, Colors.CornflowerBlue));
                TxtAccentHex.Text = _s.AccentColor;
                TxtLabelFg.Text = _s.LabelForeground;
                TxtLabelBg.Text = _s.LabelBackground;
                TxBgImage.Text = string.IsNullOrWhiteSpace(_s.BackgroundImage) ? "（未设置）" : _s.BackgroundImage;
                TxtHotkey.Text = _s.EmergencyHotkey;
                ChkAutoStart.IsChecked = AutoStart.IsEnabled();

                RefreshList();
            }
            finally { _loading = false; }
        }

        // ───────────── 保存 ─────────────

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Commit()
        {
            SettingsStore.Normalize(_s);
            _store.Save(_s);
        }

        private void ResetAppearance()
        {
            var d = new DockSettings();
            _s.IconSize = d.IconSize;
            _s.IconGap = d.IconGap;
            _s.Padding = d.Padding;
            _s.CornerRadius = d.CornerRadius;
            _s.MaxVisible = d.MaxVisible;
            _s.HoverScale = d.HoverScale;
            _s.NeighborMagnify = d.NeighborMagnify;
            _s.GlassOpacity = d.GlassOpacity;
            _s.EnableRealBlur = d.EnableRealBlur;
            _s.BlurMode = d.BlurMode;
            _s.NoiseAmount = d.NoiseAmount;
            _s.HighlightStrength = d.HighlightStrength;
            _s.ShowShadow = d.ShowShadow;
            _s.ShowInnerGlow = d.ShowInnerGlow;
            _s.AccentStrength = d.AccentStrength;
            _s.TintStrength = d.TintStrength;
            _s.FollowSystemAccent = d.FollowSystemAccent;
            _s.FollowSystemTheme = d.FollowSystemTheme;
            _s.ThemeMode = d.ThemeMode;
            _s.BackgroundImage = "";
            _s.ImageOpacity = d.ImageOpacity;
            _s.ImageBlur = d.ImageBlur;
            _s.ImageStretch = d.ImageStretch;
            _s.LabelBackground = "";
            _s.LabelForeground = "";

            // 重新绑定界面数值
            RefreshAllControls();
            AccentSwatch.Background = new SolidColorBrush(ColorUtil.Parse(_s.AccentColor, Colors.CornflowerBlue));
            TxtAccentHex.Text = _s.AccentColor;
            TxtLabelFg.Text = _s.LabelForeground;
            TxtLabelBg.Text = _s.LabelBackground;
            TxBgImage.Text = "（未设置）";
            _store.Save(_s);
            MessageBox.Show("已恢复默认外观。", "OrbDock",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ───────────── 颜色 / 文件 ─────────────

        private void PickColor(Border swatch, TextBox hex, Func<string> get, Action<string> set)
        {
            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                dlg.FullOpen = true;
                try { dlg.Color = System.Drawing.ColorTranslator.FromHtml(
                    string.IsNullOrWhiteSpace(get()) ? "#3D7EFF" : get()); } catch { }
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    string v = string.Format("#{0:X2}{1:X2}{2:X2}", dlg.Color.R, dlg.Color.G, dlg.Color.B);
                    set(v);
                    if (hex != null) hex.Text = v;
                    if (swatch != null) swatch.Background = new SolidColorBrush(
                        Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
                    ScheduleSave();
                }
            }
        }

        private void PickBackgroundImage()
        {
            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件 (*.*)|*.*";
                dlg.Title = "选择背景板图片";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _s.BackgroundImage = dlg.FileName;
                    TxBgImage.Text = dlg.FileName;
                    ScheduleSave();
                }
            }
        }

        // ───────────── 内容列表 ─────────────

        private void RefreshList()
        {
            _items.Clear();
            _items.AddRange(_s.Items);
            int keep = LstItems.SelectedIndex;
            LstItems.ItemsSource = null;
            LstItems.ItemsSource = _items.ToList();
            if (keep >= 0 && keep < _items.Count) LstItems.SelectedIndex = keep;
            else if (_items.Count > 0) LstItems.SelectedIndex = 0;
        }

        private void LoadEditor(DockItem item)
        {
            bool has = item != null;
            TxtName.IsEnabled = TxtTarget.IsEnabled = TxtArgs.IsEnabled = TxtIcon.IsEnabled = has;
            CmbKind.IsEnabled = ChkAdmin.IsEnabled = ChkEnabled.IsEnabled = has;
            BtnApplyItem.IsEnabled = BtnBrowseTarget.IsEnabled = BtnBrowseIcon.IsEnabled = has;
            if (!has)
            {
                TxtName.Text = TxtTarget.Text = TxtArgs.Text = TxtIcon.Text = "";
                return;
            }
            TxtName.Text = item.Name;
            TxtTarget.Text = item.Target;
            TxtArgs.Text = item.Arguments;
            TxtIcon.Text = item.IconPath;
            ChkAdmin.IsChecked = item.RunAsAdmin;
            ChkEnabled.IsChecked = item.Enabled;
            foreach (var obj in CmbKind.Items)
            {
                if (obj is ComboBoxItem it && (it.Tag as string) == item.Kind.ToString())
                {
                    CmbKind.SelectedItem = it;
                    break;
                }
            }
        }

        private void ApplyEditor()
        {
            var item = LstItems.SelectedItem as DockItem;
            if (item == null) return;
            item.Name = string.IsNullOrWhiteSpace(TxtName.Text)
                ? Path.GetFileNameWithoutExtension(TxtTarget.Text)
                : TxtName.Text.Trim();
            item.Target = TxtTarget.Text.Trim();
            item.Arguments = TxtArgs.Text;
            item.IconPath = TxtIcon.Text.Trim();
            item.RunAsAdmin = ChkAdmin.IsChecked == true;
            item.Enabled = ChkEnabled.IsChecked != false;
            if (CmbKind.SelectedItem is ComboBoxItem it) item.Kind = ParseEnum(it.Tag as string, ItemKind.Launch);

            IconHelper.ClearCache();
            ShortcutResolver.ClearCache();
            RefreshList();
            Commit();
        }

        private void AddFromCatalog()
        {
            var picker = new PickerWindow { Owner = this };
            picker.LoadCatalog(_s.Items.Select(i => i.Target));
            if (picker.ShowDialog() != true || picker.SelectedPaths.Count == 0) return;

            int added = 0, skipped = 0;
            DockItem last = null;
            for (int i = 0; i < picker.SelectedPaths.Count; i++)
            {
                var path = picker.SelectedPaths[i];
                var name = i < picker.SelectedNames.Count ? picker.SelectedNames[i] : null;

                if (IsAlreadyInDock(path)) { skipped++; continue; }

                var info = ShortcutResolver.Resolve(path);
                var item = new DockItem
                {
                    Name = string.IsNullOrWhiteSpace(name)
                        ? Path.GetFileNameWithoutExtension(path) : name,
                    Target = path,
                    Kind = ItemKind.Launch
                };
                if (!string.IsNullOrWhiteSpace(info.WorkingDirectory)) item.WorkingDirectory = info.WorkingDirectory;
                _s.Items.Add(item);
                last = item;
                added++;
            }

            RefreshList();
            if (last != null) LstItems.SelectedItem = last;
            Commit();

            if (skipped > 0)
            {
                MessageBox.Show(
                    string.Format("已添加 {0} 个程序，跳过 {1} 个（已经在 Dock 里了）。", added, skipped),
                    "OrbDock", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private bool IsAlreadyInDock(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            foreach (var it in _s.Items)
            {
                if (string.Equals(it.Target, target, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>把桌面上的快捷方式/文件/文件夹全部导入 Dock。</summary>
        private void ImportDesktopIcons()
        {
            int skipped;
            int added = DesktopImporter.ImportInto(_s, out skipped);
            RefreshList();
            Commit();
            MessageBox.Show(
                string.Format("已从桌面导入 {0} 项，跳过 {1} 项（已经在 Dock 里了）。", added, skipped),
                "OrbDock", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AddFromDialog(bool folder)
        {
            if (folder)
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "选择文件夹";
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        _s.Items.Add(new DockItem
                        {
                            Name = Path.GetFileName(dlg.SelectedPath.TrimEnd('\\')),
                            Target = dlg.SelectedPath,
                            Kind = ItemKind.Folder
                        });
                        RefreshList();
                        Commit();
                    }
                }
                return;
            }

            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Filter = "所有文件 (*.*)|*.*";
                dlg.Title = "选择文件（可按住 Ctrl / Shift 多选）";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    DockItem last = null;
                    foreach (var f in dlg.FileNames)
                    {
                        if (IsAlreadyInDock(f)) continue;
                        var item = new DockItem
                        {
                            Name = Path.GetFileNameWithoutExtension(f),
                            Target = f,
                            Kind = ItemKind.Launch
                        };
                        _s.Items.Add(item);
                        last = item;
                    }
                    RefreshList();
                    if (last != null) LstItems.SelectedItem = last;
                    Commit();
                }
            }
        }

        private void AddUrl()
        {
            var item = new DockItem { Name = "新网址", Target = "https://", Kind = ItemKind.Url };
            _s.Items.Add(item);
            RefreshList();
            LstItems.SelectedItem = item;
            LoadEditor(item);
            TxtTarget.Focus();
            TxtTarget.SelectAll();
        }

        private void MoveItem(int dir)
        {
            int i = LstItems.SelectedIndex;
            if (i < 0) return;
            int j = i + dir;
            if (j < 0 || j >= _s.Items.Count) return;
            var tmp = _s.Items[i];
            _s.Items[i] = _s.Items[j];
            _s.Items[j] = tmp;
            _store.Save(_s);
            RefreshList();
            LstItems.SelectedIndex = j;
        }

        private void DeleteItem()
        {
            int i = LstItems.SelectedIndex;
            if (i < 0) return;
            var item = _s.Items[i];
            if (MessageBox.Show("确定要从 Dock 中移除“" + item.Name + "”吗？", "OrbDock",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _s.Items.RemoveAt(i);
            RefreshList();
            Commit();
        }

        private void BrowseTarget()
        {
            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Filter = "所有文件 (*.*)|*.*";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) TxtTarget.Text = dlg.FileName;
            }
        }

        private void BrowseIcon()
        {
            using (var dlg = new System.Windows.Forms.OpenFileDialog())
            {
                dlg.Filter = "图标 (*.ico;*.png;*.jpg;*.exe;*.dll)|*.ico;*.png;*.jpg;*.exe;*.dll|所有文件 (*.*)|*.*";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) TxtIcon.Text = dlg.FileName;
            }
        }

        // ───────────── 热键录制 ─────────────

        private void ToggleRecord()
        {
            _recording = !_recording;
            if (_recording)
            {
                TxtHotkey.Text = "请按下组合键…（Esc 取消）";
                BtnRecord.Content = "取消录制";
                Focus();
                Keyboard.Focus(this);
            }
            else
            {
                TxtHotkey.Text = _s.EmergencyHotkey;
                BtnRecord.Content = "录制按键";
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!_recording) { base.OnPreviewKeyDown(e); return; }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                _recording = false;
                TxtHotkey.Text = _s.EmergencyHotkey;
                BtnRecord.Content = "录制按键";
                e.Handled = true;
                return;
            }
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin ||
                key == Key.System || key == Key.None)
            {
                e.Handled = true;
                return;
            }

            int vk = KeyInterop.VirtualKeyFromKey(key);
            string name = ((System.Windows.Forms.Keys)vk).ToString();
            var mods = Keyboard.Modifiers;
            var parts = new List<string>();
            if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((mods & ModifierKeys.Windows) != 0) parts.Add("Win");
            parts.Add(name);
            string gesture = string.Join("+", parts);

            string err;
            if (!HotkeyService.TryParse(gesture, out err))
            {
                TxHotkeyState.Text = "无法识别：" + err;
                TxHotkeyState.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x2B, 0x2B));
                e.Handled = true;
                return;
            }
            _s.EmergencyHotkey = gesture;
            TxtHotkey.Text = gesture;
            _recording = false;
            BtnRecord.Content = "录制按键";
            TxHotkeyState.Text = "已设为 " + gesture + "（立即生效）";
            TxHotkeyState.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x7F, 0x3B));
            Commit();
            HotkeyChanged?.Invoke();
            e.Handled = true;
        }
    }
}
