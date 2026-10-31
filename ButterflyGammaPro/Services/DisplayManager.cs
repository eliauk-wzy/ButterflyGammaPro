using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ButterflyGammaPro.Services {
    public class DisplayMonitorInfo {
        public string DeviceName { get; set; } = ""; // e.g. @"\\.\DISPLAY1"
        public string DisplayId { get; set; } = "";   // "ALL", "PRIMARY", or @"\\.\DISPLAY1"
        public string Title { get; set; } = "";       // UI 显示标题
        public string FriendlyName { get; set; } = "";// 显示器型号
        public bool IsPrimary { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int RefreshRate { get; set; }
        public bool IsHdrEnabled { get; set; }
        public bool IsHdrSupported { get; set; }
        public int BitsPerColor { get; set; } = 8;
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

        // --- Win32 DisplayConfig HDR Query APIs ---
        [StructLayout(LayoutKind.Sequential)]
        private struct LUID {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER {
            public int type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public int colorEncoding;
            public int bitsPerColorChannel;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] byte[] pathArray, ref uint numModeInfoArrayElements, [Out] byte[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

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

            // 检测系统 HDR 状态字典
            var hdrInfoMap = QueryHdrStates();

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) => {
                var mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (GetMonitorInfo(hMon, ref mi)) {
                    bool isPrimary = (mi.dwFlags & 1) != 0;
                    int width = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    int height = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    int refreshRate = 60;

                    var dm = new DEVMODE();
                    dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                    if (EnumDisplaySettings(mi.szDevice, ENUM_CURRENT_SETTINGS, ref dm)) {
                        if (dm.dmDisplayFrequency > 0) refreshRate = dm.dmDisplayFrequency;
                        if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0) {
                            width = dm.dmPelsWidth;
                            height = dm.dmPelsHeight;
                        }
                    }

                    string friendlyName = "";
                    var dd = new DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (EnumDisplayDevices(mi.szDevice, 0, ref dd, 0)) {
                        friendlyName = dd.DeviceString;
                    }

                    string shortName = string.IsNullOrWhiteSpace(friendlyName) ? $"显示器 {index}" : friendlyName;
                    string roleTag = isPrimary ? " [主屏/游戏]" : " [副屏]";

                    bool isHdrEnabled = false;
                    bool isHdrSupported = false;
                    int bpc = 8;

                    if (hdrInfoMap.TryGetValue(index - 1, out var hdrState)) {
                        isHdrEnabled = hdrState.Enabled;
                        isHdrSupported = hdrState.Supported;
                        bpc = hdrState.Bpc;
                    }

                    string hdrTag = isHdrEnabled ? " [⚠️HDR开启]" : "";
                    string title = $"📺 {shortName} ({width}x{height}@{refreshRate}Hz){roleTag}{hdrTag}";

                    list.Add(new DisplayMonitorInfo {
                        DeviceName = mi.szDevice,
                        DisplayId = mi.szDevice,
                        Title = title,
                        FriendlyName = shortName,
                        IsPrimary = isPrimary,
                        Width = width,
                        Height = height,
                        RefreshRate = refreshRate,
                        IsHdrEnabled = isHdrEnabled,
                        IsHdrSupported = isHdrSupported,
                        BitsPerColor = bpc
                    });

                    index++;
                }
                return true;
            }, IntPtr.Zero);

            return list;
        }

        private struct HdrState {
            public bool Supported;
            public bool Enabled;
            public int Bpc;
        }

        private static Dictionary<int, HdrState> QueryHdrStates() {
            var map = new Dictionary<int, HdrState>();
            try {
                uint paths = 0, modes = 0;
                if (GetDisplayConfigBufferSizes(2, out paths, out modes) == 0 && paths > 0) {
                    byte[] pathArray = new byte[paths * 72];
                    byte[] modeArray = new byte[modes * 64];
                    if (QueryDisplayConfig(2, ref paths, pathArray, ref modes, modeArray, IntPtr.Zero) == 0) {
                        for (int i = 0; i < paths; i++) {
                            int offset = i * 72;
                            var req = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                            req.header.type = 9; // GET_ADVANCED_COLOR_INFO
                            req.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO));
                            req.header.adapterId.LowPart = BitConverter.ToUInt32(pathArray, offset + 20);
                            req.header.adapterId.HighPart = BitConverter.ToInt32(pathArray, offset + 24);
                            req.header.id = BitConverter.ToUInt32(pathArray, offset + 28);

                            if (DisplayConfigGetDeviceInfo(ref req) == 0) {
                                map[i] = new HdrState {
                                    Supported = (req.value & 1) != 0,
                                    Enabled = (req.value & 2) != 0,
                                    Bpc = req.bitsPerColorChannel > 0 ? req.bitsPerColorChannel : 8
                                };
                            }
                        }
                    }
                }
            } catch { }
            return map;
        }

        public static IntPtr GetMonitorDC(string? deviceName, out bool needsDeleteDc) {
            needsDeleteDc = true;
            if (string.IsNullOrEmpty(deviceName) || deviceName.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)) {
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
