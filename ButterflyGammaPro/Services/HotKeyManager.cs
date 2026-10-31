using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ButterflyGammaPro.Models;

namespace ButterflyGammaPro.Services {
    public class HotKeyResult {
        public bool AllSucceeded { get; set; } = true;
        public List<string> Conflicts { get; set; } = new();
    }

    public class HotKeyManager : IDisposable {
        // --- Win32 Low-Level Keyboard Hook API ---
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        // --- RegisterHotKey API (后备机制) ---
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_NOREPEAT = 0x4000;

        private IntPtr _hWnd;
        private HwndSource? _source;
        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc? _hookProc;

        private readonly Dictionary<uint, GammaProfile> _keyToProfile = new();
        private readonly Dictionary<int, GammaProfile> _idToProfile = new();
        private int _idCounter = 1000;

        // 防抖：防止由于 Hook 和 RegisterHotKey 双重触发或按键连发
        private uint _lastTriggerKey = 0;
        private long _lastTriggerTick = 0;

        public event Action<GammaProfile>? HotKeyPressed;

        public void Initialize(IntPtr hWnd, HwndSource source) {
            _hWnd = hWnd;
            _source = source;
            _source.AddHook(HwndHook);

            // 安装全局底层键盘钩子（直接拦截操作系统输入流，无视全屏游戏焦点与 UIPI 阻断）
            InstallLowLevelHook();
        }

        private void InstallLowLevelHook() {
            if (_hookId != IntPtr.Zero) return;
            _hookProc = HookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            IntPtr modHandle = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, modHandle, 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)) {
                int vkCode = Marshal.ReadInt32(lParam);
                uint vk = (uint)vkCode;

                if (_keyToProfile.TryGetValue(vk, out var profile)) {
                    TriggerProfile(profile, vk);
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == WM_HOTKEY && _idToProfile.TryGetValue(wParam.ToInt32(), out var profile)) {
                TriggerProfile(profile, profile.VirtualKey);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void TriggerProfile(GammaProfile profile, uint vk) {
            long now = Environment.TickCount64;
            // 200ms 防抖，避免同时收到 Hook 和 RegisterHotKey 或长按重复触发
            if (_lastTriggerKey == vk && (now - _lastTriggerTick) < 200) {
                return;
            }
            _lastTriggerKey = vk;
            _lastTriggerTick = now;

            HotKeyPressed?.Invoke(profile);
        }

        public HotKeyResult RegisterAll(IEnumerable<GammaProfile> profiles) {
            ClearAll();
            var result = new HotKeyResult();

            var registeredKeys = new HashSet<uint>();

            foreach (var p in profiles) {
                if (p.VirtualKey > 0) {
                    if (registeredKeys.Contains(p.VirtualKey)) {
                        result.AllSucceeded = false;
                        result.Conflicts.Add($"{p.Name}({p.KeyName} 重复绑定)");
                        continue;
                    }

                    _keyToProfile[p.VirtualKey] = p;
                    registeredKeys.Add(p.VirtualKey);

                    // 同时注册 RegisterHotKey 作为系统级后备
                    if (_hWnd != IntPtr.Zero) {
                        int id = ++_idCounter;
                        bool ok = RegisterHotKey(_hWnd, id, MOD_NOREPEAT, p.VirtualKey);
                        if (!ok) ok = RegisterHotKey(_hWnd, id, 0, p.VirtualKey);

                        if (ok) {
                            _idToProfile[id] = p;
                        }
                    }
                }
            }
            return result;
        }

        public void ClearAll() {
            _keyToProfile.Clear();
            if (_hWnd != IntPtr.Zero) {
                foreach (var id in _idToProfile.Keys) {
                    UnregisterHotKey(_hWnd, id);
                }
            }
            _idToProfile.Clear();
        }

        public void Dispose() {
            ClearAll();
            if (_hookId != IntPtr.Zero) {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            _hookProc = null;

            if (_source != null) {
                try { _source.RemoveHook(HwndHook); } catch {}
                _source = null;
            }
        }

        public static readonly Dictionary<string, uint> KeyMap = new() {
            { "无", 0 },
            { "F1", 0x70 }, { "F2", 0x71 }, { "F3", 0x72 }, { "F4", 0x73 },
            { "F5", 0x74 }, { "F6", 0x75 }, { "F7", 0x76 }, { "F8", 0x77 },
            { "F9", 0x78 }, { "F10", 0x79 }, { "F11", 0x7A }, { "F12", 0x7B },
            { "小键盘 1", 0x61 }, { "小键盘 2", 0x62 }, { "小键盘 3", 0x63 },
            { "小键盘 4", 0x64 }, { "小键盘 5", 0x65 }, { "小键盘 6", 0x66 }
        };
    }
}
