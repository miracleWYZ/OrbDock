using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OrbDock.Models;

namespace OrbDock.Services
{
    /// <summary>把桌面上的快捷方式 / 文件 / 文件夹收集成 Dock 项。</summary>
    public static class DesktopImporter
    {
        public static IEnumerable<string> DesktopFolders()
        {
            var list = new List<string>();
            try { list.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
            try { list.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
            return list.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public static List<DockItem> Collect()
        {
            var files = new List<DockItem>();
            var folders = new List<DockItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in DesktopFolders())
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                }
                catch { continue; }

                string[] entries;
                try { entries = Directory.GetFileSystemEntries(dir); }
                catch (Exception ex)
                {
                    Log.Warn("枚举桌面失败(" + dir + "): " + ex.Message);
                    continue;
                }

                foreach (var path in entries)
                {
                    try
                    {
                        var fileName = Path.GetFileName(path);
                        if (string.IsNullOrWhiteSpace(fileName)) continue;
                        if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!seen.Add(path)) continue;

                        bool isDir;
                        try
                        {
                            var attrs = File.GetAttributes(path);
                            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                            isDir = (attrs & FileAttributes.Directory) != 0;
                        }
                        catch { continue; }

                        var item = new DockItem
                        {
                            Target = path,
                            Name = isDir ? fileName : Path.GetFileNameWithoutExtension(path),
                            Kind = isDir ? ItemKind.Folder : ItemKind.Launch
                        };
                        if (string.IsNullOrWhiteSpace(item.Name)) item.Name = fileName;

                        if (isDir) folders.Add(item); else files.Add(item);
                    }
                    catch (Exception ex) { Log.Warn("读取桌面项失败: " + ex.Message); }
                }
            }

            // 桌面上默认是"文件夹在前，其余按名称排序"
            var cmp = StringComparer.CurrentCultureIgnoreCase;
            folders.Sort((a, b) => cmp.Compare(a.Name, b.Name));
            files.Sort((a, b) => cmp.Compare(a.Name, b.Name));

            var result = new List<DockItem>();
            result.AddRange(folders);
            result.AddRange(files);
            return result;
        }

        /// <summary>把桌面项并入现有列表（跳过重复），返回新加入的个数。</summary>
        public static int ImportInto(DockSettings s, out int skipped)
        {
            skipped = 0;
            if (s == null) return 0;
            if (s.Items == null) s.Items = new List<DockItem>();

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in s.Items)
            {
                if (string.IsNullOrWhiteSpace(it.Target)) continue;
                existing.Add(it.Target);
                try { existing.Add(Path.GetFullPath(it.Target)); } catch { }
            }

            int added = 0;
            foreach (var item in Collect())
            {
                bool dup = existing.Contains(item.Target);
                if (!dup)
                {
                    try { dup = existing.Contains(Path.GetFullPath(item.Target)); } catch { }
                }
                if (dup) { skipped++; continue; }

                // 快捷方式解析出工作目录，启动更稳
                if (item.Kind == ItemKind.Launch &&
                    Path.GetExtension(item.Target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var info = ShortcutResolver.Resolve(item.Target);
                    if (!string.IsNullOrWhiteSpace(info.WorkingDirectory)) item.WorkingDirectory = info.WorkingDirectory;
                }

                s.Items.Add(item);
                existing.Add(item.Target);
                added++;
            }
            return added;
        }
    }
}
