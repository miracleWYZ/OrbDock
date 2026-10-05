using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrbDock.Interop;

namespace OrbDock.Services
{
    /// <summary>从 exe / lnk / 文件夹 / 图片中取出高质量图标（带透明通道）。</summary>
    public static class IconHelper
    {
        private static readonly ConcurrentDictionary<string, BitmapSource> Cache =
            new ConcurrentDictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] ImageExt =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tif", ".tiff" };

        public static void ClearCache() => Cache.Clear();

        public static BitmapSource GetIcon(string path, string iconOverride, int size)
        {
            string src = !string.IsNullOrWhiteSpace(iconOverride) ? iconOverride : path;
            if (string.IsNullOrWhiteSpace(src)) return null;

            string key = src + "|" + size;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            BitmapSource result = null;
            try
            {
                string ext = Path.GetExtension(src).ToLowerInvariant();

                // 注意：文件夹要算"存在"，否则 File.Exists 会一直是 false，导致文件夹永远拿不到 shell 图标
                bool isFile = false, isDir = false;
                try
                {
                    isFile = File.Exists(src);
                    isDir = Directory.Exists(src);
                }
                catch { }

                if (Array.IndexOf(ImageExt, ext) >= 0 && isFile)
                {
                    result = LoadImageFile(src, size);
                }

                if (result == null && (isFile || isDir))
                {
                    // 1) 最优先：Shell 大图标（256px，带 alpha，会带上文件夹自定义图标）
                    result = FromShellFactory(src, size);
                    // 2) 退路：系统图标列表 Jumbo
                    if (result == null) result = FromImageList(src, size);
                    // 3) 兜底：关联图标
                    if (result == null) result = FromAssociated(src);
                }

                if (result == null) result = GenericFileIcon(ext);
            }
            catch (Exception ex)
            {
                Log.Warn("提取图标失败(" + src + "): " + ex.Message);
            }

            if (result != null)
            {
                if (result.CanFreeze) result.Freeze();
                Cache[key] = result;
            }
            return result;
        }

        private static BitmapSource LoadImageFile(string file, int size)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.UriSource = new Uri(file, UriKind.Absolute);
                bmp.DecodePixelWidth = Math.Max(16, size * 2);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static BitmapSource FromShellFactory(string file, int size)
        {
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                object item;
                NativeMethods.SHCreateItemFromParsingName(file, IntPtr.Zero,
                    typeof(NativeMethods.IShellItemImageFactory).GUID, out item);
                var factory = item as NativeMethods.IShellItemImageFactory;
                if (factory == null) return null;

                int hr = factory.GetImage(new NativeMethods.SIZE(size, size),
                    NativeMethods.SIIGBF_ICONONLY | NativeMethods.SIIGBF_BIGGERSIZEOK, out hbmp);
                if (hr != 0 || hbmp == IntPtr.Zero) return null;

                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                return src;
            }
            catch { return null; }
            finally
            {
                if (hbmp != IntPtr.Zero) NativeMethods.DeleteObject(hbmp);
            }
        }

        private static BitmapSource FromImageList(string file, int size)
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                var shfi = new NativeMethods.SHFILEINFO();
                IntPtr ret = NativeMethods.SHGetFileInfo(file, 0, ref shfi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf(shfi),
                    NativeMethods.SHGFI_SYSICONINDEX);
                if (ret == IntPtr.Zero) return null;

                var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                NativeMethods.IImageList list;
                int hr = NativeMethods.SHGetImageList(
                    size >= 96 ? NativeMethods.SHIL_JUMBO : NativeMethods.SHIL_EXTRALARGE, ref iid, out list);
                if (hr != 0 || list == null) return null;

                hr = list.GetIcon(shfi.iIcon, NativeMethods.ILD_TRANSPARENT, out hIcon);
                if (hr != 0 || hIcon == IntPtr.Zero) return null;

                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                return src;
            }
            catch { return null; }
            finally
            {
                if (hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(hIcon);
            }
        }

        private static BitmapSource FromAssociated(string file)
        {
            try
            {
                using (var ico = Icon.ExtractAssociatedIcon(file))
                {
                    if (ico == null) return null;
                    var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    return src;
                }
            }
            catch { return null; }
        }

        /// <summary>没有图标时画一个通用的玻璃方块。</summary>
        private static BitmapSource GenericFileIcon(string ext)
        {
            try
            {
                int px = 128;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    var rect = new Rect(0, 0, px, px);
                    var brush = new LinearGradientBrush(
                        System.Windows.Media.Color.FromRgb(0x7A, 0x9C, 0xFF),
                        System.Windows.Media.Color.FromRgb(0x35, 0x5C, 0xD6), 55);
                    dc.DrawRoundedRectangle(brush, null, rect, 30, 30);
                    var pen = new System.Windows.Media.Pen(
                        new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 255, 255, 255)), 3);
                    dc.DrawRoundedRectangle(null, pen, new Rect(4, 4, px - 8, px - 8), 27, 27);
                    var ft = new FormattedText("◆",
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI Symbol"), 54,
                        System.Windows.Media.Brushes.White, 96);
                    dc.DrawText(ft, new System.Windows.Point((px - ft.Width) / 2, (px - ft.Height) / 2));
                }
                var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }
    }
}
