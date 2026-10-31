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
        public string TargetMonitorId { get; set; } = "ALL";

        public GammaEngine() {
            BackupInitialRamps();

            AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreHardwareSafely();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => RestoreHardwareSafely();

            // 多点多通道防掉滤镜守护：以 3 秒低开销轮询作为后备巡检
            _antiResetTimer = new System.Threading.Timer(_ => {
                if (!AntiResetEnabled || ActiveProfile == null) return;
                CheckAndRestoreTargetRamps();
            }, null, 3000, 3000);
        }

        public void CheckAndRestoreTargetRamps() {
            var targets = ResolveTargetDevices();
            foreach (var dev in targets) {
                IntPtr hdc = DisplayManager.GetMonitorDC(dev, out bool needsDelete);
                if (hdc == IntPtr.Zero) continue;
                try {
                    RAMP cur = RAMP.CreateEmpty();
                    if (GetDeviceGammaRamp(hdc, ref cur)) {
                        // 多点多通道检测：暗部 (32)、中位 (128)、高光 (224) 跨 RGB 综合判断
                        bool drifted = Math.Abs(cur.Red[32] - _targetRamp.Red[32]) > 1500
                                    || Math.Abs(cur.Red[128] - _targetRamp.Red[128]) > 1500
                                    || Math.Abs(cur.Green[128] - _targetRamp.Green[128]) > 1500
                                    || Math.Abs(cur.Blue[128] - _targetRamp.Blue[128]) > 1500;
                        if (drifted) {
                            lock (_rampLock) {
                                SetDeviceGammaRamp(hdc, ref _targetRamp);
                            }
                        }
                    }
                } finally {
                    DisplayManager.ReleaseMonitorDC(hdc, needsDelete);
                }
            }
        }

        public void ReapplyActiveProfileImmediately() {
            if (ActiveProfile == null) return;
            lock (_rampLock) {
                var targets = ResolveTargetDevices();
                ApplyRampToDevices(targets, _targetRamp);
            }
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

                foreach (var mon in allMonitors) {
                    if (!currentTargets.Contains(mon.DeviceName)) {
                        if (_initialRamps.TryGetValue(mon.DeviceName, out var orig)) {
                            ApplyRampToDevice(mon.DeviceName, orig);
                        } else {
                            ApplyRampToDevice(mon.DeviceName, _fallbackLinearRamp);
                        }
                    }
                }

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
            RAMP target = CalculateRamp(profile.Red, profile.Green, profile.Blue, profile.BlackEqualizer, profile.HighlightRolloff);
            ActiveProfile = profile;
            TransitionToRamp(target, immediate ? 0 : TransitionDurationMs);
        }

        public void ResetToDefault(bool immediate = false) {
            ActiveProfile = null;
            TransitionToRamp(_fallbackLinearRamp, immediate ? 0 : TransitionDurationMs);
        }

        /// <summary>
        /// 专业 FPS 竞技调色数学模型：融合 暗部平衡 (Black Equalizer) 与 高光防刺眼滚降 (Highlight Rolloff)
        /// </summary>
        public static RAMP CalculateRamp(ColorChannel red, ColorChannel green, ColorChannel blue, int blackEqualizer = 0, int highlightRolloff = 0) {
            var ramp = RAMP.CreateEmpty();
            ramp.Red = BuildChannelRamp(red, blackEqualizer, highlightRolloff);
            ramp.Green = BuildChannelRamp(green, blackEqualizer, highlightRolloff);
            ramp.Blue = BuildChannelRamp(blue, blackEqualizer, highlightRolloff);
            return ramp;
        }

        public static ushort[] BuildChannelRamp(ColorChannel ch, int blackEqualizer = 0, int highlightRolloff = 0) {
            ushort[] channel = new ushort[256];
            double gammaExp = 100.0 / Math.Max(10, ch.Gamma);
            double contrastFactor = ch.Contrast / 100.0;
            double brightnessOffset = ch.Brightness / 100.0;

            double beWeight = (blackEqualizer / 100.0) * 0.38;
            double hlWeight = (highlightRolloff / 100.0) * 0.22;

            for (int i = 0; i < 256; i++) {
                double x = i / 255.0;

                // 1. 暗部平衡 (Black Equalizer / Shadow Lift):
                // 采用 (1-x)^2 * x 钟形增益曲线，在 15%~25% 暗部区间达到峰值提升，0% 与 100% 保持 0 偏移，不发灰、不过曝
                if (beWeight > 0.001) {
                    double lift = (1.0 - x) * (1.0 - x) * x * 4.0 * beWeight;
                    x = Math.Clamp(x + lift, 0.0, 1.0);
                }

                // 2. 灰度 Gamma 变换
                double val = Math.Pow(x, gammaExp);

                // 3. 对比度与亮度偏移
                val = (val - 0.5) * contrastFactor + 0.5 + brightnessOffset;

                // 4. 高光防刺眼抑制 (Highlight Rolloff):
                // 当高光值超过 0.65 时，施加平滑二次回退，压制室外雪盲和枪口焰火闪烁
                if (hlWeight > 0.001 && val > 0.65) {
                    double t = (val - 0.65) / 0.35;
                    val -= t * t * hlWeight;
                }

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
