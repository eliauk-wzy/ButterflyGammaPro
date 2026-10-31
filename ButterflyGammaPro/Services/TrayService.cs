using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ButterflyGammaPro.Services {
    public class TrayService : IDisposable {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;
        private const int NIM_SETVERSION = 0x00000004;

        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NIF_INFO = 0x00000010;

        public const int WM_USER = 0x0400;
        public const int WM_TRAYICON = WM_USER + 102;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_LBUTTONDBLCLK = 0x0203;
        public const int WM_RBUTTONUP = 0x0205;

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        private IntPtr _hWnd;
        private IntPtr _hIcon = IntPtr.Zero;
        private NOTIFYICONDATA _nid;
        private bool _isCreated = false;

        public event Action? DoubleClicked;
        public event Action? LeftClicked;
        public ContextMenu? ContextMenu { get; set; }

        public void Initialize(IntPtr hWnd, string tooltip, string? iconPath) {
            _hWnd = hWnd;
            if (!string.IsNullOrEmpty(iconPath) && System.IO.File.Exists(iconPath)) {
                _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }

            _nid = new NOTIFYICONDATA {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hWnd,
                uID = 1001,
                uFlags = NIF_MESSAGE | NIF_TIP | (_hIcon != IntPtr.Zero ? NIF_ICON : 0),
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = tooltip.Length > 127 ? tooltip.Substring(0, 127) : tooltip
            };

            _isCreated = Shell_NotifyIcon(NIM_ADD, ref _nid);
        }

        public void HandleMessage(int msg, IntPtr wParam, IntPtr lParam) {
            if (msg != WM_TRAYICON) return;
            int lp = lParam.ToInt32();
            if (lp == WM_LBUTTONDBLCLK) {
                DoubleClicked?.Invoke();
            } else if (lp == WM_LBUTTONUP) {
                LeftClicked?.Invoke();
            } else if (lp == WM_RBUTTONUP) {
                ShowContextMenu();
            }
        }

        public void ShowContextMenu() {
            if (ContextMenu == null) return;
            SetForegroundWindow(_hWnd);
            GetCursorPos(out POINT pt);
            ContextMenu.Placement = PlacementMode.AbsolutePoint;
            ContextMenu.HorizontalOffset = pt.X;
            ContextMenu.VerticalOffset = pt.Y;
            ContextMenu.IsOpen = true;
        }

        public void ShowBalloon(string title, string message) {
            if (!_isCreated) return;
            _nid.uFlags |= NIF_INFO;
            _nid.szInfoTitle = title.Length > 63 ? title.Substring(0, 63) : title;
            _nid.szInfo = message.Length > 255 ? message.Substring(0, 255) : message;
            _nid.dwInfoFlags = 1; // Info icon
            Shell_NotifyIcon(NIM_MODIFY, ref _nid);
        }

        public void Dispose() {
            if (_isCreated) {
                Shell_NotifyIcon(NIM_DELETE, ref _nid);
                _isCreated = false;
            }
            if (_hIcon != IntPtr.Zero) {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }
        }
    }
}
