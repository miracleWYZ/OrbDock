using System;
using System.Windows;
using System.Windows.Forms;
using OrbDock.Interop;

namespace OrbDock.Services
{
    public sealed class WorkAreaInfo
    {
        public double Left, Top, Width, Height;  // DIP
        public double Scale = 1.0;               // 该显示器的 DPI 缩放
        public bool Primary;
        public string DeviceName = "";
    }

    /// <summary>取得指定显示器的工作区（已排除任务栏），单位 DIP。</summary>
    public static class ScreenHelper
    {
        public static int ScreenCount => Screen.AllScreens.Length;

        public static string Describe(int index)
        {
            var screens = Screen.AllScreens;
            if (index < 0 || index >= screens.Length) index = 0;
            var s = screens[index];
            return string.Format("{0}{1}  {2}×{3}", s.Primary ? "主显示器 " : "显示器 ",
                index + 1, s.Bounds.Width, s.Bounds.Height);
        }

        public static WorkAreaInfo Get(int index)
        {
            var screens = Screen.AllScreens;
            if (index < 0 || index >= screens.Length) index = 0;
            var scr = screens[index];
            double scale = GetScale(scr);

            var info = new WorkAreaInfo
            {
                Scale = scale,
                Primary = scr.Primary,
                DeviceName = scr.DeviceName
            };

            // 设备像素下计算可用区域（工作区再扣掉任务栏，兼容任务栏自动隐藏时工作区=整屏的情况）
            var work = scr.WorkingArea;
            var bounds = scr.Bounds;
            var tb = TaskbarRect();
            if (tb.HasValue && tb.Value.IntersectsWith(bounds))
            {
                var t = tb.Value;
                bool horizontal = t.Width >= t.Height;
                if (horizontal)
                {
                    if (t.Bottom >= bounds.Bottom - 1)
                        work = System.Drawing.Rectangle.FromLTRB(work.Left, work.Top,
                            work.Right, Math.Min(work.Bottom, bounds.Bottom - t.Height));
                    else if (t.Top <= bounds.Top + 1)
                        work = System.Drawing.Rectangle.FromLTRB(work.Left,
                            Math.Max(work.Top, bounds.Top + t.Height), work.Right, work.Bottom);
                }
                else
                {
                    if (t.Right >= bounds.Right - 1)
                        work = System.Drawing.Rectangle.FromLTRB(work.Left, work.Top,
                            Math.Min(work.Right, bounds.Right - t.Width), work.Bottom);
                    else if (t.Left <= bounds.Left + 1)
                        work = System.Drawing.Rectangle.FromLTRB(
                            Math.Max(work.Left, bounds.Left + t.Width), work.Top, work.Right, work.Bottom);
                }
            }

            if (scr.Primary)
            {
                // 主显示器：最小坐标即原点
                info.Left = work.Left / scale;
                info.Top = work.Top / scale;
                info.Width = work.Width / scale;
                info.Height = work.Height / scale;
            }
            else
            {
                var p = Screen.PrimaryScreen;
                var pwa = p.WorkingArea;
                double pScale = GetScale(p);
                info.Left = (pwa.Left + (work.Left - pwa.Left)) / pScale;
                info.Top = (pwa.Top + (work.Top - pwa.Top)) / pScale;
                info.Width = work.Width / scale;
                info.Height = work.Height / scale;
            }
            return info;
        }

        /// <summary>任务栏矩形（设备像素）。任务栏自动隐藏时也能拿到尺寸。</summary>
        public static System.Drawing.Rectangle? TaskbarRect()
        {
            // 首选：Shell_TrayWnd 窗口矩形（自动隐藏时会部分位于屏幕外，但尺寸依然正确）
            try
            {
                IntPtr h = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (h != IntPtr.Zero)
                {
                    NativeMethods.RECT r;
                    if (NativeMethods.GetWindowRect(h, out r))
                    {
                        var rect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                        if (rect.Width > 0 && rect.Height > 0) return rect;
                    }
                }
            }
            catch { }

            // 备选：SHAppBarMessage
            try
            {
                var data = new NativeMethods.APPBARDATA();
                data.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(data);
                if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref data) == 0)
                    return null;
                var r = data.rc;
                if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return null;
                return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
            catch { return null; }
        }

        private static double GetScale(Screen scr)
        {
            try
            {
                var pt = new NativeMethods.POINT
                {
                    X = scr.Bounds.Left + scr.Bounds.Width / 2,
                    Y = scr.Bounds.Top + scr.Bounds.Height / 2
                };
                IntPtr mon = NativeMethods.MonitorFromPoint(pt, 2 /*MONITOR_DEFAULTTONEAREST*/);
                if (mon != IntPtr.Zero)
                {
                    uint dx, dy;
                    if (NativeMethods.GetDpiForMonitor(mon, NativeMethods.MDT_EFFECTIVE_DPI, out dx, out dy) == 0 && dx > 0)
                        return dx / 96.0;
                }
            }
            catch { }
            try
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    return g.DpiX / 96.0;
                }
            }
            catch { }
            return 1.0;
        }
    }
}
