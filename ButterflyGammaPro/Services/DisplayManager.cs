using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ButterflyGammaPro.Services {
    public class DisplayMonitorInfo {
        public string DeviceName { get; set; } = ""; // e.g. @"\\.\DISPLAY1"
        public string DisplayId { get; set; } = "";   // "ALL", "PRIMARY", or @"\\.\DISPLAY1"
        public string Title { get; set; } = "";       // UI 显示标题
        public string FriendlyName { get; set; } = "";// 显示器友好型号
        public bool IsPrimary { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int RefreshRate { get; set; }
    }

    public static class DisplayManager {
        // --- Win32 Monitor & Display Device APIs ---
        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DISPLAY_DEVICE {
            [MarshalAs(UnmanagedType.U4)]
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            [MarshalAs(UnmanagedType.U4)]
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DEVMODE {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public short dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("gdi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateDC(string? lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        private const int ENUM_CURRENT_SETTINGS = -1;

        public static List<DisplayMonitorInfo> GetActiveMonitors() {
            var list = new List<DisplayMonitorInfo>();
            int index = 1;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) => {
                var mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (GetMonitorInfo(hMon, ref mi)) {
                    bool isPrimary = (mi.dwFlags & 1) != 0;
                    int width = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    int height = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    int refreshRate = 60;

                    // 获取刷新率
                    var dm = new DEVMODE();
                    dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                    if (EnumDisplaySettings(mi.szDevice, ENUM_CURRENT_SETTINGS, ref dm)) {
                        if (dm.dmDisplayFrequency > 0) {
                            refreshRate = dm.dmDisplayFrequency;
                        }
                        if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0) {
                            width = dm.dmPelsWidth;
                            height = dm.dmPelsHeight;
                        }
                    }

                    // 获取显示器友好型号
                    string friendlyName = "";
                    var dd = new DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (EnumDisplayDevices(mi.szDevice, 0, ref dd, 0)) {
                        friendlyName = dd.DeviceString;
                    }

                    string shortName = string.IsNullOrWhiteSpace(friendlyName) ? $"显示器 {index}" : friendlyName;
                    string roleTag = isPrimary ? " [主屏/游戏推荐]" : " [副屏]";
                    string title = $"📺 {shortName} ({width}x{height}@{refreshRate}Hz){roleTag}";

                    list.Add(new DisplayMonitorInfo {
                        DeviceName = mi.szDevice,
                        DisplayId = mi.szDevice,
                        Title = title,
                        FriendlyName = shortName,
                        IsPrimary = isPrimary,
                        Width = width,
                        Height = height,
                        RefreshRate = refreshRate
                    });

                    index++;
                }
                return true;
            }, IntPtr.Zero);

            return list;
        }

        public static IntPtr GetMonitorDC(string? deviceName, out bool needsDeleteDc) {
            needsDeleteDc = true;
            if (string.IsNullOrEmpty(deviceName) || deviceName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) {
                // 主屏优先获取专属设备 DC
                var monitors = GetActiveMonitors();
                var primary = monitors.Find(m => m.IsPrimary);
                if (primary != null) {
                    IntPtr hdc = CreateDC(null, primary.DeviceName, null, IntPtr.Zero);
                    if (hdc != IntPtr.Zero) return hdc;
                }
                needsDeleteDc = false;
                return GetDC(IntPtr.Zero);
            }

            IntPtr devHdc = CreateDC(null, deviceName, null, IntPtr.Zero);
            if (devHdc != IntPtr.Zero) {
                return devHdc;
            }

            needsDeleteDc = false;
            return GetDC(IntPtr.Zero);
        }

        public static void ReleaseMonitorDC(IntPtr hdc, bool wasCreatedWithCreateDc) {
            if (hdc == IntPtr.Zero) return;
            if (wasCreatedWithCreateDc) {
                DeleteDC(hdc);
            } else {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }
    }
}
