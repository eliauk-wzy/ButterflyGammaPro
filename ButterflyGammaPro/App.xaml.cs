using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace ButterflyGammaPro {
    public partial class App : System.Windows.Application {
        private static Mutex? _instanceMutex;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        public static readonly uint WM_SHOWME = RegisterWindowMessage("ButterflyGammaPro_WakeUp_Msg_v1");
        private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;

        private static bool IsAdministrator() {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        protected override void OnStartup(StartupEventArgs e) {
            // 0. 自热更新替换参数处理 (彻底告别高危 .bat 脚本与杀毒软件拦截)
            for (int i = 0; i < e.Args.Length; i++) {
                if (e.Args[i] == "--replace-old" && i + 1 < e.Args.Length) {
                    string targetExe = e.Args[i + 1];
                    int waitPid = 0;
                    if (i + 3 < e.Args.Length && e.Args[i + 2] == "--wait-pid") {
                        int.TryParse(e.Args[i + 3], out waitPid);
                    }
                    PerformSelfReplacement(targetExe, waitPid);
                    Current.Shutdown();
                    return;
                }
            }

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

            // 2. 单实例互斥锁与无感广播唤醒机制（彻底解决托盘隐藏时 FindWindow 失败假死）
            const string mutexName = "ButterflyGammaPro_SingleInstance_Mutex_98a7c2";
            _instanceMutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew) {
                // 向系统所有窗口广播专属唤醒消息，无论老进程是处于前台还是最小化托盘，均能即时被唤醒
                PostMessage(HWND_BROADCAST, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);

                // 辅助兜底：若前台窗口可见，直接前置
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

        private static void PerformSelfReplacement(string targetExe, int waitPid) {
            try {
                if (waitPid > 0) {
                    try {
                        var proc = Process.GetProcessById(waitPid);
                        proc.WaitForExit(6000);
                    } catch { }
                }

                Thread.Sleep(300);

                string myPath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
                if (File.Exists(myPath) && !string.Equals(myPath, targetExe, StringComparison.OrdinalIgnoreCase)) {
                    File.Copy(myPath, targetExe, true);
                    Process.Start(new ProcessStartInfo {
                        FileName = targetExe,
                        UseShellExecute = true
                    });
                }
            } catch { }
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
