using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HomeworkBoard.Core
{
    /// <summary>
    /// 屏幕定位与 DPI 辅助工具。
    /// 
    /// 需求覆盖：
    /// - 默认位置在主屏靠右，距屏幕边缘约 20px；
    /// - 不溢出屏幕；
    /// - 多显示器下不模糊、不越界、可正常拖拽；
    /// - 若默认位置超出可见区域，自动调整到可见范围。
    /// </summary>
    public static class ScreenHelper
    {
        // ================= Win32 结构体与 P/Invoke =================

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;   // 显示器完整矩形（含任务栏区域）
            public RECT rcWork;      // 工作区矩形（不含任务栏）
            public uint dwFlags;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        /// <summary>
        /// 显示器工作区信息（物理像素）。
        /// </summary>
        public struct WorkArea
        {
            /// <summary>工作区左边界（物理像素）</summary>
            public int Left;
            /// <summary>工作区上边界（物理像素）</summary>
            public int Top;
            /// <summary>工作区右边界（物理像素）</summary>
            public int Right;
            /// <summary>工作区下边界（物理像素）</summary>
            public int Bottom;
            /// <summary>工作区宽度</summary>
            public int Width { get { return Right - Left; } }
            /// <summary>工作区高度</summary>
            public int Height { get { return Bottom - Top; } }

            /// <summary>
            /// 判断一个矩形是否完全落在本工作区内。
            /// </summary>
            /// <param name="l">矩形左边界</param>
            /// <param name="t">矩形上边界</param>
            /// <param name="r">矩形右边界</param>
            /// <param name="b">矩形下边界</param>
            /// <returns>true = 完全可见</returns>
            public bool Contains(int l, int t, int r, int b)
            {
                return l >= Left && t >= Top && r <= Right && b <= Bottom;
            }
        }

        /// <summary>
        /// 获取指定物理像素坐标所在显示器的工作区。
        /// </summary>
        /// <param name="physicalX">物理像素 X 坐标</param>
        /// <param name="physicalY">物理像素 Y 坐标</param>
        /// <returns>该坐标所在显示器的工作区；API 失败时返回主显示器工作区</returns>
        /// <remarks>
        /// 使用 MonitorFromPoint + MONITOR_DEFAULTTONEAREST：
        /// 即使坐标完全在当前显示器之外，也会返回最近的显示器，保证永远有可用区域。
        /// </remarks>
        public static WorkArea GetWorkAreaAt(int physicalX, int physicalY)
        {
            var pt = new POINT { X = physicalX, Y = physicalY };
            IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (hMon == IntPtr.Zero)
            {
                return GetPrimaryWorkArea();
            }

            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(hMon, ref mi))
            {
                return GetPrimaryWorkArea();
            }

            return new WorkArea
            {
                Left = mi.rcWork.Left,
                Top = mi.rcWork.Top,
                Right = mi.rcWork.Right,
                Bottom = mi.rcWork.Bottom
            };
        }

        /// <summary>
        /// 获取主显示器工作区（物理像素）。
        /// </summary>
        /// <returns>主显示器工作区；Win32 调用失败时回退到 WPF 的 SystemParameters 值</returns>
        public static WorkArea GetPrimaryWorkArea()
        {
            try
            {
                var pt = new POINT { X = 0, Y = 0 };
                IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (hMon != IntPtr.Zero && GetMonitorInfo(hMon, ref mi))
                {
                    return new WorkArea
                    {
                        Left = mi.rcWork.Left,
                        Top = mi.rcWork.Top,
                        Right = mi.rcWork.Right,
                        Bottom = mi.rcWork.Bottom
                    };
                }
            }
            catch
            {
                // 忽略，走下面的 WPF 回退
            }

            // 回退方案：使用 WPF 的虚拟屏幕信息（逻辑像素，多显示器下精度略低但不会崩）
            return new WorkArea
            {
                Left = 0,
                Top = 0,
                Right = (int)SystemParameters.PrimaryScreenWidth,
                Bottom = (int)SystemParameters.PrimaryScreenHeight
            };
        }

        /// <summary>
        /// 计算窗口的默认位置：主屏靠右、距边缘 margin 像素、顶部对齐。
        /// </summary>
        /// <param name="windowWidthDip">窗口逻辑宽度（DIP）</param>
        /// <param name="marginDip">距屏幕边缘的间距（DIP），需求约定 20</param>
        /// <param name="dpiScale">目标显示器的 DPI 缩放比例（1.0 = 100%）</param>
        /// <returns>窗口左上角在 WPF 坐标系（逻辑像素）中的位置</returns>
        /// <remarks>
        /// 关键点：Win32 返回的坐标是物理像素，而 WPF 的 Window.Left/Top 是逻辑像素（DIP）。
        /// 多显示器且各屏缩放不同时，必须用「该屏的 DPI 比例」换算，否则窗口会跑到屏幕外。
        /// </remarks>
        public static Point CalculateDefaultPosition(double windowWidthDip, double marginDip, double dpiScale)
        {
            if (dpiScale <= 0.01) dpiScale = 1.0;

            WorkArea wa = GetPrimaryWorkArea();

            // 物理像素 -> 逻辑像素
            double waLeft = wa.Left / dpiScale;
            double waTop = wa.Top / dpiScale;
            double waRight = wa.Right / dpiScale;

            // 靠右对齐，且保证左边界不小于工作区左边界
            double left = waRight - windowWidthDip - marginDip;
            if (left < waLeft) left = waLeft + marginDip;

            double top = waTop + marginDip;

            return new Point(Math.Round(left), Math.Round(top));
        }

        /// <summary>
        /// 把窗口位置修正到可见范围内，避免窗口跑到屏幕外找不回来。
        /// </summary>
        /// <param name="window">待修正的窗口</param>
        /// <remarks>
        /// 使用窗口当前所在的显示器（多屏场景下不会把窗口强行拉回主屏）。
        /// 判断依据：若窗口矩形与工作区完全没有交集，或者窗口标题栏区域被移出工作区上方，
        /// 则把窗口重新摆到该显示器的靠右默认位置。
        /// </remarks>
        public static void EnsureVisible(Window window)
        {
            if (window == null) return;
            try
            {
                // 取窗口左上角所在的显示器（用物理像素判断）
                HwndSource source = PresentationSource.FromVisual(window) as HwndSource;
                double dpiScale = 1.0;
                if (source != null && source.CompositionTarget != null)
                {
                    dpiScale = source.CompositionTarget.TransformToDevice.M11;
                }
                if (dpiScale <= 0.01) dpiScale = 1.0;

                int physX = (int)Math.Round(window.Left * dpiScale);
                int physY = (int)Math.Round(window.Top * dpiScale);
                WorkArea wa = GetWorkAreaAt(physX, physY);

                double w = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
                double h = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
                if (double.IsNaN(w) || w <= 0) w = 400;
                if (double.IsNaN(h) || h <= 0) h = 600;

                int l = (int)Math.Round(window.Left * dpiScale);
                int t = (int)Math.Round(window.Top * dpiScale);
                int r = (int)Math.Round((window.Left + w) * dpiScale);
                int b = (int)Math.Round((window.Top + h) * dpiScale);

                // 可见性判断：至少要露出 120 物理像素宽、40 物理像素高的区域
                const int minVisibleW = 120;
                const int minVisibleH = 40;
                bool visibleX = (r > wa.Left + minVisibleW) && (l < wa.Right - minVisibleW);
                bool visibleY = (b > wa.Top + minVisibleH) && (t < wa.Bottom - minVisibleH);

                if (visibleX && visibleY)
                {
                    return; // 位置正常，不动
                }

                // 越界了，重置到该显示器靠右默认位置
                double leftDip = (wa.Right / dpiScale) - w - 20;
                double topDip = (wa.Top / dpiScale) + 20;
                if (leftDip < wa.Left / dpiScale + 20)
                {
                    leftDip = wa.Left / dpiScale + 20;
                }
                window.Left = Math.Round(leftDip);
                window.Top = Math.Round(topDip);
            }
            catch
            {
                // 任何异常都不影响程序运行，只是位置没被修正
            }
        }

        /// <summary>
        /// 把窗口整体限制在当前显示器工作区内（用于用户拖拽结束时纠偏）。
        /// </summary>
        /// <param name="window">待限制的窗口</param>
        /// <remarks>
        /// 与 EnsureVisible 的区别：本方法只保证「不完全移出屏幕」，
        /// 只要还有一小块可见就允许（符合用户拖到边缘的预期），
        /// 但绝对不允许标题栏跑到工作区上方之外（否则无法再拖回来）。
        /// </remarks>
        public static void ClampIntoWorkArea(Window window)
        {
            if (window == null) return;
            try
            {
                HwndSource source = PresentationSource.FromVisual(window) as HwndSource;
                double dpiScale = 1.0;
                if (source != null && source.CompositionTarget != null)
                {
                    dpiScale = source.CompositionTarget.TransformToDevice.M11;
                }
                if (dpiScale <= 0.01) dpiScale = 1.0;

                int physX = (int)Math.Round(window.Left * dpiScale);
                int physY = (int)Math.Round(window.Top * dpiScale);
                WorkArea wa = GetWorkAreaAt(physX, physY);

                double w = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
                double h = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
                if (double.IsNaN(w) || w <= 0) w = 400;
                if (double.IsNaN(h) || h <= 0) h = 600;

                double leftDip = window.Left;
                double topDip = window.Top;

                // 保证顶部不高于工作区顶部（标题栏始终可点）
                double minTop = wa.Top / dpiScale;
                if (topDip < minTop) topDip = minTop;

                // 保证左侧至少留 80 物理像素在屏内
                double minLeft = (wa.Left - (80 * dpiScale)) / dpiScale;
                double maxLeft = (wa.Right - (80 * dpiScale)) / dpiScale;
                if (leftDip < minLeft) leftDip = minLeft;
                if (leftDip > maxLeft) leftDip = maxLeft;

                // 保证顶部至少留 40 物理像素在屏内
                double maxTop = (wa.Bottom - (40 * dpiScale)) / dpiScale;
                if (topDip > maxTop) topDip = maxTop;

                if (Math.Abs(leftDip - window.Left) > 0.5 || Math.Abs(topDip - window.Top) > 0.5)
                {
                    window.Left = Math.Round(leftDip);
                    window.Top = Math.Round(topDip);
                }
            }
            catch
            {
                // 忽略
            }
        }
    }
}
