using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace OrbDock.Services
{
    /// <summary>轻量的补间动画驱动器（DispatcherTimer + 缓动）。</summary>
    public sealed class Ticker
    {
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _watch = new Stopwatch();
        private double _from, _to;
        private int _duration;
        private Action<double> _apply;
        private Action _done;
        private bool _easeOut = true;

        public Ticker(DispatcherPriority priority = DispatcherPriority.Render)
        {
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), priority, OnTick,
                System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
            _timer.Stop();
        }

        public bool IsRunning => _timer.IsEnabled;

        public void Start(double from, double to, int durationMs, Action<double> apply,
            Action done = null, bool easeOut = true)
        {
            _from = from;
            _to = to;
            _duration = Math.Max(1, durationMs);
            _apply = apply;
            _done = done;
            _easeOut = easeOut;
            _watch.Restart();
            _timer.Start();
            _apply(from);
        }

        public void Stop()
        {
            _timer.Stop();
            _watch.Stop();
            _done = null;
            _apply = null;
        }

        private void OnTick(object sender, EventArgs e)
        {
            double t = _watch.Elapsed.TotalMilliseconds / _duration;
            if (t >= 1.0)
            {
                _timer.Stop();
                _watch.Stop();
                var apply = _apply;
                var done = _done;
                _apply = null;
                _done = null;
                apply?.Invoke(_to);
                done?.Invoke();
                return;
            }
            double k = _easeOut ? 1.0 - Math.Pow(1.0 - t, 3.0) : t;
            _apply?.Invoke(_from + (_to - _from) * k);
        }
    }
}
