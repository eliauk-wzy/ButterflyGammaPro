using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ButterflyGammaPro.Models;

namespace ButterflyGammaPro.Services {
    public class HotKeyResult {
        public bool AllSucceeded { get; set; } = true;
        public List<string> Conflicts { get; set; } = new();
    }

    public class HotKeyManager : IDisposable {
        // --- Win32 Hook API ---
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_NCXBUTTONDOWN = 0x00AB;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12; // Alt

        public const uint VK_XBUTTON1 = 0x05; // 鼠标侧键下 (Mouse 4)
        public const uint VK_XBUTTON2 = 0x06; // 鼠标侧键上 (Mouse 5)

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT {
            public int pt_x;
            public int pt_y;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint WM_QUIT = 0x0012;

        private Thread? _hookThread;
        private uint _hookThreadId = 0;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private IntPtr _mouseHook = IntPtr.Zero;
        private HookProc? _keyboardProc;
        private HookProc? _mouseProc;
        private readonly ManualResetEvent _threadStarted = new(false);

        // 注册方案映射表 (线程安全锁保护)
        private readonly object _lock = new();
        private readonly Dictionary<string, GammaProfile> _keyProfileMap = new();

        private uint _lastTriggerKey = 0;
        private long _lastTriggerTick = 0;

        public event Action<GammaProfile>? HotKeyPressed;

        public void Initialize() {
            if (_hookThread != null) return;

            // 启动专属最高优先级后台 STA 线程运行全局钩子消息泵，完全与 WPF UI 线程解耦！
            // 彻底防止因 UI 渲染或 GC 暂停导致 Windows 系统注销 LowLevelHooksTimeout！
            _hookThread = new Thread(HookThreadLoop) {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "ButterflyGamma_ZeroLagHookThread"
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            _threadStarted.WaitOne(3000);
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private void HookThreadLoop() {
            _hookThreadId = GetCurrentThreadId();
            _keyboardProc = KeyboardHookCallback;
            _mouseProc = MouseHookCallback;

            IntPtr modHandle = IntPtr.Zero;
            try {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                if (curModule != null) modHandle = GetModuleHandle(curModule.ModuleName);
            } catch { }
            if (modHandle == IntPtr.Zero) modHandle = GetModuleHandle(null);

            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, modHandle, 0);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, modHandle, 0);

            _threadStarted.Set();

            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0)) {
                if (msg.message == WM_QUIT) break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_keyboardHook != IntPtr.Zero) {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero) {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        // --- 零输入延迟回调：立即向操作系统返回 CallNextHookEx，异步投递按键匹配 ---
        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)) {
                int vk = Marshal.ReadInt32(lParam);
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

                ThreadPool.QueueUserWorkItem(_ => CheckMatchAndTrigger((uint)vk, ctrl, alt, shift));
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
            if (nCode >= 0 && (wParam == (IntPtr)WM_XBUTTONDOWN || wParam == (IntPtr)WM_NCXBUTTONDOWN)) {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                uint xbutton = (hookStruct.mouseData >> 16) & 0xFFFF;
                uint vk = xbutton == 1 ? VK_XBUTTON1 : (xbutton == 2 ? VK_XBUTTON2 : 0);

                if (vk > 0) {
                    bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                    bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                    bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

                    ThreadPool.QueueUserWorkItem(_ => CheckMatchAndTrigger(vk, ctrl, alt, shift));
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void CheckMatchAndTrigger(uint vk, bool ctrl, bool alt, bool shift) {
            uint mods = 0;
            if (alt) mods |= 1;
            if (ctrl) mods |= 2;
            if (shift) mods |= 4;

            string key = $"{mods}_{vk}";
            GammaProfile? target = null;

            lock (_lock) {
                if (_keyProfileMap.TryGetValue(key, out var p)) {
                    target = p;
                }
            }

            if (target != null) {
                long now = Environment.TickCount64;
                if (_lastTriggerKey == vk && (now - _lastTriggerTick) < 180) {
                    return;
                }
                _lastTriggerKey = vk;
                _lastTriggerTick = now;

                HotKeyPressed?.Invoke(target);
            }
        }

        public HotKeyResult RegisterAll(IEnumerable<GammaProfile> profiles) {
            lock (_lock) {
                _keyProfileMap.Clear();
                var result = new HotKeyResult();
                var registered = new HashSet<string>();

                foreach (var p in profiles) {
                    if (p.VirtualKey > 0) {
                        string key = $"{p.Modifiers}_{p.VirtualKey}";
                        if (registered.Contains(key)) {
                            result.AllSucceeded = false;
                            result.Conflicts.Add($"{p.Name} ({p.KeyName} 重复绑定)");
                            continue;
                        }

                        _keyProfileMap[key] = p;
                        registered.Add(key);
                    }
                }
                return result;
            }
        }

        public void Dispose() {
            if (_hookThreadId != 0) {
                PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            _hookThread?.Join(1000);
            _threadStarted.Dispose();
        }
    }
}
