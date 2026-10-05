using System;
using System.Collections.Generic;

namespace OrbDock.Models
{
    public enum DockEdge { Bottom, Top, Left, Right }

    public enum DockAlign { Center, Start, End }

    /// <summary>窗口层级。</summary>
    public enum DockLayer
    {
        /// <summary>桌面层：永远在所有应用窗口之下（会被窗口压住、只露出没被挡的部分），但在壁纸之上。</summary>
        Desktop,
        /// <summary>普通窗口：唤出时临时浮到最前，收起后让位。</summary>
        Normal,
        /// <summary>始终置顶。</summary>
        Topmost
    }

    public enum ItemKind { Launch, Url, Folder, Command, Separator }

    public class DockItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "新项目";
        public string Target { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string IconPath { get; set; } = "";
        public ItemKind Kind { get; set; } = ItemKind.Launch;
        public bool RunAsAdmin { get; set; } = false;
        public bool Enabled { get; set; } = true;
        public string Tint { get; set; } = "";

        public DockItem Clone()
        {
            return new DockItem
            {
                Id = Id,
                Name = Name,
                Target = Target,
                Arguments = Arguments,
                WorkingDirectory = WorkingDirectory,
                IconPath = IconPath,
                Kind = Kind,
                RunAsAdmin = RunAsAdmin,
                Enabled = Enabled,
                Tint = Tint
            };
        }
    }

    /// <summary>全部可持久化设置。分组与设置界面分页一一对应。</summary>
    public class DockSettings
    {
        // ───────── 位置 ─────────
        public DockEdge Edge { get; set; } = DockEdge.Bottom;
        public int MonitorIndex { get; set; } = 0;
        public DockAlign Align { get; set; } = DockAlign.Center;
        public double Offset { get; set; } = 0;          // 沿屏幕边缘的偏移(逻辑像素)
        public double EdgeMargin { get; set; } = 6;      // 与工作区边缘的距离
        public bool AutoHide { get; set; } = true;
        public int HideDelayMs { get; set; } = 450;
        public double RevealWhenHidden { get; set; } = 4; // 隐藏时留在屏幕内的像素

        // ───────── 大小 ─────────
        public double IconSize { get; set; } = 44;
        public double IconGap { get; set; } = 12;
        public double Padding { get; set; } = 12;
        public double CornerRadius { get; set; } = 0;     // 0 = 完全胶囊形
        public int MaxVisible { get; set; } = 10;         // 超过则滚轮滚动
        public double HoverScale { get; set; } = 1.28;    // 鼠标悬停放大倍率
        public bool NeighborMagnify { get; set; } = true; // 相邻图标联动放大

        // ───────── 内容 ─────────
        public List<DockItem> Items { get; set; } = new List<DockItem>();
        public bool ShowLabels { get; set; } = true;
        public bool ShowRunningDot { get; set; } = true;
        public bool ShowSettingsButton { get; set; } = true;
        /// <summary>点击已在运行的程序时，切到它已有的窗口而不是再启动一个（单实例程序再启动不会有任何反应）。</summary>
        public bool ActivateRunning { get; set; } = true;

        // ───────── 颜色 ─────────
        public bool FollowSystemAccent { get; set; } = true;
        public string AccentColor { get; set; } = "#3D7EFF";
        public bool FollowSystemTheme { get; set; } = true;
        public string ThemeMode { get; set; } = "auto";   // auto | light | dark
        public double AccentStrength { get; set; } = 0.30;
        public double TintStrength { get; set; } = 0.22;
        public string LabelForeground { get; set; } = "";
        public string LabelBackground { get; set; } = "";

        // ───────── 外观 ─────────
        public double GlassOpacity { get; set; } = 0.62;
        public bool EnableRealBlur { get; set; } = true;
        public int BlurMode { get; set; } = 0;            // 0=高斯(流畅,Win10 推荐) 1=亚克力(Win11)
        public string BackgroundImage { get; set; } = "";
        public string ImageStretch { get; set; } = "UniformToFill";
        public double ImageOpacity { get; set; } = 0.45;
        public double ImageBlur { get; set; } = 0;
        public double NoiseAmount { get; set; } = 0.05;
        public double HighlightStrength { get; set; } = 1.0;
        public bool ShowShadow { get; set; } = true;
        public bool ShowInnerGlow { get; set; } = true;

        // ───────── 常规 / 快捷键 ─────────
        public string EmergencyHotkey { get; set; } = "Ctrl+Alt+F12";
        public bool AutoStart { get; set; } = false;
        public bool ShowTrayIcon { get; set; } = true;
        public DockLayer Layer { get; set; } = DockLayer.Desktop;
        /// <summary>把桌面图标收进 Dock 后隐藏桌面上的图标（不删除文件，随时可恢复）。</summary>
        public bool HideDesktopIcons { get; set; } = false;
        public string LaunchEffect { get; set; } = "bounce"; // bounce | none

        public static DockSettings CreateDefault()
        {
            var s = new DockSettings();
            s.Items = DefaultItems.Build();
            return s;
        }
    }
}
