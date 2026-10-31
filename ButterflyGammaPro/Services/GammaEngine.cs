using System;
using System.Diagnostics;
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

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

        private RAMP _originalRamp = RAMP.CreateDefaultLinear();
        private RAMP _currentRamp = RAMP.CreateDefaultLinear();
        private RAMP _targetRamp = RAMP.CreateDefaultLinear();
        private CancellationTokenSource? _transitionCts;
        private readonly object _rampLock = new();
        private System.Threading.Timer? _antiResetTimer;

        public bool AntiResetEnabled { get; set; } = true;
        public int TransitionDurationMs { get; set; } = 120;
        public GammaProfile? ActiveProfile { get; private set; }

        public GammaEngine() {
            BackupInitialRamp();

            // 进程退出时强制还原硬件色彩防异常暴光
            AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreHardwareSafely();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => RestoreHardwareSafely();

            _antiResetTimer = new System.Threading.Timer(_ => {
                if (!AntiResetEnabled || ActiveProfile == null) return;
                IntPtr hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return;
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
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }, null, 1500, 1200);
        }

        private void RestoreHardwareSafely() {
            try {
                SetHardwareRamp(_originalRamp);
            } catch {}
        }

        private void BackupInitialRamp() {
            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero) {
                try {
                    RAMP cur = RAMP.CreateEmpty();
                    if (GetDeviceGammaRamp(hdc, ref cur)) {
                        _originalRamp = cur.Clone();
                        _currentRamp = cur.Clone();
                        _targetRamp = cur.Clone();
                    }
                } finally {
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }
        }

        public void ApplyProfile(GammaProfile profile, bool immediate = false) {
            RAMP target = CalculateRamp(profile.Red, profile.Green, profile.Blue);
            ActiveProfile = profile;
            TransitionToRamp(target, immediate ? 0 : TransitionDurationMs);
        }

        public void ResetToDefault(bool immediate = false) {
            ActiveProfile = null;
            TransitionToRamp(_originalRamp, immediate ? 0 : TransitionDurationMs);
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

                if (durationMs <= 0) {
                    _currentRamp = target.Clone();
                    SetHardwareRamp(_targetRamp);
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
                            SetHardwareRamp(_currentRamp);
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
                            SetHardwareRamp(_targetRamp);
                        }
                    }
                }, token);
            }
        }

        private static bool SetHardwareRamp(RAMP ramp) {
            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;
            try {
                return SetDeviceGammaRamp(hdc, ref ramp);
            } finally {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        public void Dispose() {
            _antiResetTimer?.Dispose();
            _transitionCts?.Cancel();
            RestoreHardwareSafely();
        }
    }
}
