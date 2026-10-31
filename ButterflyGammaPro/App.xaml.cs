using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace ButterflyGammaPro {
    public partial class App : System.Windows.Application {
        private static Mutex? _instanceMutex;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        private static bool IsAdministrator() {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        protected override void OnStartup(StartupEventArgs e) {
            // 1. 若非管理员权限启动，自动申请提权重启（确保与各类全屏反作弊游戏平权，无阻拦截按键）
            if (!IsAdministrator()) {
                try {
                    var psi = new ProcessStartInfo {
                        FileName = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0],
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(psi);
                    Current.Shutdown();
                    return;
                } catch {
                    // 若用户点击了取消 UAC，降级继续运行
                }
            }

            // 2. 单实例互斥锁检查
            const string mutexName = "ButterflyGammaPro_SingleInstance_Mutex_98a7c2";
            _instanceMutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew) {
                // 已经有实例在运行，直接唤醒已有窗口并退出
                IntPtr hwnd = FindWindow(null, "Butterfly Gamma Pro (小蝴蝶滤镜)");
                if (hwnd != IntPtr.Zero) {
                    ShowWindow(hwnd, 9); // SW_RESTORE
                    SetForegroundWindow(hwnd);
                }
                Current.Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e) {
            if (_instanceMutex != null) {
                try {
                    _instanceMutex.ReleaseMutex();
                    _instanceMutex.Dispose();
                } catch {}
            }
            base.OnExit(e);
        }
    }
}
