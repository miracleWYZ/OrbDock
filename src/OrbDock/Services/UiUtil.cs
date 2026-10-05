using System;
using System.Windows;
using System.Windows.Interop;
using OrbDock.Interop;

namespace OrbDock.Services
{
    public static class UiUtil
    {
        /// <summary>
        /// 把窗口强行抬到最前（普通窗口，不常驻置顶）。
        /// 用 TOPMOST → NOTOPMOST 的经典手法绕过前台窗口锁定，保证设置窗口/对话框不会被别的窗口压住。
        /// </summary>
        public static void BringToFront(Window w)
        {
            if (w == null) return;
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
                NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
                NativeMethods.SetWindowPos(h, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                NativeMethods.SetForegroundWindow(h);
            }
            catch { }
        }
    }
}
