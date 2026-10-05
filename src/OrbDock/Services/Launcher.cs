using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using OrbDock.Interop;
using OrbDock.Models;

namespace OrbDock.Services
{
    public static class Launcher
    {
        public static void Launch(DockItem item, Window owner, bool activateRunning)
        {
            if (item == null) return;
            try
            {
                switch (item.Kind)
                {
                    case ItemKind.Url:
                        ShellExec(item.Target);
                        break;
                    case ItemKind.Folder:
                        ShellExec("explorer.exe", "\"" + item.Target + "\"");
                        break;
                    case ItemKind.Command:
                        ShellExec("cmd.exe", "/c " + item.Target + " " + item.Arguments);
                        break;
                    case ItemKind.Separator:
                        break;
                    default:
                        // 单实例程序（微信/哔哩哔哩/QQ 这类）再次启动只会立刻退出，
                        // 看上去就是"点了没反应"，所以先尝试把它已有的窗口恢复并切到前台
                        if (activateRunning && !item.RunAsAdmin && TryActivate(item)) return;

                        var psi = new ProcessStartInfo
                        {
                            FileName = item.Target,
                            UseShellExecute = true
                        };
                        if (!string.IsNullOrWhiteSpace(item.Arguments)) psi.Arguments = item.Arguments;
                        if (!string.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                            psi.WorkingDirectory = item.WorkingDirectory;
                        if (item.RunAsAdmin) psi.Verb = "runas";
                        Process.Start(psi);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("启动失败(" + item.Target + "): " + ex.Message);
                MessageBox.Show("无法启动：" + item.Name + "\n" + ex.Message,
                    "OrbDock", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 该程序已在运行且有自己的窗口时，恢复（若最小化）并切到前台。
        /// 成功返回 true；没在运行 / 只有托盘无窗口 / 激活失败都返回 false（调用方会照常启动新的）。
        /// </summary>
        public static bool TryActivate(DockItem item)
        {
            if (item == null) return false;
            try
            {
                string exe = ProcessWatcher.ResolveExeName(item);
                if (string.IsNullOrEmpty(exe)) return false;

                IntPtr hwnd = IntPtr.Zero;
                foreach (var p in Process.GetProcessesByName(exe))
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero) { hwnd = p.MainWindowHandle; break; }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                if (hwnd == IntPtr.Zero) return false;

                if (NativeMethods.IsIconic(hwnd))
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                else
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);

                if (NativeMethods.SetForegroundWindow(hwnd)) return true;

                // Windows 的前台锁有时会拒绝 SetForegroundWindow，用这个兜底
                NativeMethods.SwitchToThisWindow(hwnd, true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("激活已有窗口失败(" + item.Name + "): " + ex.Message);
                return false;
            }
        }

        private static void ShellExec(string file)
        {
            Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true });
        }

        private static void ShellExec(string file, string args)
        {
            Process.Start(new ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = true });
        }

        /// <summary>把运行中的进程名做成集合，用于图标下方的小圆点。</summary>
        public sealed class ProcessWatcher : IDisposable
        {
            private readonly Timer _timer;
            private HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly object _gate = new object();

            public event EventHandler Refreshed;

            public ProcessWatcher()
            {
                Refresh();
                _timer = new Timer(_ => { Refresh(); Refreshed?.Invoke(this, EventArgs.Empty); },
                    null, 2500, 2500);
            }

            public bool IsRunning(DockItem item)
            {
                if (item == null) return false;
                string exe = ResolveExeName(item);
                if (string.IsNullOrEmpty(exe)) return false;
                lock (_gate) { return _names.Contains(exe); }
            }

            public static string ResolveExeName(DockItem item)
            {
                string target = item.Target;
                try
                {
                    if (!string.IsNullOrEmpty(target) &&
                        Path.GetExtension(target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        var info = ShortcutResolver.Resolve(target);
                        if (!string.IsNullOrWhiteSpace(info.TargetPath)) target = info.TargetPath;
                    }
                    if (string.IsNullOrWhiteSpace(target)) return null;
                    var name = Path.GetFileNameWithoutExtension(target);
                    return string.IsNullOrWhiteSpace(name) ? null : name;
                }
                catch { return null; }
            }

            private void Refresh()
            {
                try
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in Process.GetProcesses())
                    {
                        try { set.Add(p.ProcessName); } catch { }
                        finally { p.Dispose(); }
                    }
                    lock (_gate) { _names = set; }
                }
                catch (Exception ex) { Log.Warn("枚举进程失败: " + ex.Message); }
            }

            public void Dispose() => _timer?.Dispose();
        }
    }
}
