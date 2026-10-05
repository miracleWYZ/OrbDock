using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using OrbDock.Controls;
using OrbDock.Interop;
using OrbDock.Models;
using OrbDock.Services;

namespace OrbDock.Views
{
    public partial class DockWindow : Window
    {
        private readonly SettingsStore _store;
        private readonly ThemeService _theme;
        private readonly Launcher.ProcessWatcher _watcher;
        private BitmapSource _glassCapture;

        private readonly List<DockIconControl> _icons = new List<DockIconControl>();
        private readonly List<double> _cellWidths = new List<double>();
        private readonly List<DockItem> _iconItems = new List<DockItem>();

        private readonly DispatcherTimer _hideTimer;
        private readonly DispatcherTimer _topmostTimer;
        private readonly Ticker _slideTicker = new Ticker();
        private readonly Ticker _scrollTicker = new Ticker();

        private DockIconControl _hovered;
        private bool _shown;
        private bool _dragging;
        private double _scrollOffset;
        private double _scrollMax;
        private string _iconSignature = "";

        // 几何（DIP）
        private double _winW, _winH;
        private double _pillX, _pillY, _pillW, _pillH, _pillRadius;
        private double _slideHidden;
        private bool _horizontal = true;

        public event Action SettingsRequested;
        public event Action EmergencyHotkeyPressed;

        /// <summary>是否需要监听全局鼠标位置（Dock 收起时为 true）。</summary>
        public event Action<bool> HotZoneWatchChanged;

        private void SetHotZoneWatch(bool on)
        {
            try { HotZoneWatchChanged?.Invoke(on); } catch { }
        }

        /// <summary>收起时整窗鼠标穿透（WS_EX_TRANSPARENT），保证不阻挡其它应用。</summary>
        private void SetClickThrough(bool on)
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;
                int ex = NativeMethods.GetWindowLong(h, NativeMethods.GWL_EXSTYLE);
                int want = on
                    ? (ex | NativeMethods.WS_EX_TRANSPARENT)
                    : (ex & ~NativeMethods.WS_EX_TRANSPARENT);
                if (want != ex) NativeMethods.SetWindowLong(h, NativeMethods.GWL_EXSTYLE, want);
            }
            catch { }
        }

        /// <summary>来自全局鼠标钩子（非 UI 线程），用于把收起的 Dock 唤出来。</summary>
        public void OnGlobalMouseMove(int x, int y)
        {
            if (_shown || _dragging) return;
            if (!_store.Current.AutoHide) return;
            if (!InHotRect(x, y)) return;
            if (Interlocked.Exchange(ref _pendingShow, 1) == 1) return;
            Debug.Marker("hot-zone summon by mouse hook");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _pendingShow, 0);
                if (!_shown) ShowDock(true);
            }));
        }

        private int _pendingShow;

        public bool IsShown => _shown;

        public DockWindow(SettingsStore store, ThemeService theme, Launcher.ProcessWatcher watcher)
        {
            _store = store;
            _theme = theme;
            _watcher = watcher;

            InitializeComponent();

            MouseEnter += (s, e) => OnPointerEnter();
            MouseLeave += (s, e) => OnPointerLeave();
            PreviewMouseRightButtonUp += OnRightButtonUp;
            Scroller.ScrollChanged += Scroller_ScrollChanged;
            Scroller.SizeChanged += (s, e) => UpdateScrollMask();

            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _hideTimer.Tick += (s, e) => CheckAutoHide();
            _hideTimer.Start();

            _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _topmostTimer.Tick += (s, e) => ReassertTopmost();

            _store.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(() => ReloadAll()));
            _theme.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(() => { ApplyAppearance(); PushThemeToIcons(); }));

            Loaded += (s, e) =>
            {
                _topmostTimer.Start();
            };
            Closed += (s, e) => { _topmostTimer.Stop(); _hideTimer.Stop(); };
            SourceInitialized += OnSourceInitialized;
        }

        // ───────────────────────── 初始化 ─────────────────────────

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

            var src = HwndSource.FromHwnd(hwnd);
            src?.AddHook(WndProc);

            Log.Info("Dock 窗口已创建");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                handled = true;
                EmergencyHotkeyPressed?.Invoke();
            }
            return IntPtr.Zero;
        }

        public IntPtr Handle => new WindowInteropHelper(this).Handle;

        // ───────────────────────── 重建内容 ─────────────────────────

        public void ReloadAll()
        {
            var s = _store.Current;
            string sig = string.Join("|", s.IconSize, s.IconGap, s.ShowRunningDot,
                s.ShowSettingsButton, s.Items.Count, IconSignature(s));
            if (sig != _iconSignature)
            {
                _iconSignature = sig;
                BuildItems();
            }
            else
            {
                foreach (var c in _icons) c.SetRunning(s.ShowRunningDot && !c.IsSpecial && _watcher.IsRunning(c.Item));
            }

            ApplyAppearance();
            UpdateGeometry(true);
            PushThemeToIcons();
            UpdateScrollMask();
        }

        private static string IconSignature(DockSettings s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var i in s.Items)
            {
                sb.Append(i.Id).Append(':').Append(i.Target).Append(':')
                  .Append(i.IconPath).Append(':').Append(i.Enabled ? 1 : 0).Append(';');
            }
            return sb.ToString();
        }

        private void BuildItems()
        {
            var s = _store.Current;
            double scale = CurrentScale();
            double iconSize = s.IconSize;
            double gap = s.IconGap;
            double cellW = iconSize + gap;

            ItemsPanel.Children.Clear();
            _icons.Clear();
            _cellWidths.Clear();
            _iconItems.Clear();
            _hovered = null;
            int realIcons = 0;
            _emptyHint = null;

            foreach (var item in s.Items)
            {
                if (!item.Enabled) continue;

                if (item.Kind == ItemKind.Separator)
                {
                    var sep = new Border
                    {
                        Width = 1,
                        Height = Math.Max(10, iconSize * 0.55),
                        Margin = new Thickness(6, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                        SnapsToDevicePixels = true
                    };
                    ItemsPanel.Children.Add(sep);
                    _cellWidths.Add(13);
                    _iconItems.Add(item);
                    continue;
                }

                var ctl = new DockIconControl(item, iconSize, cellW, gap, null, false, null, s.ShowRunningDot);
                WireIcon(ctl);
                ItemsPanel.Children.Add(ctl);
                _icons.Add(ctl);
                _cellWidths.Add(cellW);
                _iconItems.Add(item);
                realIcons++;
            }

            if (s.ShowSettingsButton)
            {
                var gear = new DockIconControl(null, iconSize, cellW, gap, null, true, "settings", false);
                gear.Clicked += c => SettingsRequested?.Invoke();
                WireIcon(gear);
                ItemsPanel.Children.Add(gear);
                _icons.Add(gear);
                _cellWidths.Add(cellW);
                _iconItems.Add(null);
            }
            if (realIcons == 0) AddEmptyHint();

            PushThemeToIcons();
        }

        private TextBlock _emptyHint;

        private void AddEmptyHint()
        {
            _emptyHint = new TextBlock
            {
                Text = "右键 Dock → 添加程序",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                Opacity = 0.9
            };
            ItemsPanel.Children.Add(_emptyHint);
            _cellWidths.Add(190);
            _iconItems.Add(null);
        }

        private void WireIcon(DockIconControl ctl)
        {
            ctl.HoverChanged += c => Dispatcher.BeginInvoke(new Action(() => UpdateHover(c)));
            ctl.Clicked += c => OnIconClicked(c);
        }

        private void OnIconClicked(DockIconControl c)
        {
            if (c.IsSpecial)
            {
                if (c.SpecialAction == "settings") SettingsRequested?.Invoke();
                return;
            }
            if (c.Item == null) return;
            if (_store.Current.LaunchEffect == "bounce") c.Bounce();
            Launcher.Launch(c.Item, this);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                t.Tick += (s, e) => { t.Stop(); UpdateRunningDots(); };
                t.Start();
            }));
        }

        private void UpdateRunningDots()
        {
            var s = _store.Current;
            foreach (var c in _icons)
            {
                if (c.IsSpecial) continue;
                c.SetRunning(s.ShowRunningDot && _watcher.IsRunning(c.Item));
            }
        }

        private void PushThemeToIcons()
        {
            var s = _store.Current;
            var pal = CurrentPalette();
            Color dot = pal.RunningDot;
            foreach (var c in _icons)
            {
                c.SetDotColor(dot);
                c.SetShadow(s.ShowShadow);
            }
            if (_emptyHint != null)
            {
                _emptyHint.Foreground = new SolidColorBrush(pal.Light
                    ? Color.FromRgb(0x33, 0x36, 0x3C)
                    : Color.FromRgb(0xE8, 0xEA, 0xF0));
            }
        }

        // ───────────────────────── 外观 ─────────────────────────

        private sealed class Palette
        {
            public Color Accent;
            public Color Base;
            public Color BaseTop;
            public Color BaseBottom;
            public Color RimTop;
            public Color RimBottom;
            public Color Gloss;
            public Color Shadow;
            public Color LabelBg;
            public Color LabelFg;
            public Color RunningDot;
            public bool Light;
            public double Luminance;
        }

        private Palette CurrentPalette()
        {
            var s = _store.Current;
            bool light;
            if (s.ThemeMode == "light") light = true;
            else if (s.ThemeMode == "dark") light = false;
            else light = _theme.SystemUsesLightTheme;

            Color accent = s.FollowSystemAccent
                ? _theme.SystemAccent
                : ColorUtil.Parse(s.AccentColor, _theme.SystemAccent);

            var p = new Palette { Accent = accent, Light = light };

            // 玻璃底色：暗色主题偏冷黑，亮色主题偏白
            double op = s.GlassOpacity;
            if (light)
            {
                p.BaseTop = ColorUtil.Mix(Color.FromRgb(0xFF, 0xFF, 0xFF), accent, 0.12);
                p.BaseBottom = ColorUtil.Mix(Color.FromRgb(0xDF, 0xE4, 0xEE), accent, 0.20);
            }
            else
            {
                p.BaseTop = ColorUtil.Mix(Color.FromRgb(0x16, 0x1A, 0x24), accent, 0.22);
                p.BaseBottom = ColorUtil.Mix(Color.FromRgb(0x05, 0x06, 0x0A), accent, 0.30);
            }
            p.Luminance = ColorUtil.Luminance(p.BaseTop);

            byte baseA = (byte)Math.Round(Math.Max(0, Math.Min(1, op)) * 255);
            p.Base = Color.FromArgb(baseA, p.BaseTop.R, p.BaseTop.G, p.BaseTop.B);

            p.RimTop = light
                ? Color.FromArgb(210, 255, 255, 255)
                : Color.FromArgb(120, 255, 255, 255);
            p.RimBottom = light
                ? Color.FromArgb(70, 120, 130, 150)
                : Color.FromArgb(40, 255, 255, 255);
            p.Gloss = light ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(70, 255, 255, 255);
            p.Shadow = Color.FromArgb(150, 0, 0, 0);

            p.LabelBg = light ? Color.FromArgb(225, 250, 250, 253) : Color.FromArgb(215, 24, 26, 32);
            p.LabelFg = light ? Color.FromRgb(0x16, 0x18, 0x1C) : Color.FromRgb(0xF2, 0xF4, 0xF8);
            p.RunningDot = light ? ColorUtil.Darken(accent, 0.15) : ColorUtil.Lighten(accent, 0.45);
            return p;
        }

        private void ApplyAppearance()
        {
            var s = _store.Current;
            var p = CurrentPalette();
            double r = _pillRadius > 0 ? _pillRadius : 24;
            var radius = new CornerRadius(r);

            Pill.CornerRadius = radius;
            BlurLayer.CornerRadius = radius;
            TintLayer.CornerRadius = radius;
            AccentLayer.CornerRadius = radius;
            NoiseLayer.CornerRadius = radius;
            InnerShade.CornerRadius = radius;
            SheenLayer.CornerRadius = radius;
            GlossLayer.CornerRadius = radius;
            RimLayer.CornerRadius = radius;

            // 主色调：从上到下略微加深
            var tintBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0.25, 1)
            };
            tintBrush.GradientStops.Add(new GradientStop(
                ColorUtil.WithAlpha(ColorUtil.Mix(p.BaseTop, Colors.White, 0.06), s.GlassOpacity * 0.98), 0));
            tintBrush.GradientStops.Add(new GradientStop(
                ColorUtil.WithAlpha(ColorUtil.Mix(p.BaseBottom, Colors.White, s.TintStrength * 0.10), s.GlassOpacity * 0.92), 1));
            TintLayer.Background = tintBrush;

            // 主题色光晕（左上角），液态玻璃的色彩流动感
            var accentBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            accentBrush.GradientStops.Add(new GradientStop(
                ColorUtil.WithAlpha(ColorUtil.Lighten(p.Accent, 0.25), s.AccentStrength * 0.85), 0));
            accentBrush.GradientStops.Add(new GradientStop(
                ColorUtil.WithAlpha(p.Accent, s.AccentStrength * 0.30), 0.55));
            accentBrush.GradientStops.Add(new GradientStop(
                ColorUtil.WithAlpha(ColorUtil.Darken(p.Accent, 0.20), s.AccentStrength * 0.45), 1));
            AccentLayer.Background = accentBrush;

            // 顶部高光（玻璃厚度）
            var glossBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            glossBrush.GradientStops.Add(new GradientStop(
                ColorUtil.WithAlpha(p.Gloss, 0.85 * s.HighlightStrength), 0));
            glossBrush.GradientStops.Add(new GradientStop(
                ColorUtil.WithAlpha(p.Gloss, 0.10 * s.HighlightStrength), 0.45));
            glossBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            GlossLayer.Background = glossBrush;

            // 内侧下缘阴影（厚度感）
            var shadeBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            shadeBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            shadeBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.55));
            shadeBrush.GradientStops.Add(new GradientStop(
                p.Light ? Color.FromArgb(26, 40, 50, 70) : Color.FromArgb(70, 0, 0, 0), 1));
            InnerShade.Background = shadeBrush;

            // 描边：上亮下暗，模拟玻璃棱边
            var rimBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0.1, 0),
                EndPoint = new Point(0.9, 1)
            };
            rimBrush.GradientStops.Add(new GradientStop(p.RimTop, 0));
            rimBrush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(p.RimTop, 0.25), 0.5));
            rimBrush.GradientStops.Add(new GradientStop(p.RimBottom, 1));
            RimLayer.BorderBrush = rimBrush;

            // 噪点（磨砂颗粒）—— 用圆角 Border 的背景画笔，跟着圆角抗锯齿裁切
            NoiseLayer.Background = NoiseBrush();
            NoiseLayer.Opacity = s.NoiseAmount;

            // 投影：收紧一点，避免在半透明胶囊周围形成明显的灰晕
            if (s.ShowShadow)
            {
                PillShadow.Opacity = p.Light ? 0.22 : 0.42;
                PillShadow.BlurRadius = 16;
                PillShadow.ShadowDepth = 3;
                PillShadow.Color = Colors.Black;
            }
            else
            {
                PillShadow.Opacity = 0;
            }

            // 背景图片
            var img = LoadBackgroundImage(s.BackgroundImage);
            if (img != null)
            {
                BgImage.Source = img;
                BgImage.Visibility = Visibility.Visible;
                BgImage.Opacity = s.ImageOpacity;
                try { BgImage.Stretch = (Stretch)Enum.Parse(typeof(Stretch), s.ImageStretch, true); }
                catch { BgImage.Stretch = Stretch.UniformToFill; }
                if (s.ImageBlur > 0.5)
                {
                    BgImage.Effect = new BlurEffect { Radius = s.ImageBlur, KernelType = KernelType.Gaussian };
                }
                else BgImage.Effect = null;
            }
            else
            {
                BgImage.Source = null;
                BgImage.Visibility = Visibility.Collapsed;
            }

            // 名称标签
            LabelBox.Background = new SolidColorBrush(
                ColorUtil.Parse(s.LabelBackground, p.LabelBg));
            LabelBox.BorderBrush = new SolidColorBrush(p.Light
                ? Color.FromArgb(60, 0, 0, 0)
                : Color.FromArgb(50, 255, 255, 255));
            LabelBox.BorderThickness = new Thickness(1);
            LabelText.Foreground = new SolidColorBrush(
                ColorUtil.Parse(s.LabelForeground, p.LabelFg));

            // 内部流光：圆角 Border 的径向渐变背景（会随圆角抗锯齿裁切），并做缓慢漂移
            SheenLayer.Opacity = s.ShowInnerGlow ? 0.5 : 0;
            if (s.ShowInnerGlow) ApplySheen(s, p);
        }

        private RadialGradientBrush _sheenBrush;

        private void ApplySheen(DockSettings s, Palette p)
        {
            if (_sheenBrush == null)
            {
                _sheenBrush = new RadialGradientBrush
                {
                    MappingMode = BrushMappingMode.RelativeToBoundingBox,
                    Center = new Point(0.30, 0.45),
                    GradientOrigin = new Point(0.30, 0.45),
                    RadiusX = 0.55,
                    RadiusY = 1.1
                };
                _sheenBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                _sheenBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                SheenLayer.Background = _sheenBrush;

                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                var dur = TimeSpan.FromSeconds(9);
                _sheenBrush.BeginAnimation(RadialGradientBrush.CenterProperty,
                    new PointAnimation(new Point(0.18, 0.35), new Point(0.82, 0.62), dur)
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
                _sheenBrush.BeginAnimation(RadialGradientBrush.GradientOriginProperty,
                    new PointAnimation(new Point(0.12, 0.3), new Point(0.9, 0.7), dur)
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            }

            _sheenBrush.GradientStops[0].Color =
                ColorUtil.WithAlpha(ColorUtil.Lighten(p.Accent, 0.78), 0.22 * s.HighlightStrength);
            _sheenBrush.GradientStops[1].Color = ColorUtil.WithAlpha(p.Accent, 0.0);
        }

        /// <summary>
        /// 抓取胶囊背后的屏幕内容作为毛玻璃底图。必须在 Dock 收起时调用，
        /// 否则会把 Dock 自己拍进去（那时胶囊已经滑出窗口，该区域是透明/穿透的）。
        /// </summary>
        private void CaptureGlass()
        {
            var s = _store.Current;
            if (!s.EnableRealBlur) { BlurLayer.Background = null; return; }

            try
            {
                int w = (int)Math.Round(_pillRectDevice[2] - _pillRectDevice[0]);
                int h = (int)Math.Round(_pillRectDevice[3] - _pillRectDevice[1]);
                if (w < 4 || h < 4) return;

                double radius = s.BlurMode == 1 ? 34 : 20;
                int pad = (int)Math.Ceiling(radius * 2.2);

                var img = GlassCapture.CaptureBlurred(_pillRectDevice[0], _pillRectDevice[1],
                    w, h, pad, radius);
                if (img == null) return;

                _glassCapture = img;
                BlurLayer.Background = new ImageBrush(img) { Stretch = Stretch.Fill };
            }
            catch (Exception ex) { Log.Warn("设置毛玻璃底图失败: " + ex.Message); }
        }

        private ImageBrush _noiseBrush;

        private ImageBrush NoiseBrush()
        {
            if (_noiseBrush != null) return _noiseBrush;
            const int size = 128;
            var rnd = new Random(20240521);
            var pixels = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                byte v = (byte)rnd.Next(0, 256);
                pixels[i * 4 + 0] = v;
                pixels[i * 4 + 1] = v;
                pixels[i * 4 + 2] = v;
                pixels[i * 4 + 3] = (byte)rnd.Next(0, 90);
            }
            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
            bmp.Freeze();
            _noiseBrush = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, size, size),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None
            };
            _noiseBrush.Freeze();
            return _noiseBrush;
        }

        private string _imgPath;
        private BitmapImage _imgCache;

        private ImageSource LoadBackgroundImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (_imgPath == path && _imgCache != null) return _imgCache;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                _imgPath = path;
                _imgCache = bmp;
                return bmp;
            }
            catch (Exception ex)
            {
                Log.Warn("加载背景图片失败: " + ex.Message);
                return null;
            }
        }

        private double CurrentScale()
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleX > 0.1) return dpi.DpiScaleX;
            }
            catch { }
            return 1.0;
        }

        // ───────────────────────── 几何 ─────────────────────────

        public void UpdateGeometry(bool moveWindow)
        {
            var s = _store.Current;
            _horizontal = s.Edge == DockEdge.Bottom || s.Edge == DockEdge.Top;

            double pad = s.Padding;
            double iconSize = s.IconSize;
            double gap = s.IconGap;
            double cellW = iconSize + gap;

            double contentLen = 0;
            for (int i = 0; i < _cellWidths.Count; i++)
            {
                contentLen += _cellWidths[i];
                if (i < _cellWidths.Count - 1) contentLen += gap;
            }

            double maxLen = Math.Max(cellW, s.MaxVisible * cellW - gap);
            double innerLen = Math.Min(contentLen, maxLen);
            if (innerLen <= 0) innerLen = Math.Min(cellW, maxLen);

            double pillLong = innerLen + pad * 2;
            double pillThick = iconSize + pad * 2;
            _pillW = _horizontal ? pillLong : pillThick;
            _pillH = _horizontal ? pillThick : pillLong;
            _pillRadius = s.CornerRadius <= 0.5 ? pillThick / 2.0 : Math.Min(s.CornerRadius, pillThick / 2.0);

            const double side = 24;      // 阴影留白
            const double tail = 6;       // 胶囊与感应条之间的空隙
            double labelBand = s.ShowLabels ? 34 : 8;

            if (_horizontal)
            {
                _winW = _pillW + side * 2;
                if (s.Edge == DockEdge.Bottom)
                {
                    _pillY = labelBand;
                    _winH = labelBand + _pillH + tail + s.EdgeMargin;
                }
                else
                {
                    _pillY = s.EdgeMargin;
                    _winH = s.EdgeMargin + _pillH + tail + labelBand;
                }
                _pillX = side;
            }
            else
            {
                _winH = _pillH + side * 2;
                _pillY = side;
                if (s.Edge == DockEdge.Left)
                {
                    _pillX = s.EdgeMargin;
                    _winW = s.EdgeMargin + _pillW + tail + labelBand;
                }
                else
                {
                    _pillX = labelBand;
                    _winW = labelBand + _pillW + tail + s.EdgeMargin;
                }
            }

            Width = _winW;
            Height = _winH;

            var wa = ScreenHelper.Get(s.MonitorIndex);
            double winLeft, winTop;
            double along = s.Offset;

            switch (s.Edge)
            {
                case DockEdge.Bottom:
                    winTop = wa.Top + wa.Height - _winH;
                    winLeft = AlignAlong(wa.Left, wa.Width, _pillW, s, along) - side;
                    break;
                case DockEdge.Top:
                    winTop = wa.Top;
                    winLeft = AlignAlong(wa.Left, wa.Width, _pillW, s, along) - side;
                    break;
                case DockEdge.Left:
                    winLeft = wa.Left;
                    winTop = AlignAlong(wa.Top, wa.Height, _pillH, s, along) - side;
                    break;
                default:
                    winLeft = wa.Left + wa.Width - _winW;
                    winTop = AlignAlong(wa.Top, wa.Height, _pillH, s, along) - side;
                    break;
            }

            if (moveWindow)
            {
                Left = winLeft;
                Top = winTop;
            }
            var tbDiag = ScreenHelper.TaskbarRect();
            Debug.Marker(string.Format("geo edge={0} align={1} wa={2},{3},{4},{5} scale={6} tb={7} win={8},{9},{10},{11} pill={12},{13},{14},{15}",
                s.Edge, s.Align, wa.Left, wa.Top, wa.Width, wa.Height, wa.Scale,
                tbDiag.HasValue ? tbDiag.Value.ToString() : "null",
                winLeft, winTop, _winW, _winH, _pillX, _pillY, _pillW, _pillH));

            // 胶囊与感应区
            Canvas.SetLeft(Pill, _pillX);
            Canvas.SetTop(Pill, _pillY);
            Pill.Width = _pillW;
            Pill.Height = _pillH;

            Scroller.Margin = _horizontal
                ? new Thickness(pad, 0, pad, 0)
                : new Thickness(0, pad, 0, pad);
            ItemsPanel.Orientation = _horizontal ? Orientation.Horizontal : Orientation.Vertical;

            // 隐藏时的位移量
            if (s.Edge == DockEdge.Bottom)
                _slideHidden = (_winH - s.RevealWhenHidden) - _pillY;
            else if (s.Edge == DockEdge.Top)
                _slideHidden = s.RevealWhenHidden - (_pillY + _pillH);
            else if (s.Edge == DockEdge.Left)
                _slideHidden = s.RevealWhenHidden - (_pillX + _pillW);
            else
                _slideHidden = (_winW - s.RevealWhenHidden) - _pillX;

            // 停靠边可能变了：把位移重新落到正确的轴上（另一边清零），
            // 否则从"顶部"切到"左侧"后，残留的 Y 位移会让胶囊位置错乱
            Slide.X = 0;
            Slide.Y = 0;
            ApplySlideOffset(_shown ? 0 : _slideHidden);

            // 感应条：贴着屏幕边缘的一条窄带，用于把 Dock 唤出来
            double hotBand = Math.Max(16, s.EdgeMargin + 10);
            if (s.Edge == DockEdge.Bottom)
            {
                _hotX = 0; _hotY = Math.Max(0, _winH - hotBand);
                _hotW = _winW; _hotH = Math.Min(hotBand, _winH);
            }
            else if (s.Edge == DockEdge.Top)
            {
                _hotX = 0; _hotY = 0;
                _hotW = _winW; _hotH = Math.Min(hotBand, _winH);
            }
            else if (s.Edge == DockEdge.Left)
            {
                _hotX = 0; _hotY = 0;
                _hotW = Math.Min(hotBand, _winW); _hotH = _winH;
            }
            else
            {
                _hotX = Math.Max(0, _winW - hotBand); _hotY = 0;
                _hotW = Math.Min(hotBand, _winW); _hotH = _winH;
            }
            Canvas.SetLeft(HotZoneEdge, _hotX);
            Canvas.SetTop(HotZoneEdge, _hotY);
            HotZoneEdge.Width = _hotW;
            HotZoneEdge.Height = _hotH;
            // 感应带不参与命中测试（否则会在屏幕底部挡到别的窗口）；
            // 唤出改为"全局鼠标钩子 + 光标位置轮询"，鼠标穿透，零遮挡。
            HotZoneEdge.Visibility = Visibility.Collapsed;

            double hotScale = CurrentScale();
            _hotRectDevice[0] = (winLeft + _hotX) * hotScale;
            _hotRectDevice[1] = (winTop + _hotY) * hotScale;
            _hotRectDevice[2] = (winLeft + _hotX + _hotW) * hotScale;
            _hotRectDevice[3] = (winTop + _hotY + _hotH) * hotScale;
            _pillRectDevice[0] = (winLeft + _pillX) * hotScale;
            _pillRectDevice[1] = (winTop + _pillY) * hotScale;
            _pillRectDevice[2] = (winLeft + _pillX + _pillW) * hotScale;
            _pillRectDevice[3] = (winTop + _pillY + _pillH) * hotScale;

            // 毛玻璃底图位置：_pillRectDevice 已在上面算好，抓屏时按它取景

            UpdateScrollMask();
            ReassertTopmost();
        }

        private static double AlignAlong(double start, double length, double pillLen, DockSettings s, double offset)
        {
            switch (s.Align)
            {
                case DockAlign.Start:
                    return start + s.EdgeMargin + offset;
                case DockAlign.End:
                    return start + length - pillLen - s.EdgeMargin + offset;
                default:
                    return start + (length - pillLen) / 2.0 + offset;
            }
        }

        private void ReassertTopmost()
        {
            ApplyZOrder();
        }

        /// <summary>
        /// 统一管理 z 序：
        ///  · 桌面层（默认）：HWND_BOTTOM —— 永远被应用窗口压住（只露出没被挡的部分），
        ///                    但仍位于桌面壁纸/桌面图标之上，实测最底层紧贴 Progman；
        ///  · 普通窗口：唤出时浮到最前，收起时让位；
        ///  · 始终置顶：永远 HWND_TOPMOST。
        ///  毛玻璃窗永远紧贴 Dock 正下方（仍然在壁纸之上），绝不盖住图标。
        /// </summary>
        private void ApplyZOrder()
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;

                IntPtr insertAfter;
                uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
                switch (_store.Current.Layer)
                {
                    case DockLayer.Topmost:
                        insertAfter = NativeMethods.HWND_TOPMOST;
                        break;
                    case DockLayer.Normal:
                        insertAfter = _shown ? NativeMethods.HWND_TOP : NativeMethods.HWND_NOTOPMOST;
                        break;
                    default:
                        insertAfter = NativeMethods.HWND_BOTTOM;
                        break;
                }

                NativeMethods.SetWindowPos(h, insertAfter, 0, 0, 0, 0, flags);
            }
            catch { }
        }

        // ───────────────────────── 显示 / 隐藏 ─────────────────────────

        public void ShowDock(bool animate)
        {
            bool wasHidden = !_shown;
            _shown = true;
            _lastInside = DateTime.Now;
            SetClickThrough(false);
            SetHotZoneWatch(false);
            Debug.Marker("show animate=" + animate);

            if (!IsVisible) Show();

            // 必须在胶囊滑出之前抓屏：此刻该区域只有桌面，不会把 Dock 自己拍进去
            if (wasHidden || _glassCapture == null) CaptureGlass();

            double from = SlideOffset;
            double to = 0;
            _slideTicker.Start(from, to, animate ? 280 : 1, ApplySlideOffset, () => ApplyZOrder());
            ApplyZOrder();
        }

        public void HideDock(bool animate)
        {
            if (!_shown && !animate) return;
            _shown = false;
            // 收起后整窗鼠标穿透，绝不挡住别的窗口
            SetClickThrough(true);
            SetHotZoneWatch(true);
            Debug.Marker("hide animate=" + animate);
            _hovered = null;
            HideLabel();
            UpdateHover(null);
            ApplyZOrder();

            if (!animate)
            {
                ApplySlideOffset(_slideHidden);

                return;
            }

            _slideTicker.Start(SlideOffset, _slideHidden, 260, ApplySlideOffset, () =>
            {

            });
        }

        /// <summary>
        /// 收起/展开的位移。横向停靠（上/下）走 Y 轴，竖向停靠（左/右）必须走 X 轴，
        /// 否则侧边时胶囊只会上下挪、根本缩不回屏幕外。
        /// </summary>
        private double SlideOffset
        {
            get { return _horizontal ? Slide.Y : Slide.X; }
        }

        private void ApplySlideOffset(double v)
        {
            if (_horizontal) Slide.Y = v; else Slide.X = v;
        }

        public void ToggleDock()
        {
            if (_shown) HideDock(true); else ShowDock(true);
        }

        private void OnPointerEnter()
        {
            // 注意：这里不能停 _hideTimer —— 自动隐藏靠它轮询，停了就再也不会收起
            _lastInside = DateTime.Now;
            if (!_shown) ShowDock(true);
        }

        private void OnPointerLeave()
        {
            UpdateHover(null);
        }

        private DateTime _lastInside = DateTime.Now;
        private bool _menuOpen;
        private double _hotX, _hotY, _hotW, _hotH;

        /// <summary>轮询鼠标：既负责自动隐藏，也负责在 Dock 被别的窗口挡住时把它唤出来。</summary>
        private void CheckAutoHide()
        {
            var s = _store.Current;

            if (!_shown)
            {
                // 隐藏状态下用光标位置判断是否触到屏幕边缘的感应带
                // （这样即使 Dock 被其它窗口覆盖、或没置顶，也一定能唤出）
                if (s.AutoHide && !_dragging && IsCursorInHotZone()) ShowDock(true);
                return;
            }

            if (IsCursorInPill() || IsCursorInHotZone())
            {
                _lastInside = DateTime.Now;
                DiagState();
                return;
            }
            if (!s.AutoHide || _dragging || _menuOpen) return;
            if ((DateTime.Now - _lastInside).TotalMilliseconds < Math.Max(0, s.HideDelayMs)) return;
            HideDock(true);
        }

        private bool IsCursorInHotZone()
        {
            try
            {
                NativeMethods.POINT p;
                if (!NativeMethods.GetCursorPos(out p)) return false;
                return InHotRect(p.X, p.Y);
            }
            catch { return false; }
        }

        /// <summary>
        /// 光标是否停在胶囊上。必须用真实光标坐标判断：
        /// 收起时窗口是鼠标穿透的，WPF 收不到 MouseLeave，IsMouseOver 会一直停在 true。
        /// </summary>
        private bool IsCursorInPill()
        {
            try
            {
                NativeMethods.POINT p;
                if (!NativeMethods.GetCursorPos(out p)) return false;
                var r = _pillRectDevice;
                if (r[2] - r[0] < 2 || r[3] - r[1] < 2) return false;
                return p.X >= r[0] - 4 && p.X <= r[2] + 4 && p.Y >= r[1] - 4 && p.Y <= r[3] + 4;
            }
            catch { return false; }
        }

        private readonly double[] _pillRectDevice = new double[4];
        private DateTime _lastDiag = DateTime.MinValue;

        /// <summary>调试用：为什么没有自动收起。</summary>
        private void DiagState()
        {
            if ((DateTime.Now - _lastDiag).TotalSeconds < 1.0) return;
            _lastDiag = DateTime.Now;
            try
            {
                NativeMethods.POINT p;
                NativeMethods.GetCursorPos(out p);
                Debug.Marker(string.Format(
                    "keep cursor={0},{1} mouseOver={2} inPill={3} inHot={4} pillRect={5},{6},{7},{8} hotRect={9},{10},{11},{12} menu={13} drag={14}",
                    p.X, p.Y, IsMouseOver, IsCursorInPill(), IsCursorInHotZone(),
                    (int)_pillRectDevice[0], (int)_pillRectDevice[1], (int)_pillRectDevice[2], (int)_pillRectDevice[3],
                    (int)_hotRectDevice[0], (int)_hotRectDevice[1], (int)_hotRectDevice[2], (int)_hotRectDevice[3],
                    _menuOpen, _dragging));
            }
            catch { }
        }

        /// <summary>屏幕像素坐标是否落在边缘感应带内（_hotRectDevice 由 UI 线程算好）。</summary>
        private bool InHotRect(double x, double y)
        {
            double l = _hotRectDevice[0], t = _hotRectDevice[1];
            double r = _hotRectDevice[2], b = _hotRectDevice[3];
            if (r - l < 2 || b - t < 2) return false;
            return x >= l - 2 && x <= r + 2 && y >= t - 2 && y <= b + 2;
        }

        private readonly double[] _hotRectDevice = new double[4];

        private void ScheduleAutoHide()
        {
            _lastInside = DateTime.MinValue;
        }

        private void TryAutoHide()
        {
            CheckAutoHide();
        }

        // ───────────────────────── 悬停 / 名称 ─────────────────────────

        private void UpdateHover(DockIconControl ctl)
        {
            var s = _store.Current;
            _hovered = ctl;
            int hi = ctl == null ? -1 : _icons.IndexOf(ctl);

            for (int i = 0; i < _icons.Count; i++)
            {
                double k = 0;
                if (hi >= 0)
                {
                    int d = Math.Abs(i - hi);
                    if (d == 0) k = 1.0;
                    else if (d == 1) k = 0.52;
                    else if (d == 2) k = 0.18;
                }
                double amt = 1.0 + (s.HoverScale - 1.0) * (k == 1.0 || s.NeighborMagnify ? k : 1.0);
                if (hi < 0) amt = 1.0;
                _icons[i].SetHoverAmount(amt);
                _icons[i].SetBubble(k == 1.0);
                _icons[i].SetLift(k == 1.0 ? -Math.Max(2, s.IconSize * 0.07) : 0);
            }

            if (ctl != null && s.ShowLabels) ShowLabel(ctl); else HideLabel();
            if (ctl == null && !IsMouseOver) ScheduleAutoHide();
        }

        private void ShowLabel(DockIconControl ctl)
        {
            string name = ctl.IsSpecial ? "设置" : (ctl.Item?.Name ?? "");
            if (string.IsNullOrWhiteSpace(name)) { HideLabel(); return; }
            // 图标可能已经被重建（例如设置里刚改过内容），此时它已脱离可视树
            if (!ctl.IsDescendantOf(SlideGroup)) { HideLabel(); return; }

            LabelText.Text = name;
            LabelBox.Visibility = Visibility.Visible;
            LabelBox.Measure(new Size(1000, 1000));
            double w = LabelBox.DesiredSize.Width;
            double h = LabelBox.DesiredSize.Height;
            if (w <= 0) w = 80;
            if (h <= 0) h = 26;

            var s = _store.Current;
            var pos = ctl.TransformToAncestor(SlideGroup).Transform(new Point(ctl.ActualWidth / 2, 0));

            double lx, ly;
            if (_horizontal)
            {
                lx = pos.X - w / 2;
                ly = s.Edge == DockEdge.Bottom ? _pillY - h - 8 : _pillY + _pillH + 8;
            }
            else
            {
                lx = s.Edge == DockEdge.Left ? _pillX + _pillW + 8 : _pillX - w - 8;
                ly = pos.Y + (ctl.ActualHeight - h) / 2;
            }
            lx = Math.Max(2, Math.Min(_winW - w - 2, lx));
            ly = Math.Max(2, Math.Min(_winH - h - 2, ly));

            Canvas.SetLeft(LabelBox, lx);
            Canvas.SetTop(LabelBox, ly);
            LabelBox.Opacity = 0;
            LabelBox.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        }

        private void HideLabel()
        {
            if (LabelBox.Visibility != Visibility.Visible) return;
            var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
            anim.Completed += (s, e) =>
            {
                if (LabelBox.Opacity < 0.1) LabelBox.Visibility = Visibility.Collapsed;
            };
            LabelBox.BeginAnimation(OpacityProperty, anim);
        }

        // ───────────────────────── 滚轮横向滚动 ─────────────────────────

        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            if (TryScroll(-e.Delta * 0.85)) { e.Handled = true; return; }
            base.OnPreviewMouseWheel(e);
        }

        private bool TryScroll(double delta)
        {
            if (_scrollMax <= 0.5) return false;
            double target = Math.Max(0, Math.Min(_scrollMax, _scrollOffset + delta));
            if (Math.Abs(target - _scrollOffset) < 0.1) return true;
            _scrollTicker.Start(_scrollOffset, target, 170, v =>
            {
                _scrollOffset = v;
                if (_horizontal) Scroller.ScrollToHorizontalOffset(v);
                else Scroller.ScrollToVerticalOffset(v);
                UpdateScrollMask();
            });
            return true;
        }

        private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            _scrollMax = _horizontal ? Scroller.ScrollableWidth : Scroller.ScrollableHeight;
        }

        private void UpdateScrollMask()
        {
            _scrollMax = _horizontal ? Scroller.ScrollableWidth : Scroller.ScrollableHeight;
            if (_scrollMax <= 0.5)
            {
                Scroller.OpacityMask = null;
                return;
            }
            double len = _horizontal ? Scroller.ActualWidth : Scroller.ActualHeight;
            if (len <= 1) return;
            double startFade = Math.Min(0.28, 26.0 / len);
            double endFade = 1.0 - startFade;

            var stops = new GradientStopCollection();
            bool leftMore = _scrollOffset > 1;
            bool rightMore = _scrollOffset < _scrollMax - 1;

            if (_horizontal)
            {
                stops.Add(new GradientStop(leftMore ? Colors.Transparent : Colors.White, 0));
                stops.Add(new GradientStop(Colors.White, startFade));
                stops.Add(new GradientStop(Colors.White, endFade));
                stops.Add(new GradientStop(rightMore ? Colors.Transparent : Colors.White, 1));
                Scroller.OpacityMask = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0));
            }
            else
            {
                stops.Add(new GradientStop(leftMore ? Colors.Transparent : Colors.White, 0));
                stops.Add(new GradientStop(Colors.White, startFade));
                stops.Add(new GradientStop(Colors.White, endFade));
                stops.Add(new GradientStop(rightMore ? Colors.Transparent : Colors.White, 1));
                Scroller.OpacityMask = new LinearGradientBrush(stops, new Point(0, 0), new Point(0, 1));
            }
        }

        // ───────────────────────── 右键菜单 ─────────────────────────

        private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
        {            var s = _store.Current;
            var menu = new ContextMenu
            {
                PlacementTarget = this,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                HasDropShadow = true
            };

            var miSettings = new MenuItem { Header = "设置…" };
            miSettings.Click += (a, b) => SettingsRequested?.Invoke();
            menu.Items.Add(miSettings);

            var miAdd = new MenuItem { Header = "添加程序…" };
            miAdd.Click += (a, b) => SettingsRequested?.Invoke();
            menu.Items.Add(miAdd);

            menu.Items.Add(new Separator());

            var miHide = new MenuItem { Header = _shown ? "隐藏 Dock" : "显示 Dock" };
            miHide.Click += (a, b) => ToggleDock();
            menu.Items.Add(miHide);

            var miReload = new MenuItem { Header = "重新载入图标" };
            miReload.Click += (a, b) => { IconHelper.ClearCache(); ShortcutResolver.ClearCache(); ReloadAll(); };
            menu.Items.Add(miReload);

            var miFolder = new MenuItem { Header = "打开设置文件夹" };
            miFolder.Click += (a, b) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", SettingsStore.SettingsFolder); }
                catch (Exception ex) { Log.Warn(ex.Message); }
            };
            menu.Items.Add(miFolder);

            menu.Items.Add(new Separator());

            var miExit = new MenuItem { Header = "退 ?OrbDock" };
            miExit.Click += (a, b) => Application.Current.Shutdown();
            menu.Items.Add(miExit);

            menu.IsOpen = true;
            _menuOpen = true;
            menu.Closed += (a, b) => { _menuOpen = false; _lastInside = DateTime.Now; };
            e.Handled = true;
        }

        // ───────────────────────── 拖动排序 ─────────────────────────

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                if (_dragging) EndDrag();
                return;
            }
            var cap = Mouse.Captured as DockIconControl;
            if (cap == null || !cap.IsDragging) return;
            if (!_dragging)
            {
                _dragging = true;
                _lastInside = DateTime.Now;
            }
            var pos = e.GetPosition(ItemsPanel);
            ReorderTo(_horizontal ? pos.X : pos.Y);
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            SyncOrderToModel();
            ScheduleAutoHide();
        }

        private void ReorderTo(double along)
        {
            if (_cellWidths.Count < 2) return;
            int target = 0;
            double acc = 0;
            for (int i = 0; i < _cellWidths.Count; i++)
            {
                if (along < acc + _cellWidths[i] / 2.0) { target = i; break; }
                acc += _cellWidths[i] + _store.Current.IconGap;
                target = i;
            }
            target = Math.Max(0, Math.Min(_cellWidths.Count - 1, target));

            // 找到当前被拖动图标在面板中的位置（简单做法：用鼠标捕获对象）
            var captured = Mouse.Captured as DockIconControl;
            if (captured == null) return;
            int cur = ItemsPanel.Children.IndexOf(captured);
            if (cur < 0 || cur == target) return;

            ItemsPanel.Children.RemoveAt(cur);
            ItemsPanel.Children.Insert(target, captured);

            var w = _cellWidths[cur];
            _cellWidths.RemoveAt(cur);
            _cellWidths.Insert(target, w);

            var it = _iconItems[cur];
            _iconItems.RemoveAt(cur);
            _iconItems.Insert(target, it);
        }

        private void SyncOrderToModel()
        {
            var s = _store.Current;
            var ordered = new List<DockItem>();
            foreach (var it in _iconItems) if (it != null) ordered.Add(it);
            foreach (var it in s.Items) if (!ordered.Contains(it)) ordered.Add(it);
            s.Items = ordered;
            _store.Save(s);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var ctl = e.OriginalSource as DependencyObject;
            while (ctl != null && !(ctl is DockIconControl)) ctl = VisualTreeHelper.GetParent(ctl);
            if (ctl is DockIconControl icon && !icon.IsSpecial)
            {
                icon.CaptureMouse();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_dragging) EndDrag();
            if (Mouse.Captured is DockIconControl c) c.ReleaseMouseCapture();
        }
    }
}
