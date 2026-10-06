using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OrbDock.Services
{
    public class CatalogEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Folder { get; set; }
        /// <summary>附加提示（例如"已在 Dock 中"）。</summary>
        public string Note { get; set; } = "";
        public override string ToString() => Name;
    }

    /// <summary>扫描开始菜单 / 桌面上的快捷方式，供“添加程序”使用。</summary>
    public static class AppCatalog
    {
        private static List<CatalogEntry> _cache;

        public static void Invalidate() => _cache = null;

        public static List<CatalogEntry> All()
        {
            if (_cache != null) return _cache;
            var list = new List<CatalogEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in Roots())
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        if (!seen.Add(f)) continue;
                        var name = System.IO.Path.GetFileNameWithoutExtension(f);
                        list.Add(new CatalogEntry
                        {
                            Name = name,
                            Path = f,
                            Folder = ShortenFolder(root, f)
                        });
                    }
                }
                catch (Exception ex) { Log.Warn("扫描开始菜单失败: " + ex.Message); }
            }

            _cache = list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return _cache;
        }

        public static List<CatalogEntry> Search(string keyword)
        {
            var all = All();
            if (string.IsNullOrWhiteSpace(keyword)) return all;
            var k = keyword.Trim();
            return all.Where(e =>
                    e.Name.IndexOf(k, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .ToList();
        }

        private static IEnumerable<string> Roots()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        }

        private static string ShortenFolder(string root, string file)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(file) ?? "";
                if (dir.Length > root.Length) dir = dir.Substring(root.Length).TrimStart('\\');
                return dir;
            }
            catch { return ""; }
        }
    }

    public static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "OrbDock";

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return k?.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 当前进程真正的 exe 路径。
        /// 注意：framework-dependent 发布时 Assembly.Location 给的是 .dll（.exe 只是 apphost 外壳），
        /// 拿它写进 Run 键会导致开机启动一个 dll —— 什么都不会发生。
        /// </summary>
        public static string CurrentExePath()
        {
            try
            {
                var p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return p;

                var loc = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(loc))
                    return Path.ChangeExtension(loc, ".exe");
            }
            catch { }
            return null;
        }

        /// <summary>开机自启用的命令行：--tray 让 Dock 启动后直接收起。</summary>
        private static string CommandLine(string exe) => "\"" + exe + "\" --tray";

        /// <summary>让注册表里的自启项与设置保持一致（每次启动时调用，可自动修好路径变了/写错的情况）。</summary>
        public static void Apply(bool enabled)
        {
            if (!enabled) { Set(false); return; }

            string exe = CurrentExePath();
            if (string.IsNullOrEmpty(exe)) return;

            string want = CommandLine(exe);
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k != null && string.Equals(k.GetValue(ValueName) as string, want, StringComparison.OrdinalIgnoreCase))
                        return;   // 已经是正确的，不用重写
                }
            }
            catch { }
            Set(true);
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enabled)
                    {
                        var exe = CurrentExePath();
                        if (string.IsNullOrEmpty(exe)) return;
                        k.SetValue(ValueName, CommandLine(exe));
                        Log.Info("已设置开机自启: " + exe);
                    }
                    else
                    {
                        if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("设置开机自启失败: " + ex.Message); }
        }
    }
}
