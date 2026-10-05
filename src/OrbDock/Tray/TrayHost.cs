using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OrbDock.Interop;
using OrbDock.Services;

namespace OrbDock.Tray
{
    /// <summary>系统托盘图标与菜单。</summary>
    public sealed class TrayHost : IDisposable
    {
        private NotifyIcon _icon;
        private Icon _handle;

        public event Action SettingsRequested;
        public event Action ToggleRequested;
        public event Action ReloadRequested;
        public event Action ExitRequested;

        public void Show()
        {
            if (_icon != null) return;
            try
            {
                _handle = BuildIcon();
                var menu = new ContextMenuStrip();
                menu.Items.Add("设置…", null, (s, e) => SettingsRequested?.Invoke());
                menu.Items.Add("显示 / 隐藏 Dock", null, (s, e) => ToggleRequested?.Invoke());
                menu.Items.Add("重新载入图标", null, (s, e) => ReloadRequested?.Invoke());
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出 OrbDock", null, (s, e) => ExitRequested?.Invoke());

                _icon = new NotifyIcon
                {
                    Icon = _handle,
                    Text = "OrbDock 液态玻璃桌面导航栏",
                    Visible = true,
                    ContextMenuStrip = menu
                };
                _icon.DoubleClick += (s, e) => SettingsRequested?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warn("创建托盘图标失败: " + ex.Message);
            }
        }

        public void Hide()
        {
            if (_icon != null) _icon.Visible = false;
        }

        private static Icon BuildIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new LinearGradientBrush(new Point(2, 2), new Point(30, 30),
                        Color.FromArgb(255, 130, 190, 255), Color.FromArgb(255, 30, 70, 190)))
                    {
                        g.FillEllipse(b, 1, 1, 30, 30);
                    }
                    using (var hl = new LinearGradientBrush(new Point(6, 4), new Point(24, 20),
                        Color.FromArgb(190, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
                    {
                        g.FillEllipse(hl, 5, 4, 20, 14);
                    }
                    using (var p = new Pen(Color.FromArgb(150, 255, 255, 255), 1.6f))
                    {
                        g.DrawEllipse(p, 1.6f, 1.6f, 28.8f, 28.8f);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    NativeMethods.DestroyIcon(h);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                    _icon = null;
                }
                _handle?.Dispose();
                _handle = null;
            }
            catch { }
        }
    }
}
