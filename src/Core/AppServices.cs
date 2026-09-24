using System;
using System.Threading;
using System.Windows.Threading;

namespace HomeworkBoard.Core
{
    /// <summary>
    /// 单实例守卫。
    /// 
    /// 需求依据：「建议单实例运行，避免多开导致 JSON 写入冲突」。
    /// 实现方式：进程级命名互斥体（Mutex）。第二个实例检测到已存在时，
    /// 会把已有窗口激活到前台并自己立即退出，用户体验上表现为「点一下就切过去」。
    /// </summary>
    public class SingleInstanceGuard : IDisposable
    {
        /// <summary>
        /// 互斥体名称。
        /// 加 Local\ 前缀表示仅对当前登录会话有效——学校电脑换人登录也能各开一个，
        /// 不会因为前一个用户没退出而互相阻塞。
        /// </summary>
        private const string MutexName = @"Local\HomeworkBoard_SingleInstance_9F3A7C21";

        private Mutex _mutex;
        private bool _ownsMutex;

        /// <summary>
        /// 尝试获取单实例所有权。
        /// </summary>
        /// <returns>true = 本进程是唯一实例；false = 已有实例在运行</returns>
        /// <remarks>
        /// 不抛异常。若互斥体创建失败（极端权限问题），返回 true 让程序继续跑，
        /// 宁可多开也不要启动不了。
        /// </remarks>
        public bool TryAcquire()
        {
            try
            {
                bool createdNew;
                _mutex = new Mutex(true, MutexName, out createdNew);
                _ownsMutex = createdNew;
                return createdNew;
            }
            catch
            {
                // 创建失败时按「唯一实例」处理，保证程序能用
                _ownsMutex = false;
                return true;
            }
        }

        /// <summary>
        /// 释放互斥体。程序退出时调用。
        /// </summary>
        /// <remarks>不抛异常。</remarks>
        public void Dispose()
        {
            try
            {
                if (_mutex != null)
                {
                    if (_ownsMutex)
                    {
                        _mutex.ReleaseMutex();
                    }
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>
    /// 延迟节流保存器。
    /// 
    /// 需求依据：「变更时自动保存，避免高频写入」。
    /// 机制：每次数据变化时调用 RequestSave，重置一个 DispatcherTimer；
    /// 只有安静 800ms 后没有新变更，才真正落盘。
    /// 这样连续勾选 10 条作业只会写 1 次磁盘。
    /// </summary>
    public class ThrottledSaver : IDisposable
    {
        /// <summary>静默等待时长（毫秒）。太短会导致频繁写盘，太长会在断电时丢数据。</summary>
        private const int DelayMs = 800;

        private readonly DispatcherTimer _timer;
        private readonly Action _saveAction;
        private bool _pending;

        /// <summary>
        /// 构造节流保存器。
        /// </summary>
        /// <param name="saveAction">真正执行保存的委托。会在 UI 线程上被调用。</param>
        /// <exception cref="ArgumentNullException">saveAction 为 null 时抛出</exception>
        /// <remarks>
        /// 使用 DispatcherTimer 而非 System.Timers.Timer：
        /// 前者回调在 UI 线程，省去跨线程同步，也不会因为后台线程与 UI 抢资源而卡顿。
        /// </remarks>
        public ThrottledSaver(Action saveAction)
        {
            if (saveAction == null) throw new ArgumentNullException("saveAction");
            _saveAction = saveAction;
            _pending = false;

            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Interval = TimeSpan.FromMilliseconds(DelayMs);
            _timer.Tick += OnTimerTick;
        }

        /// <summary>
        /// 请求一次保存。多次调用会被合并成一次实际写入。
        /// </summary>
        /// <remarks>
        /// 不抛异常。若上一次保存还在等待中，本次只是重置计时器。
        /// </remarks>
        public void RequestSave()
        {
            try
            {
                _pending = true;
                _timer.Stop();
                _timer.Start();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 立即执行一次保存（用于程序退出前兜底，防止最后几秒的修改丢失）。
        /// </summary>
        /// <returns>true = 确实执行了保存</returns>
        /// <remarks>不抛异常。没有待保存内容时直接返回 false，不写盘。</remarks>
        public bool FlushNow()
        {
            try
            {
                _timer.Stop();
                if (!_pending)
                {
                    return false;
                }
                _pending = false;
                _saveAction();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 定时器回调：执行实际保存。
        /// </summary>
        /// <param name="sender">事件源（未使用）</param>
        /// <param name="e">事件参数（未使用）</param>
        private void OnTimerTick(object sender, EventArgs e)
        {
            try
            {
                _timer.Stop();
                if (_pending)
                {
                    _pending = false;
                    _saveAction();
                }
            }
            catch
            {
                // 保存失败不崩溃，界面由调用方通过状态栏提示
            }
        }

        /// <summary>
        /// 释放定时器资源。
        /// </summary>
        /// <remarks>不抛异常。</remarks>
        public void Dispose()
        {
            try
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Tick -= OnTimerTick;
                }
            }
            catch
            {
                // 忽略
            }
        }
    }
}
