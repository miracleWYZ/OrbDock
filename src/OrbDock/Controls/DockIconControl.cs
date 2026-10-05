using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OrbDock.Models;
using OrbDock.Services;

namespace OrbDock.Controls
{
    /// <summary>Dock 里的一个图标格子（含悬停气泡、运行指示点、放大动效）。</summary>
    public sealed class DockIconControl : Grid
    {
        public DockItem Item { get; private set; }
        public bool IsSpecial { get; private set; }
        public string SpecialAction { get; private set; }

        private readonly Image _image;
        private readonly Border _bubble;
        private readonly Ellipse _dot;
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly TranslateTransform _lift = new TranslateTransform(0, 0);
        private readonly Grid _visual;

        public event Action<DockIconControl> HoverChanged;
        public event Action<DockIconControl> Clicked;
        public event Action<DockIconControl> RightClicked;

        public DockIconControl(DockItem item, double iconSize, double cellWidth, double gap,
            string iconOverride, bool isSpecial, string specialAction, bool showDot)
        {
            Item = item;
            IsSpecial = isSpecial;
            SpecialAction = specialAction;

            Width = cellWidth;
            Height = iconSize;
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;

            _bubble = new Border
            {
                Width = iconSize * 1.42,
                Height = iconSize * 1.42,
                CornerRadius = new CornerRadius(iconSize),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false,
                Background = new RadialGradientBrush(
                    Color.FromArgb(70, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255))
            };

            _visual = new Grid
            {
                Width = iconSize,
                Height = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var tg = new TransformGroup();
            tg.Children.Add(_scale);
            tg.Children.Add(_lift);
            _visual.RenderTransform = tg;

            _image = new Image
            {
                Stretch = Stretch.Uniform,
                Width = iconSize,
                Height = iconSize,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

            if (isSpecial)
            {
                _image.Source = BuildGlyph(specialAction == "settings" ? "\uE713" : "\uE710", iconSize);
            }
            else
            {
                try
                {
                    _image.Source = IconHelper.GetIcon(item.Target, string.IsNullOrWhiteSpace(item.IconPath) ? iconOverride : item.IconPath,
                        Math.Max(32, (int)(iconSize * 2)));
                }
                catch (Exception ex) { Log.Warn("加载图标失败: " + ex.Message); }
            }

            _dot = new Ellipse
            {
                Width = 4.5,
                Height = 4.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, -1),
                Fill = Brushes.White,
                Opacity = 0.9,
                Visibility = Visibility.Collapsed
            };

            _visual.Children.Add(_image);
            Children.Add(_bubble);
            Children.Add(_visual);
            if (showDot && !isSpecial) Children.Add(_dot);

            MouseEnter += OnEnter;
            MouseLeave += OnLeave;
            MouseLeftButtonDown += OnDown;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonUp += (s, e) => { RightClicked?.Invoke(this); e.Handled = true; };
        }

        public void SetDotColor(Color c) => _dot.Fill = new SolidColorBrush(c);

        public void SetRunning(bool running)
        {
            if (IsSpecial) return;
            _dot.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }

        private double _targetScale = 1.0;

        /// <summary>给图标加一层柔和阴影，让它在明亮背景的玻璃上依然清晰。</summary>
        public void SetShadow(bool on)
        {
            if (on)
            {
                if (_visual.Effect == null)
                {
                    _visual.Effect = new DropShadowEffect
                    {
                        BlurRadius = 9,
                        ShadowDepth = 2,
                        Direction = 270,
                        Color = Colors.Black,
                        Opacity = 0.42,
                        RenderingBias = RenderingBias.Performance
                    };
                }
            }
            else
            {
                _visual.Effect = null;
            }
        }

        /// <summary>按"与鼠标的距离"设置放大倍率（1 = 原大小）。</summary>
        public void SetHoverAmount(double amount)
        {
            if (Math.Abs(amount - _targetScale) < 0.002) return;
            _targetScale = amount;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(amount > 1.0 ? 140 : 190);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(amount, dur) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(amount, dur) { EasingFunction = ease });
        }

        public void SetBubble(bool on)
        {
            if (Math.Abs((on ? 1 : 0) - _bubble.Opacity) < 0.01) return;
            _bubble.BeginAnimation(OpacityProperty,
                new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 130 : 200)));
        }

        public void Bounce()        {
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(-14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)))
            { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 2 } });
            _lift.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        private void SetHeld(bool on)
        {
            _visual.Opacity = on ? 0.6 : 1.0;
        }

        private double _targetLift;

        public void SetLift(double value)
        {
            if (Math.Abs(value - _targetLift) < 0.3) return;
            _targetLift = value;
            _lift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(value, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // ───────── 交互 ─────────

        private Point _downPoint;
        private bool _down;
        private bool _dragging;

        public bool IsDragging => _dragging;

        private void OnEnter(object sender, MouseEventArgs e) { HoverChanged?.Invoke(this); e.Handled = false; }
        private void OnLeave(object sender, MouseEventArgs e) { HoverChanged?.Invoke(this); }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _down = true;
            _dragging = false;
            _downPoint = e.GetPosition(this);
            SetHeld(true);
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            bool wasDrag = _dragging;
            _down = false;
            _dragging = false;
            SetHeld(false);
            if (!wasDrag) Clicked?.Invoke(this);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_down) return;
            var p = e.GetPosition(this);
            if (!_dragging && (Math.Abs(p.X - _downPoint.X) > 7 || Math.Abs(p.Y - _downPoint.Y) > 7))
            {
                _dragging = true;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _down = false;
            _dragging = false;
            SetHeld(false);
            Cursor = Cursors.Hand;
        }

        private static ImageSource BuildGlyph(string glyph, double size)
        {
            double px = Math.Max(32, size * 2);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var ft = new FormattedText(glyph,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe MDL2 Assets"), FontStyles.Normal,
                        FontWeights.Normal, FontStretches.Normal),
                    px * 0.62, Brushes.White, 96);
                dc.DrawText(ft, new Point((px - ft.Width) / 2, (px - ft.Height) / 2));
            }
            var rtb = new RenderTargetBitmap((int)px, (int)px, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
