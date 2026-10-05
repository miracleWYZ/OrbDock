using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using OrbDock.Interop;

namespace OrbDock.Services
{
    /// <summary>
    /// 全局应急热键。使用 WH_KEYBOARD_LL 底层键盘钩子（运行在独立线程 + 独立消息循环），
    /// 在任何应用的按键消息之前截获并吞掉按键，保证最高优先级；同时用 RegisterHotKey 作为备份。
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private readonly object _gate = new object();
        private Thread _thread;
        private uint _threadId;
        private IntPtr _hook = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc _proc;   // 必须保持引用，避免被 GC
        private volatile bool _running;

        private uint _vk = 0x7B; // F12
        private bool _needCtrl = true, _needAlt = true, _needShift = false, _needWin = false;

        private IntPtr _hwndForRegister = IntPtr.Zero;
        private const int HotkeyId = 0x0B0B;

        // 全局鼠标位置（用于鼠标不在 Dock 上时也能唤出 Dock，且不占用任何点击区域）
        private IntPtr _mouseHook = IntPtr.Zero;
        private NativeMethods.LowLevelMouseProc _mouseProc;
        private volatile bool _watchMouse;

        public bool WatchMouse
        {
            get { return _watchMouse; }
            set { _watchMouse = value; }
        }

        /// <summary>钩子线程上触发（x, y 为屏幕像素）。必须立即返回，否则会拖慢全局鼠标。</summary>
        public event Action<int, int> MouseMoved;

        public string Gesture { get; private set; } = "Ctrl+Alt+F12";

        /// <summary>钩子线程上触发（同步、尽快）。</summary>
        public event Action Triggered;

        public bool IsActive { get; private set; }

        public bool Start(string gesture, IntPtr hwndForRegister)
        {
            Stop();
            if (!Apply(gesture, out var err))
            {
                Log.Warn("热键解析失败(" + gesture + "): " + err + "，回退到 Ctrl+Alt+F12");
                gesture = "Ctrl+Alt+F12";
                Apply(gesture, out err);
            }
            Gesture = gesture;
            _hwndForRegister = hwndForRegister;

            _proc = HookCallback;
            _running = true;
            _thread = new Thread(HookThread) { IsBackground = true, Name = "OrbDock-HotkeyHook" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            // 备份：注册系统热键（吞键优先级低于钩子，但可作为兜底）
            if (hwndForRegister != IntPtr.Zero)
            {
                try
                {
                    uint mods = NativeMethods.MOD_NOREPEAT;
                    if (_needCtrl) mods |= NativeMethods.MOD_CONTROL;
                    if (_needAlt) mods |= NativeMethods.MOD_ALT;
                    if (_needShift) mods |= NativeMethods.MOD_SHIFT;
                    if (_needWin) mods |= NativeMethods.MOD_WIN;
                    RegisterHotKey(hwndForRegister, HotkeyId, mods, _vk);
                }
                catch (Exception ex) { Log.Warn("RegisterHotKey 失败: " + ex.Message); }
            }

            IsActive = true;
            Log.Info("应急热键已启用: " + Gesture);
            return true;
        }

        public void UnregisterWindowHotkey()
        {
            if (_hwndForRegister != IntPtr.Zero)
            {
                try { NativeMethods.UnregisterHotKey(_hwndForRegister, HotkeyId); } catch { }
            }
        }

        private void HookThread()
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            try
            {
                IntPtr hMod = NativeMethods.GetModuleHandle(null);
                _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, hMod, 0);
                if (_hook == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Error("安装键盘钩子失败, 错误码 " + err);
                }
                else
                {
                    Log.Info("底层键盘钩子已安装 (线程 " + _threadId + ")");
                }

                _mouseProc = MouseCallback;
                _mouseHook = NativeMethods.SetWindowsHookExMouse(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
                if (_mouseHook == IntPtr.Zero)
                    Log.Warn("安装鼠标钩子失败, 错误码 " + Marshal.GetLastWin32Error());

                while (_running)
                {
                    int r = NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0);
                    if (r == 0 || r == -1) break;
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Log.Error("钩子线程异常: " + ex.Message);
            }
            finally
            {
                if (_hook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
                if (_mouseHook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_mouseHook);
                    _mouseHook = IntPtr.Zero;
                }
            }
        }

        private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && _watchMouse && MouseMoved != null)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    MouseMoved(data.pt.X, data.pt.Y);
                }
            }
            catch { }
            // 绝不吞掉鼠标消息
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
                    {
                        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                        if (data.vkCode == _vk && ModifiersMatch())
                        {
                            // 吞掉按键，任何应用都不会收到
                            Fire();
                            return new IntPtr(1);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("热键回调异常: " + ex.Message);
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private bool ModifiersMatch()
        {
            bool ctrl = Down(NativeMethods.VK_CONTROL);
            bool alt = Down(NativeMethods.VK_MENU);
            bool shift = Down(NativeMethods.VK_SHIFT);
            bool win = Down(NativeMethods.VK_LWIN) || Down(NativeMethods.VK_RWIN);
            if (_needCtrl != ctrl) return false;
            if (_needAlt != alt) return false;
            if (_needShift != shift) return false;
            if (_needWin != win) return false;
            return true;
        }

        private static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

        private void Fire()
        {
            Log.Info("应急热键触发，立即退出");
            try { Triggered?.Invoke(); }
            catch (Exception ex) { Log.Error("热键处理异常: " + ex.Message); }
        }

        /// <summary>由窗口消息 WM_HOTKEY 走到的备份通道。</summary>
        public void FireFromWindow() => Fire();

        public void Stop()
        {
            try
            {
                _running = false;
                if (_threadId != 0)
                    NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                if (_thread != null && _thread.IsAlive)
                    _thread.Join(600);
                _thread = null;
                _threadId = 0;
            }
            catch (Exception ex) { Log.Warn("停止热键服务失败: " + ex.Message); }
            IsActive = false;
        }

        public void Dispose()
        {
            Stop();
            UnregisterWindowHotkey();
        }

        // ───────── 手势解析 ─────────

        private bool Apply(string gesture, out string error)
        {
            if (!TryParse(gesture, out error)) return false;
            var parts = gesture.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries);
            bool ctrl = false, alt = false, shift = false, win = false;
            string keyPart = parts[parts.Length - 1].Trim();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "ctrl": case "control": ctrl = true; break;
                    case "alt": alt = true; break;
                    case "shift": shift = true; break;
                    case "win": case "windows": win = true; break;
                }
            }
            Enum.TryParse<Keys>(keyPart, true, out var key);
            _vk = (uint)key;
            _needCtrl = ctrl;
            _needAlt = alt;
            _needShift = shift;
            _needWin = win;
            return true;
        }

        public static bool TryParse(string gesture, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(gesture)) { error = "为空"; return false; }
            var parts = gesture.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { error = "无法识别"; return false; }
            string keyPart = parts[parts.Length - 1].Trim();
            if (!Enum.TryParse<Keys>(keyPart, true, out var key)) { error = "未知按键 " + keyPart; return false; }
            if (key == Keys.None) { error = "未知按键"; return false; }
            return true;
        }

        public static uint ToVirtualKey(string gesture)
        {
            var parts = gesture.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries);
            string keyPart = parts[parts.Length - 1].Trim();
            if (Enum.TryParse<Keys>(keyPart, true, out var key)) return (uint)key;
            return 0x7B;
        }

        public static string Normalize(string gesture)
        {
            if (!TryParse(gesture, out _)) return "Ctrl+Alt+F12";
            var parts = gesture.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries);
            bool ctrl = false, alt = false, shift = false, win = false;
            string key = null;
            foreach (var raw in parts)
            {
                var p = raw.Trim();
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": ctrl = true; break;
                    case "alt": alt = true; break;
                    case "shift": shift = true; break;
                    case "win": case "windows": win = true; break;
                    default: key = p; break;
                }
            }
            if (key != null && Enum.TryParse<Keys>(key, true, out var k)) key = k.ToString();
            var list = new List<string>();
            if (ctrl) list.Add("Ctrl");
            if (alt) list.Add("Alt");
            if (shift) list.Add("Shift");
            if (win) list.Add("Win");
            list.Add(key ?? "F12");
            return string.Join("+", list);
        }

        private static void RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk)
        {
            NativeMethods.RegisterHotKey(hwnd, id, mods, vk);
        }
    }
}
