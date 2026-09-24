using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using HomeworkBoard.Core;
using HomeworkBoard.Views;

namespace HomeworkBoard
{
    /// <summary>
    /// 应用程序入口。
    /// 
    /// 关键职责：
    /// 1. 单实例检查——已有实例时激活它并立即退出；
    /// 2. 全局异常兜底——任何未处理异常都只是提示，绝不弹出崩溃对话框；
    /// 3. 关闭时立即落盘，防止最后几秒的修改丢失。
    /// </summary>
    public partial class App : Application
    {
        private SingleInstanceGuard _guard;
        private MainWindow _mainWindow;
        private bool _isShuttingDown;

        /// <summary>
        /// 应用程序启动回调。
        /// </summary>
        /// <param name="e">启动参数</param>
        /// <remarks>
        /// 不做任何耗时操作（不联网、不扫描磁盘、不加载大资源），
        /// 保证冷启动在 2 秒内完成。
        /// </remarks>
        protected override void OnStartup(StartupEventArgs e)
        {
            // ---------- 兜底 1：UI 线程未处理异常 ----------
            this.DispatcherUnhandledException += OnDispatcherUnhandledException;

            // ---------- 兜底 2：后台线程未处理异常 ----------
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            // ---------- 兜底 3：Task 内部未观察的异常 ----------
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            base.OnStartup(e);

            // ---------- 单实例检查 ----------
            _guard = new SingleInstanceGuard();
            if (!_guard.TryAcquire())
            {
                // 已有实例在运行：尝试激活它，然后本实例退出
                try
                {
                    NativeMethods.ActivateExistingWindow();
                }
                catch
                {
                    // 激活失败也无所谓，静默退出即可
                }

                _guard.Dispose();
                _guard = null;
                Shutdown(0);
                return;
            }

            // ---------- 创建并显示主窗口 ----------
            try
            {
                var store = new JsonStore(null); // null 表示使用 exe 所在目录
                _mainWindow = new MainWindow(store);
                this.MainWindow = _mainWindow;
                _mainWindow.Show();
            }
            catch (Exception ex)
            {
                // 极端情况：主窗口都建不起来。给一个最简单的提示后退出，不留后台僵尸进程。
                MessageBox.Show(
                    "程序启动失败：" + ex.Message + "\n\n请确认程序目录可读写，或把它解压到桌面后重试。",
                    "作业可视化悬浮窗",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown(1);
            }
        }

        /// <summary>
        /// 处理 UI 线程未捕获异常。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">异常参数</param>
        /// <remarks>
        /// 标记 Handled = true 让程序继续运行——需求明确要求「以下情况程序不得崩溃」，
        /// 因此任何异常都只提示不终止。
        /// </remarks>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            ShowGentleError(e.Exception);
        }

        /// <summary>
        /// 处理后台线程未捕获异常。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">异常参数</param>
        /// <remarks>
        /// 该事件无法阻止进程终止（IsTerminating 为 true 时系统会结束进程），
        /// 所以这里只做最后的信息记录，并尝试尽快落盘。
        /// </remarks>
        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                if (_mainWindow != null)
                {
                    _mainWindow.ForceFlush();
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 处理 Task 未观察异常。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">异常参数</param>
        /// <remarks>标记为已观察，避免 .NET 在终结器线程抛出导致进程崩溃。</remarks>
        private void OnUnobservedTaskException(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
        }

        /// <summary>
        /// 以温和方式提示异常信息——状态栏 + 非模态气泡，不弹堆栈。
        /// </summary>
        /// <param name="ex">异常对象，可能为 null</param>
        /// <remarks>不抛异常。</remarks>
        private void ShowGentleError(Exception ex)
        {
            try
            {
                string msg = "遇到一个小问题，程序已自动忽略并继续运行。";
                if (ex != null)
                {
                    msg += "（" + ex.GetType().Name + "）";
                }
                if (_mainWindow != null)
                {
                    _mainWindow.ShowToast(msg);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 应用程序退出回调。
        /// </summary>
        /// <param name="e">退出参数</param>
        /// <remarks>释放互斥体，保证下次能正常启动。</remarks>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (!_isShuttingDown)
                {
                    _isShuttingDown = true;
                    if (_guard != null)
                    {
                        _guard.Dispose();
                        _guard = null;
                    }
                }
            }
            catch
            {
                // 忽略
            }
            base.OnExit(e);
        }
    }
}
