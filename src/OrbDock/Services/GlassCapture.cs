using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using OrbDock.Interop;

namespace OrbDock.Services
{
    /// <summary>
    /// 抓取 Dock 背后的屏幕内容并做高斯模糊，作为"毛玻璃"底图。
    ///
    /// 两个关键点：
    ///  1. 不用 DWM 的 SetWindowCompositionAttribute + SetWindowRgn：窗口区域裁切是 1 位掩码、**没有抗锯齿**，
    ///     在高对比背景上圆角处会露出锯齿（毛边）。
    ///  2. 也不用把 BlurEffect 加在 Border 元素上：那样模糊会把边缘"糊出去"，
    ///     在胶囊圆角外形成一圈矩形灰晕。这里先把**位图**模糊好，再作为圆角 Border 的背景刷，
    ///     背景刷会被圆角抗锯齿裁切，不会有任何外溢。
    ///
    /// 抓图时会向四周多抓一圈（pad）并用边缘像素补齐越界部分，避免模糊在图像边缘衰减。
    /// </summary>
    public static class GlassCapture
    {
        /// <summary>抓取并模糊指定区域，返回尺寸恰为 width×height 的位图。</summary>
        public static BitmapSource CaptureBlurred(double left, double top, int width, int height,
            int pad, double radius)
        {
            if (width <= 0 || height <= 0) return null;
            if (pad < 0) pad = 0;

            int l = (int)Math.Round(left);
            int t = (int)Math.Round(top);

            // 目标区域（含 pad）在虚拟桌面里的可用范围
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            int capLeft = Math.Max(vs.Left, l - pad);
            int capTop = Math.Max(vs.Top, t - pad);
            int capRight = Math.Min(vs.Right, l + width + pad);
            int capBottom = Math.Min(vs.Bottom, t + height + pad);
            int fullW = Math.Max(1, capRight - capLeft);
            int fullH = Math.Max(1, capBottom - capTop);
            int offX = l - capLeft;
            int offY = t - capTop;

            Bitmap full = null, shot = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                // 1) 抓屏（可能比目标区域小，四周用边缘像素补齐，避免模糊衰减）
                shot = new Bitmap(fullW, fullH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var sg = Graphics.FromImage(shot))
                {
                    sg.CopyFromScreen(l, t, offX, offY, new System.Drawing.Size(width, height),
                        CopyPixelOperation.SourceCopy);

                    // 用边缘像素向外拉伸补齐
                    if (offY > 0)
                        sg.DrawImage(shot, new Rectangle(offX, 0, width, offY), offX, offY, width, 1, GraphicsUnit.Pixel);
                    if (offY + height < fullH)
                        sg.DrawImage(shot, new Rectangle(offX, offY + height, width, fullH - offY - height),
                            offX, offY + height - 1, width, 1, GraphicsUnit.Pixel);
                    if (offX > 0)
                        sg.DrawImage(shot, new Rectangle(0, 0, offX, fullH), offX, 0, 1, fullH, GraphicsUnit.Pixel);
                    if (offX + width < fullW)
                        sg.DrawImage(shot, new Rectangle(offX + width, 0, fullW - offX - width, fullH),
                            offX + width - 1, 0, 1, fullH, GraphicsUnit.Pixel);
                }

                // 2) 转成 BitmapSource
                hbm = shot.GetHbitmap(System.Drawing.Color.FromArgb(0, 0, 0, 0));
                var raw = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                raw.Freeze();

                // 3) 高斯模糊
                var blurred = Blur(raw, radius);

                // 4) 裁出目标区域（模糊后的中心部分，边缘不会衰减）
                int cw = Math.Min(width, blurred.PixelWidth - offX);
                int ch = Math.Min(height, blurred.PixelHeight - offY);
                if (cw <= 0 || ch <= 0) return blurred;

                var cropped = new CroppedBitmap(blurred, new Int32Rect(offX, offY, cw, ch));
                cropped.Freeze();
                return cropped;
            }
            catch (Exception ex)
            {
                Log.Warn("抓取/模糊 Dock 背景失败: " + ex.Message);
                return null;
            }
            finally
            {
                if (hbm != IntPtr.Zero) { try { NativeMethods.DeleteObject(hbm); } catch { } }
                if (shot != null) shot.Dispose();
                if (full != null) full.Dispose();
            }
        }

        /// <summary>用 WPF 的 Gaussian BlurEffect 把位图模糊掉（渲染到离屏位图）。</summary>
        private static BitmapSource Blur(BitmapSource src, double radius)
        {
            if (src == null) return null;
            if (radius <= 0.5) return src;

            var img = new System.Windows.Controls.Image
            {
                Source = src,
                Stretch = Stretch.Fill,
                Width = src.PixelWidth,
                Height = src.PixelHeight,
                Effect = new BlurEffect
                {
                    Radius = radius,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance
                }
            };
            img.Measure(new System.Windows.Size(src.PixelWidth, src.PixelHeight));
            img.Arrange(new Rect(0, 0, src.PixelWidth, src.PixelHeight));
            img.UpdateLayout();

            var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(img);
            rtb.Freeze();
            return rtb;
        }
    }
}
