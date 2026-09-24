using System;
using System.Runtime.InteropServices;

namespace HomeworkBoard.Core
{
    /// <summary>
    /// 原生 Win32 调用集合。
    /// 
    /// 只包含本程序真正需要的最小集合——不引用额外 NuGet 包，
    /// 保持单文件发布体积最小、启动最快。
    /// </summary>
    internal static class NativeMethods
    {
        /// <summary>显示窗口并激活。</summary>
        private const int SW_SHOW = 5;

        /// <summary>把窗口恢复为普通大小（若被最小化）。</summary>
        private const int SW_RESTORE = 9;

        /// <summary>枚举窗口时的回调委托。</summary>
        /// <param name="hWnd">窗口句柄</param>
        /// <param name="lParam">调用方传入的自定义参数</param>
        /// <returns>true = 继续枚举；false = 停止枚举</returns>
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>
        /// 主窗口标题。用于在枚举窗口时识别自己。
        /// 必须与 MainWindow.xaml 中的 Title 完全一致。
        /// </summary>
        private const string WindowTitle = "作业可视化悬浮窗";

        /// <summary>
        /// 激活已在运行的实例窗口，并把它带到前台。
        /// </summary>
        /// <returns>true = 找到了已有窗口并已激活</returns>
        /// <remarks>
        /// 实现思路：
        /// 1. 枚举所有顶级窗口，按标题匹配 + 进程名匹配找到我们的窗口；
        /// 2. 若窗口最小化则先还原；
        /// 3. 调用 SetForegroundWindow 置前。
        /// 
        /// 注意：Windows 有前台窗口锁定机制，若当前前台窗口属于其它进程，
        /// SetForegroundWindow 可能只闪烁任务栏图标而不真正置前——这是系统行为，无法绕过，
        /// 但对用户来说效果可接受（图标闪烁也在提示「程序已经在运行」）。
        /// 不抛异常。
        /// </remarks>
        public static bool ActivateExistingWindow()
        {
            IntPtr found = IntPtr.Zero;
            uint currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

            try
            {
                EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd)) return true;

                        // 标题匹配
                        int len = GetWindowTextLength(hWnd);
                        if (len <= 0) return true;
                        var sb = new System.Text.StringBuilder(len + 2);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        if (sb.ToString() != WindowTitle) return true;

                        // 进程匹配：确保是本程序自己的窗口，不会误伤同名窗口
                        uint pid;
                        GetWindowThreadProcessId(hWnd, out pid);
                        if (pid != currentPid) return true;

                        found = hWnd;
                        return false; // 找到了，停止枚举
                    }
                    catch
                    {
                        return true; // 单个窗口出错不影响整体枚举
                    }
                }, IntPtr.Zero);

                if (found == IntPtr.Zero) return false;

                if (IsIconic(found))
                {
                    ShowWindow(found, SW_RESTORE);
                }
                else
                {
                    ShowWindow(found, SW_SHOW);
                }
                SetForegroundWindow(found);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
