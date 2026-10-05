using System;
using System.IO;

namespace OrbDock
{
    /// <summary>
    /// 诊断日志（写在程序目录的 _boot.log）。
    /// 默认关闭；设置环境变量 ORBDOCK_DEBUG=1 后开启，用于排查启动阶段问题。
    /// </summary>
    internal static class Debug
    {
        private static readonly bool Enabled =
            Environment.GetEnvironmentVariable("ORBDOCK_DEBUG") == "1";

        private static readonly string Path =
            System.IO.Path.Combine(AppContext.BaseDirectory, "_boot.log");

        public static void Marker(string message)
        {
            if (!Enabled) return;
            try
            {
                File.AppendAllText(Path,
                    string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} {1}{2}", DateTime.Now, message, Environment.NewLine));
            }
            catch { }
        }
    }
}
