using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OrbDock.Services;
using OrbDock.Tray;
using OrbDock.Views;

namespace OrbDock
{
    public partial class App : Application
    {
        private SettingsStore _store;

        /// <summary>当前是否工作在软件渲染模式（供动画降级判断）。</summary>
        internal static bool SoftwareRendering;
        private ThemeService _theme;
        private Launcher.ProcessWatcher _watcher;
        private DockWindow _dock;
        private SettingsWindow _settings;
        private HotkeyService _hotkey;
        private TrayHost _tray;
        private DispatcherTimer _themeTimer;
        private Mutex _mutex;
        private EventWaitHandle _activateEvent;
        private bool _exiting;

        public App()
        {
            Debug.Marker("App ctor");
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Debug.Marker("OnStartup enter");
            try
            {
                Boot(e.Args);
            }
            catch (Exception ex)
            {
                Debug.Marker("BOOT FAIL: " + ex);
                Log.Error("启动失败: " + ex);
                MessageBox.Show("OrbDock 启动失败：\n" + ex.Message, "OrbDock",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void Boot(string[] args)
        {
            bool created;
            _mutex = new Mutex(true, @"Global\OrbDock.SingleInstance", out created);
            Debug.Marker("mutex created=" + created);
            if (!created)
            {
                // 已经有一个实例：唤醒它打开设置
                try
                {
                    EventWaitHandle.OpenExisting(@"Global\OrbDock.Activate").Set();
                }
                catch { }
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, ex) =>
            {
                Log.Error("未处理异常: " + ex.Exception);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                Log.Error("域异常: " + ex.ExceptionObject);

            _store = new SettingsStore();
            _store.Load();
            _store.Save(_store.Current);   // 首次运行即落盘，方便用户查看/编辑配置文件

            // 渲染模式：显卡驱动不稳定（GPU 超时/被重置）时会直接把 WPF 的 D3D 设备打死，
            // 这种崩溃发生在渲染线程，托管异常兜底抓不到，也没有转储。改成软件渲染可彻底避开。
            bool softRender = _store.Current.SoftwareRender
                || string.Equals(Environment.GetEnvironmentVariable("ORBDOCK_SOFTWARE_RENDER"), "1", StringComparison.Ordinal)
                || (args != null && args.Any(a => string.Equals(a, "--software-render", StringComparison.OrdinalIgnoreCase)));
            SoftwareRendering = softRender;
            if (softRender)
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            Log.Info("渲染模式: " + (softRender ? "软件渲染（已禁用硬件加速）" : "硬件加速"));
            Debug.Marker("settings loaded");
            Log.Info("OrbDock 启动，设置文件: " + _store.FilePath);

            // 命令行：--import-desktop 把桌面图标导入 Dock 并隐藏桌面图标
            if (args != null && args.Any(a => string.Equals(a, "--import-desktop", StringComparison.OrdinalIgnoreCase)))
            {
                int skipped;
                int added = DesktopImporter.ImportInto(_store.Current, out skipped);
                _store.Current.HideDesktopIcons = true;
                _store.Save(_store.Current);
                Log.Info(string.Format("导入桌面图标：新增 {0} 项，跳过 {1} 项", added, skipped));
            }

            // 命令行：--tray 启动后直接收起（供开机自启用，避免每次登录 Dock 先弹出来一下）
            bool startInTray = args != null &&
                args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

            // 命令行：--desktop-icons=hide|show 直接切换桌面图标显示状态
            if (args != null)
            {
                if (args.Any(a => string.Equals(a, "--desktop-icons=hide", StringComparison.OrdinalIgnoreCase)))
                {
                    _store.Current.HideDesktopIcons = true;
                    _store.Save(_store.Current);
                }
                else if (args.Any(a => string.Equals(a, "--desktop-icons=show", StringComparison.OrdinalIgnoreCase)))
                {
                    _store.Current.HideDesktopIcons = false;
                    _store.Save(_store.Current);
                }
            }

            _theme = new ThemeService();
            _theme.Refresh();

            _watcher = new Launcher.ProcessWatcher();

            _dock = new DockWindow(_store, _theme, _watcher);
            _dock.SettingsRequested += OpenSettings;
            _dock.EmergencyHotkeyPressed += EmergencyExit;
            _dock.HotZoneWatchChanged += on =>
            {
                if (_hotkey != null) _hotkey.WatchMouse = on;
            };
            _dock.Show();
            Debug.Marker("dock shown");
            _dock.ReloadAll();
            _dock.ShowDock(false);
            if (startInTray)
            {
                // 同步收起：同一帧内完成，不会闪一下
                _dock.HideDock(false);
                Debug.Marker("start in tray");
            }
            Debug.Marker("dock ready");

            _hotkey = new HotkeyService();
            _hotkey.Triggered += EmergencyExit;
            _hotkey.MouseMoved += (x, y) => _dock.OnGlobalMouseMove(x, y);
            _hotkey.Start(_store.Current.EmergencyHotkey, _dock.Handle);
            Debug.Marker("hotkey started");

            if (_store.Current.ShowTrayIcon) StartTray();

            // 桌面图标隐藏状态跟随设置（两个方向都执行，保证与设置一致）
            DesktopIcons.SetHidden(_store.Current.HideDesktopIcons);

            // 开机自启项与设置保持一致（程序挪过位置、或以前写错了路径，都会在这里自动纠正）
            AutoStart.Apply(_store.Current.AutoStart);

            _themeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _themeTimer.Tick += (s, ev) => _theme.Refresh();
            _themeTimer.Start();

            ListenForSecondInstance();
        }

        private void StartTray()
        {
            if (_tray == null)
            {
                _tray = new TrayHost();
                _tray.SettingsRequested += OpenSettings;
                _tray.ToggleRequested += () => _dock.ToggleDock();
                _tray.ReloadRequested += () =>
                {
                    IconHelper.ClearCache();
                    ShortcutResolver.ClearCache();
                    AppCatalog.Invalidate();
                    _dock.ReloadAll();
                };
                _tray.ExitRequested += () => Shutdown();
            }
            _tray.Show();
        }

        private void ListenForSecondInstance()
        {
            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                    @"Global\OrbDock.Activate");
                ThreadPool.RegisterWaitForSingleObject(_activateEvent, (s, timeout) =>
                {
                    if (timeout) return;
                    Dispatcher.BeginInvoke(new Action(OpenSettings));
                }, null, -1, false);
            }
            catch (Exception ex) { Log.Warn("监听第二实例失败: " + ex.Message); }
        }

        public void OpenSettings()
        {
            try
            {
                if (_settings == null)
                {
                    _settings = new SettingsWindow(_store, _theme);
                    _settings.HotkeyChanged += ApplyHotkey;
                    _settings.Closed += (s, e) => _settings = null;
                    _settings.Show();
                }
                else
                {
                    if (_settings.WindowState == WindowState.Minimized)
                        _settings.WindowState = WindowState.Normal;
                    _settings.Activate();
                    Services.UiUtil.BringToFront(_settings);
                }
                _settings.Reload();

                // 托盘开关可能在设置里被改变
                if (_store.Current.ShowTrayIcon) StartTray(); else _tray?.Hide();
            }
            catch (Exception ex)
            {
                Log.Error("打开设置窗口失败: " + ex.Message);
            }
        }

        private void ApplyHotkey()
        {
            try
            {
                _hotkey.Stop();
                _hotkey.Start(_store.Current.EmergencyHotkey, _dock.Handle);
            }
            catch (Exception ex) { Log.Warn("重新注册热键失败: " + ex.Message); }
        }

        /// <summary>应急关闭：先落盘设置，然后立即结束进程。</summary>
        public void EmergencyExit()
        {
            if (_exiting) return;
            _exiting = true;
            try { Log.Info("应急关闭快捷键触发，立即退出"); } catch { }
            try { _store?.Save(_store.Current); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _hotkey?.Dispose(); } catch { }
            try { Process.GetCurrentProcess().Kill(); } catch { }
            Environment.Exit(0);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _themeTimer?.Stop(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _hotkey?.Dispose(); } catch { }
            try { _watcher?.Dispose(); } catch { }
            try { _store?.Save(_store.Current); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            Log.Info("OrbDock 退出");
            base.OnExit(e);
        }
    }
}
