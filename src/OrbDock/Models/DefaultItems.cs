using System;
using System.Collections.Generic;
using System.IO;

namespace OrbDock.Models
{
    /// <summary>首次启动时给出的默认内容：从系统常用程序里挑几个真实存在的。</summary>
    public static class DefaultItems
    {
        public static List<DockItem> Build()
        {
            var list = new List<DockItem>();
            void Add(string name, params string[] candidates)
            {
                foreach (var c in candidates)
                {
                    if (!string.IsNullOrWhiteSpace(c) && File.Exists(c))
                    {
                        list.Add(new DockItem { Name = name, Target = c });
                        return;
                    }
                }
            }

            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            Add("文件资源管理器", Path.Combine(win, "explorer.exe"));
            Add("Edge 浏览器",
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"));
            Add("设置", Path.Combine(win, "ImmersiveControlPanel", "SystemSettings.exe"));
            Add("记事本", Path.Combine(win, "notepad.exe"), Path.Combine(win, "System32", "notepad.exe"));
            Add("计算器", Path.Combine(win, "System32", "calc.exe"));
            Add("画图", Path.Combine(win, "System32", "mspaint.exe"));
            Add("任务管理器", Path.Combine(win, "System32", "Taskmgr.exe"));
            Add("命令提示符", Path.Combine(win, "System32", "cmd.exe"));
            Add("PowerShell", Path.Combine(win, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));

            if (list.Count == 0)
            {
                list.Add(new DockItem
                {
                    Name = "文件资源管理器",
                    Target = Path.Combine(win, "explorer.exe")
                });
            }
            return list;
        }
    }
}
