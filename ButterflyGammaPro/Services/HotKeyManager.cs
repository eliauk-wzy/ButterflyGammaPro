using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ButterflyGammaPro.Models;

namespace ButterflyGammaPro.Services {
    public class HotKeyResult {
        public bool AllSucceeded { get; set; } = true;
        public List<string> Conflicts { get; set; } = new();
    }

    public class HotKeyManager : IDisposable {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_NOREPEAT = 0x4000;

        private IntPtr _hWnd;
        private HwndSource? _source;
        private readonly Dictionary<int, GammaProfile> _registered = new();
        private int _idCounter = 1000;

        public event Action<GammaProfile>? HotKeyPressed;

        public void Initialize(IntPtr hWnd, HwndSource source) {
            _hWnd = hWnd;
            _source = source;
            _source.AddHook(HwndHook);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var p)) {
                HotKeyPressed?.Invoke(p);
                handled = true;
            }
            return IntPtr.Zero;
        }

        public HotKeyResult RegisterAll(IEnumerable<GammaProfile> profiles) {
            ClearAll();
            var result = new HotKeyResult();
            if (_hWnd == IntPtr.Zero) return result;

            var registeredKeys = new HashSet<uint>();

            foreach (var p in profiles) {
                if (p.VirtualKey > 0) {
                    if (registeredKeys.Contains(p.VirtualKey)) {
                        result.AllSucceeded = false;
                        result.Conflicts.Add($"{p.Name}({p.KeyName} 重复绑定)");
                        continue;
                    }

                    int id = ++_idCounter;
                    bool ok = RegisterHotKey(_hWnd, id, MOD_NOREPEAT, p.VirtualKey);
                    if (!ok) {
                        // Fallback without MOD_NOREPEAT
                        ok = RegisterHotKey(_hWnd, id, 0, p.VirtualKey);
                    }

                    if (ok) {
                        _registered[id] = p;
                        registeredKeys.Add(p.VirtualKey);
                    } else {
                        result.AllSucceeded = false;
                        result.Conflicts.Add($"{p.Name}({p.KeyName} 被占用)");
                    }
                }
            }
            return result;
        }

        public void ClearAll() {
            if (_hWnd != IntPtr.Zero) {
                foreach (var id in _registered.Keys) {
                    UnregisterHotKey(_hWnd, id);
                }
            }
            _registered.Clear();
        }

        public void Dispose() {
            ClearAll();
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
