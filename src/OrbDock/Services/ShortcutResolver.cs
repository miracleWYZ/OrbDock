using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OrbDock.Services
{
    /// <summary>解析 .lnk 快捷方式（IShellLink）。</summary>
    public static class ShortcutResolver
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkCoClass { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        public class ShortcutInfo
        {
            public string TargetPath = "";
            public string Arguments = "";
            public string WorkingDirectory = "";
            public string IconLocation = "";
            public string Description = "";
            public string Url = "";
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ShortcutInfo> Cache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, ShortcutInfo>(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache() => Cache.Clear();

        public static ShortcutInfo Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return new ShortcutInfo();
            if (Cache.TryGetValue(path, out var cached)) return cached;

            var info = new ShortcutInfo();
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".url")
                {
                    info.Url = ReadUrlFile(path);
                    info.TargetPath = info.Url;
                }
                else if (ext == ".lnk")
                {
                    object linkObj = new ShellLinkCoClass();
                    try
                    {
                        var link = (IShellLinkW)linkObj;
                        ((IPersistFile)link).Load(path, 0);

                        var sb = new StringBuilder(1024);
                        link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                        info.TargetPath = sb.ToString();

                        sb.Clear();
                        link.GetArguments(sb, sb.Capacity);
                        info.Arguments = sb.ToString();

                        sb.Clear();
                        link.GetWorkingDirectory(sb, sb.Capacity);
                        info.WorkingDirectory = sb.ToString();

                        sb.Clear();
                        int iconIdx;
                        link.GetIconLocation(sb, sb.Capacity, out iconIdx);
                        info.IconLocation = sb.ToString();

                        sb.Clear();
                        link.GetDescription(sb, sb.Capacity);
                        info.Description = sb.ToString();
                    }
                    finally
                    {
                        if (linkObj != null && Marshal.IsComObject(linkObj)) Marshal.ReleaseComObject(linkObj);
                    }
                }
                else
                {
                    info.TargetPath = path;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("解析快捷方式失败(" + path + "): " + ex.Message);
                info.TargetPath = path;
            }

            Cache[path] = info;
            return info;
        }

        private static string ReadUrlFile(string path)
        {
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        return line.Substring(4).Trim();
                }
            }
            catch { }
            return "";
        }
    }
}
