using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrbDock.Models;

namespace OrbDock.Services
{
    public sealed class SettingsStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private readonly object _gate = new object();
        private DockSettings _current = new DockSettings();

        public string Directory { get; }
        public string FilePath { get; }
        public string ImageCacheDirectory { get; }

        public event EventHandler Changed;

        public SettingsStore()
        {
            Directory = ResolveDirectory();
            FilePath = Path.Combine(Directory, "settings.json");
            ImageCacheDirectory = Path.Combine(Directory, "icons");
            System.IO.Directory.CreateDirectory(ImageCacheDirectory);
        }

        /// <summary>
        /// 数据目录：默认 %APPDATA%\OrbDock，可用环境变量 ORBDOCK_DATA 指定（便携模式）。
        /// 如果被安全软件/权限拦截不可写，则依次回退到 %LOCALAPPDATA% 与程序目录。
        /// </summary>
        public static string ResolveDirectory()
        {
            try
            {
                var custom = Environment.GetEnvironmentVariable("ORBDOCK_DATA");
                if (!string.IsNullOrWhiteSpace(custom)) return custom;
            }
            catch { }

            var candidates = new List<string>();
            try
            {
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrbDock"));
            }
            catch { }
            try
            {
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbDock"));
            }
            catch { }
            try { candidates.Add(Path.Combine(AppContext.BaseDirectory, "data")); } catch { }

            foreach (var dir in candidates)
            {
                if (IsWritable(dir)) return dir;
            }
            return candidates.Count > 0 ? candidates[0] : "OrbDock";
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write_probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public DockSettings Current
        {
            get { lock (_gate) { return _current; } }
        }

        public DockSettings Load()
        {
            DockSettings s = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    s = JsonSerializer.Deserialize<DockSettings>(json, JsonOpts);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取设置失败，使用默认设置: " + ex.Message);
                TryBackupBroken();
            }

            if (s == null) s = DockSettings.CreateDefault();
            if (s.Items == null) s.Items = new System.Collections.Generic.List<DockItem>();
            Normalize(s);

            lock (_gate) { _current = s; }
            return s;
        }

        private void TryBackupBroken()
        {
            try
            {
                if (File.Exists(FilePath))
                    File.Copy(FilePath, FilePath + ".broken", true);
            }
            catch { }
        }

        public static void Normalize(DockSettings s)
        {
            s.IconSize = Clamp(s.IconSize, 16, 160);
            s.IconGap = Clamp(s.IconGap, 0, 80);
            s.Padding = Clamp(s.Padding, 0, 60);
            s.CornerRadius = Clamp(s.CornerRadius, 0, 200);
            s.MaxVisible = (int)Clamp(s.MaxVisible, 1, 60);
            s.HoverScale = Clamp(s.HoverScale, 1.0, 1.8);
            s.EdgeMargin = Clamp(s.EdgeMargin, 0, 300);
            s.Offset = Clamp(s.Offset, -20000, 20000);
            s.HideDelayMs = (int)Clamp(s.HideDelayMs, 0, 10000);
            s.RevealWhenHidden = Clamp(s.RevealWhenHidden, 0, 40);
            s.GlassOpacity = Clamp(s.GlassOpacity, 0.0, 1.0);
            s.AccentStrength = Clamp(s.AccentStrength, 0.0, 1.0);
            s.TintStrength = Clamp(s.TintStrength, 0.0, 1.0);
            s.ImageOpacity = Clamp(s.ImageOpacity, 0.0, 1.0);
            s.ImageBlur = Clamp(s.ImageBlur, 0, 80);
            s.NoiseAmount = Clamp(s.NoiseAmount, 0.0, 0.4);
            s.HighlightStrength = Clamp(s.HighlightStrength, 0.0, 3.0);
            s.BlurMode = (int)Clamp(s.BlurMode, 0, 1);
            if (s.MonitorIndex < 0) s.MonitorIndex = 0;
            if (string.IsNullOrWhiteSpace(s.AccentColor)) s.AccentColor = "#3D7EFF";
            if (string.IsNullOrWhiteSpace(s.EmergencyHotkey)) s.EmergencyHotkey = "Ctrl+Alt+F12";
            if (string.IsNullOrWhiteSpace(s.ImageStretch)) s.ImageStretch = "UniformToFill";
            if (string.IsNullOrWhiteSpace(s.ThemeMode)) s.ThemeMode = "auto";
            if (string.IsNullOrWhiteSpace(s.LaunchEffect)) s.LaunchEffect = "bounce";
            foreach (var it in s.Items)
            {
                if (it.Id == null || it.Id.Length == 0) it.Id = Guid.NewGuid().ToString("N");
                if (it.Name == null) it.Name = "";
            }
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public void Save(DockSettings settings)
        {
            if (settings == null) return;
            Normalize(settings);
            lock (_gate)
            {
                _current = settings;
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                    else File.Move(tmp, FilePath);
                }
                catch (Exception ex)
                {
                    Log.Warn("保存设置失败: " + ex.Message);
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void NotifyChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public static string SettingsFolder => ResolveDirectory();
    }

    public static class Log
    {
        private static readonly object Gate = new object();
        private static string File => Path.Combine(SettingsStore.SettingsFolder, "orblock.log");

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    System.IO.Directory.CreateDirectory(SettingsStore.SettingsFolder);
                    var line = string.Format("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}{3}",
                        DateTime.Now, level, msg, Environment.NewLine);
                    File_Append(line);
                }
            }
            catch { }
        }

        private static void File_Append(string line)
        {
            using (var fs = new FileStream(File, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs))
            {
                sw.Write(line);
            }
        }
    }
}
