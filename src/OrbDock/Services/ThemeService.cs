using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace OrbDock.Services
{
    /// <summary>跟随系统主题色 / 明暗模式。</summary>
    public sealed class ThemeService
    {
        public Color SystemAccent { get; private set; } = Color.FromRgb(0x3D, 0x7E, 0xFF);
        public bool SystemUsesLightTheme { get; private set; } = false;

        public event EventHandler Changed;

        public void Refresh()
        {
            bool changed = false;
            try
            {
                var accent = ReadAccent();
                if (accent.HasValue && accent.Value != SystemAccent)
                {
                    SystemAccent = accent.Value;
                    changed = true;
                }
            }
            catch (Exception ex) { Log.Warn("读取系统主题色失败: " + ex.Message); }

            try
            {
                bool light = ReadLightTheme();
                if (light != SystemUsesLightTheme)
                {
                    SystemUsesLightTheme = light;
                    changed = true;
                }
            }
            catch (Exception ex) { Log.Warn("读取系统明暗主题失败: " + ex.Message); }

            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }

        private static bool ReadLightTheme()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                var v = k?.GetValue("SystemUsesLightTheme");
                if (v is int i) return i != 0;
            }
            // 回退：用窗口背景亮度判断
            return false;
        }

        private static Color? ReadAccent()
        {
            // 1) Windows 设置里选中的主题色（改色后最先更新）
            using (var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
            {
                if (k != null)
                {
                    // AccentColorMenu / StartColorMenu 都是 ABGR
                    var c = FromAbgr(k.GetValue("AccentColorMenu"));
                    if (c.HasValue) return c;
                    var c2 = FromAbgr(k.GetValue("StartColorMenu"));
                    if (c2.HasValue) return c2;

                    // AccentPalette：32 字节，每组 RGBA，索引 4 是系统实际使用的那一档
                    if (k.GetValue("AccentPalette") is byte[] pal && pal.Length >= 20)
                    {
                        var col = Color.FromRgb(pal[16], pal[17], pal[18]);
                        if (pal[19] != 0 || (col.R | col.G | col.B) != 0) return col;
                    }
                }
            }

            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
            {
                if (k == null) return null;
                var c = FromAbgr(k.GetValue("AccentColor"));
                if (c.HasValue) return c;
                var c2 = FromArgb(k.GetValue("ColorizationColor"));
                if (c2.HasValue) return c2;
            }
            return null;
        }

        /// <summary>注册表里以 ABGR(0xAABBGGRR) 存放的颜色。黑色/灰色也算有效颜色，要如实跟随。</summary>
        private static Color? FromAbgr(object raw)
        {
            if (raw == null) return null;
            int v;
            try { v = Convert.ToInt32(raw); } catch { return null; }
            int r = v & 0xFF;
            int g = (v >> 8) & 0xFF;
            int b = (v >> 16) & 0xFF;
            int a = (v >> 24) & 0xFF;
            if (a == 0) return null;
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }

        /// <summary>ColorizationColor 以 ARGB(0xAARRGGBB) 存放。</summary>
        private static Color? FromArgb(object raw)
        {
            if (raw == null) return null;
            int v;
            try { v = Convert.ToInt32(raw); } catch { return null; }
            int r = (v >> 16) & 0xFF;
            int g = (v >> 8) & 0xFF;
            int b = v & 0xFF;
            int a = (v >> 24) & 0xFF;
            if (a == 0) return null;
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }
    }

    public static class ColorUtil
    {
        public static Color Parse(string s, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(s.Trim());
                return c;
            }
            catch { return fallback; }
        }

        public static string ToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        public static Color WithAlpha(Color c, double a)
        {
            return Color.FromArgb((byte)Math.Max(0, Math.Min(255, a * 255.0)), c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        public static Color Lighten(Color c, double amount) => Mix(c, Colors.White, amount);
        public static Color Darken(Color c, double amount) => Mix(c, Colors.Black, amount);

        public static double Luminance(Color c)
        {
            return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        }
    }
}
