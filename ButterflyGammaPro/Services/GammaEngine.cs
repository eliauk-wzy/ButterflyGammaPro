using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ButterflyGammaPro.Models;

namespace ButterflyGammaPro.Services {
    public class GammaEngine : IDisposable {
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct RAMP {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;

            public static RAMP CreateEmpty() => new RAMP {
                Red = new ushort[256],
                Green = new ushort[256],
                Blue = new ushort[256]
            };

            public static RAMP CreateDefaultLinear() {
                var r = CreateEmpty();
                for (int i = 0; i < 256; i++) {
                    ushort v = (ushort)(i * 65535 / 255);
                    r.Red[i] = v;
                    r.Green[i] = v;
                    r.Blue[i] = v;
                }
                return r;
            }

            public RAMP Clone() {
                var c = CreateEmpty();
                Array.Copy(this.Red, c.Red, 256);
                Array.Copy(this.Green, c.Green, 256);
                Array.Copy(this.Blue, c.Blue, 256);
                return c;
            }
        }

        private readonly RAMP _fallbackLinearRamp = RAMP.CreateDefaultLinear();
        private readonly Dictionary<string, RAMP> _initialRamps = new(StringComparer.OrdinalIgnoreCase);
        private RAMP _currentRamp = RAMP.CreateDefaultLinear();
        private RAMP _targetRamp = RAMP.CreateDefaultLinear();
        private CancellationTokenSource? _transitionCts;
        private readonly object _rampLock = new();
        private System.Threading.Timer? _antiResetTimer;

        public bool AntiResetEnabled { get; set; } = true;
        public int TransitionDurationMs { get; set; } = 120;
        public GammaProfile? ActiveProfile { get; private set; }

        /// <summary>
        /// 目标屏幕：
        /// "ALL" = 全部显示器
        /// "PRIMARY" = 仅主显示器
        /// @"\\.\DISPLAY1" 等 = 指定具体显示器
        /// </summary>
        public string TargetMonitorId { get; set; } = "ALL";

        public GammaEngine() {
            BackupInitialRamps();

            // 进程退出或异常崩溃时，安全还原所有显示器的硬件色彩
            AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreHardwareSafely();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => RestoreHardwareSafely();

            // 防掉滤镜守护心跳：定期轮询目标屏幕的色彩状态，若被全屏独占游戏或切屏重置，则秒级自动回正
            _antiResetTimer = new System.Threading.Timer(_ => {
                if (!AntiResetEnabled || ActiveProfile == null) return;
                var targets = ResolveTargetDevices();
                foreach (var dev in targets) {
                    IntPtr hdc = DisplayManager.GetMonitorDC(dev, out bool needsDelete);
                    if (hdc == IntPtr.Zero) continue;
                    try {
                        RAMP cur = RAMP.CreateEmpty();
                        if (GetDeviceGammaRamp(hdc, ref cur)) {
                            int diff = Math.Abs(cur.Red[128] - _targetRamp.Red[128]);
                            if (diff > 1500) {
                                lock (_rampLock) {
                                    SetDeviceGammaRamp(hdc, ref _targetRamp);
                                }
                            }
                        }
                    } finally {
                        DisplayManager.ReleaseMonitorDC(hdc, needsDelete);
                    }
                }
            }, null, 1500, 1200);
        }

        public void BackupInitialRamps() {
            var monitors = DisplayManager.GetActiveMonitors();
            foreach (var mon in monitors) {
                IntPtr hdc = DisplayManager.GetMonitorDC(mon.DeviceName, out bool needsDelete);
                if (hdc != IntPtr.Zero) {
                    try {
                        RAMP cur = RAMP.CreateEmpty();
                        if (GetDeviceGammaRamp(hdc, ref cur)) {
                            _initialRamps[mon.DeviceName] = cur.Clone();
                        }
                    } finally {
                        DisplayManager.ReleaseMonitorDC(hdc, needsDelete);
                    }
                }
            }
        }

        public void SetTargetMonitor(string targetMonitorId) {
            lock (_rampLock) {
                TargetMonitorId = targetMonitorId;
                var currentTargets = new HashSet<string>(ResolveTargetDevices(), StringComparer.OrdinalIgnoreCase);
                var allMonitors = DisplayManager.GetActiveMonitors();

                // 将非目标屏幕恢复原厂初始色彩
                foreach (var mon in allMonitors) {
                    if (!currentTargets.Contains(mon.DeviceName)) {
                        if (_initialRamps.TryGetValue(mon.DeviceName, out var orig)) {
                            ApplyRampToDevice(mon.DeviceName, orig);
                        } else {
                            ApplyRampToDevice(mon.DeviceName, _fallbackLinearRamp);
                        }
                    }
                }

                // 如果当前处于滤镜激活状态，将滤镜应用到新目标屏幕
                if (ActiveProfile != null) {
                    foreach (var dev in currentTargets) {
                        ApplyRampToDevice(dev, _targetRamp);
                    }
                }
            }
        }

        public List<string> ResolveTargetDevices() {
            var active = DisplayManager.GetActiveMonitors();
            if (active.Count == 0) return new List<string> { "" };

            if (string.Equals(TargetMonitorId, "ALL", StringComparison.OrdinalIgnoreCase)) {
                return active.Select(m => m.DeviceName).ToList();
            }

            if (string.Equals(TargetMonitorId, "PRIMARY", StringComparison.OrdinalIgnoreCase)) {
                var p = active.Find(m => m.IsPrimary) ?? active[0];
                return new List<string> { p.DeviceName };
            }

            var match = active.Find(m => string.Equals(m.DeviceName, TargetMonitorId, StringComparison.OrdinalIgnoreCase));
            if (match != null) {
                return new List<string> { match.DeviceName };
            }

            // 找不到匹配时兜底全部
            return active.Select(m => m.DeviceName).ToList();
        }

        private void RestoreHardwareSafely() {
            try {
                var monitors = DisplayManager.GetActiveMonitors();
                foreach (var mon in monitors) {
                    if (_initialRamps.TryGetValue(mon.DeviceName, out var orig)) {
                        ApplyRampToDevice(mon.DeviceName, orig);
                    } else {
                        ApplyRampToDevice(mon.DeviceName, _fallbackLinearRamp);
                    }
                }
            } catch { }
        }

        public void ApplyProfile(GammaProfile profile, bool immediate = false) {
            RAMP target = CalculateRamp(profile.Red, profile.Green, profile.Blue);
            ActiveProfile = profile;
            TransitionToRamp(target, immediate ? 0 : TransitionDurationMs);
        }

        public void ResetToDefault(bool immediate = false) {
            ActiveProfile = null;
            TransitionToRamp(_fallbackLinearRamp, immediate ? 0 : TransitionDurationMs);
        }

        public static RAMP CalculateRamp(ColorChannel red, ColorChannel green, ColorChannel blue) {
            var ramp = RAMP.CreateEmpty();
            ramp.Red = BuildChannelRamp(red);
            ramp.Green = BuildChannelRamp(green);
            ramp.Blue = BuildChannelRamp(blue);
            return ramp;
        }

        private static ushort[] BuildChannelRamp(ColorChannel ch) {
            ushort[] channel = new ushort[256];
            double gammaExp = 100.0 / Math.Max(10, ch.Gamma);
            double contrastFactor = ch.Contrast / 100.0;
            double brightnessOffset = ch.Brightness / 100.0;

            for (int i = 0; i < 256; i++) {
                double val = Math.Pow(i / 255.0, gammaExp);
                val = (val - 0.5) * contrastFactor + 0.5 + brightnessOffset;
                channel[i] = (ushort)Math.Round(Math.Clamp(val, 0.0, 1.0) * 65535.0);
            }
            return channel;
        }

        private void TransitionToRamp(RAMP target, int durationMs) {
            lock (_rampLock) {
                _transitionCts?.Cancel();
                _transitionCts = new CancellationTokenSource();
                var token = _transitionCts.Token;
                _targetRamp = target.Clone();

                var targetDevices = ResolveTargetDevices();

                if (durationMs <= 0) {
                    _currentRamp = target.Clone();
                    ApplyRampToDevices(targetDevices, _targetRamp);
                    return;
                }

                RAMP start = _currentRamp.Clone();
                Task.Run(async () => {
                    const int interval = 15;
                    int steps = Math.Max(1, durationMs / interval);
                    for (int s = 1; s <= steps; s++) {
                        if (token.IsCancellationRequested) return;
                        double p = 0.5 * (1.0 - Math.Cos((double)s / steps * Math.PI));
                        RAMP inter = RAMP.CreateEmpty();
                        for (int i = 0; i < 256; i++) {
                            inter.Red[i] = (ushort)(start.Red[i] + (target.Red[i] - start.Red[i]) * p);
                            inter.Green[i] = (ushort)(start.Green[i] + (target.Green[i] - start.Green[i]) * p);
                            inter.Blue[i] = (ushort)(start.Blue[i] + (target.Blue[i] - start.Blue[i]) * p);
                        }
                        lock (_rampLock) {
                            _currentRamp = inter;
                            ApplyRampToDevices(targetDevices, _currentRamp);
                        }
                        try {
                            await Task.Delay(interval, token).ConfigureAwait(false);
                        } catch (OperationCanceledException) {
                            return;
                        }
                    }

                    if (!token.IsCancellationRequested) {
                        lock (_rampLock) {
                            _currentRamp = target.Clone();
                            ApplyRampToDevices(targetDevices, _targetRamp);
                        }
                    }
                }, token);
            }
        }

        private static void ApplyRampToDevices(IEnumerable<string> devices, RAMP ramp) {
            foreach (var dev in devices) {
                ApplyRampToDevice(dev, ramp);
            }
        }

        private static bool ApplyRampToDevice(string deviceName, RAMP ramp) {
            IntPtr hdc = DisplayManager.GetMonitorDC(deviceName, out bool needsDelete);
            if (hdc == IntPtr.Zero) return false;
            try {
                return SetDeviceGammaRamp(hdc, ref ramp);
            } finally {
                DisplayManager.ReleaseMonitorDC(hdc, needsDelete);
            }
        }

        public void Dispose() {
            _antiResetTimer?.Dispose();
            _transitionCts?.Cancel();
            RestoreHardwareSafely();
        }
    }
}
