using System;
using System.Collections.Generic;
using Microsoft.Win32;
using OrbDock.Interop;

namespace OrbDock.Services
{
    /// <summary>显示 / 隐藏桌面图标（不删除任何文件，随时可恢复）。</summary>
    public static class DesktopIcons
    {
        private const string AdvancedKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const uint WM_COMMAND = 0x0111;
        private const int CMD_TOGGLE_DESKTOP_ICONS = 0x7402;

        public static bool IsHidden()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(AdvancedKey))
                {
                    var v = k?.GetValue("HideIcons");
                    if (v is int i) return i != 0;
                }
            }
            catch (Exception ex) { Log.Warn("读取桌面图标状态失败: " + ex.Message); }
            return false;
        }

        /// <summary>把桌面图标设成指定状态（已一致则不做任何事）。</summary>
        public static void SetHidden(bool hidden)
        {
            try
            {
                // 资源管理器切换后会把 HideIcons 写进注册表，用它来核对结果
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (IsHidden() == hidden) break;
                    if (!ToggleViaShell())
                    {
                        Log.Warn("未能向资源管理器发送切换桌面图标的命令");
                        break;
                    }
                    System.Threading.Thread.Sleep(180);
                }

                // 仍然不一致时，写注册表兜底（下次登录/刷新后生效）
                if (IsHidden() != hidden)
                {
                    try
                    {
                        using (var k = Registry.CurrentUser.OpenSubKey(AdvancedKey, true))
                        {
                            if (k != null) k.SetValue("HideIcons", hidden ? 1 : 0, RegistryValueKind.DWord);
                        }
                    }
                    catch (Exception ex) { Log.Warn("写入桌面图标状态失败: " + ex.Message); }
                }

                Log.Info("桌面图标：目标=" + (hidden ? "隐藏" : "显示") + "，实际=" + (IsHidden() ? "隐藏" : "显示"));
            }
            catch (Exception ex) { Log.Warn("切换桌面图标失败: " + ex.Message); }
        }

        private static bool ToggleViaShell()
        {
            var defView = FindDesktopListView();
            if (defView == IntPtr.Zero) return false;
            NativeMethods.SendMessage(defView, WM_COMMAND,
                new IntPtr(CMD_TOGGLE_DESKTOP_ICONS), IntPtr.Zero);
            return true;
        }

        /// <summary>找到桌面的 SHELLDLL_DefView 窗口（可能在 Progman 下，也可能在某个 WorkerW 下）。</summary>
        private static IntPtr FindDesktopListView()
        {
            var progman = NativeMethods.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                var v = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (v != IntPtr.Zero) return v;
            }

            // 桌面被"显示桌面"或壁纸程序接管时，DefView 会挂到某个 WorkerW 下
            var candidates = new List<IntPtr>();
            try
            {
                NativeMethods.EnumWindows((h, l) =>
                {
                    var v = NativeMethods.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (v != IntPtr.Zero) candidates.Add(v);
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { Log.Warn("枚举桌面窗口失败: " + ex.Message); }

            return candidates.Count > 0 ? candidates[0] : IntPtr.Zero;
        }
    }
}
