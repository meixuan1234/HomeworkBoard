using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HomeworkBoard.Core;
using HomeworkBoard.Models;
using HomeworkBoard.ViewModels;

namespace HomeworkBoard.Views
{
    /// <summary>
    /// 比例换算器：把输入数值乘以固定系数后返回。
    /// </summary>
    /// <remarks>
    /// 专用途：右边缘缩放热区需要「占窗口高度的百分之多少」而非常规像素值，
    /// WPF 的绑定没有内置百分比高度，故用绑定 + 换算器实现。
    /// </remarks>
    public class RatioConverter : System.Windows.Data.IValueConverter
    {
        /// <summary>比例系数（0—1）。</summary>
        private readonly double _ratio;

        /// <summary>
        /// 构造比例换算器。
        /// </summary>
        /// <param name="ratio">比例系数，例如 0.38 表示取输入值的 38%</param>
        public RatioConverter(double ratio)
        {
            _ratio = ratio;
        }

        /// <summary>
        /// 正向转换：输入值 × 系数。
        /// </summary>
        /// <param name="value">输入值（通常是 ActualHeight）</param>
        /// <param name="targetType">目标类型</param>
        /// <param name="parameter">未使用</param>
        /// <param name="culture">区域信息</param>
        /// <returns>相乘后的结果；输入非法时返回 0</returns>
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            try
            {
                if (value == null) return 0d;
                double d = System.Convert.ToDouble(value);
                return d * _ratio;
            }
            catch
            {
                return 0d;
            }
        }

        /// <summary>
        /// 反向转换：本程序不需要（单向绑定），直接返回输入值。
        /// </summary>
        /// <param name="value">输入值</param>
        /// <param name="targetType">目标类型</param>
        /// <param name="parameter">未使用</param>
        /// <param name="culture">区域信息</param>
        /// <returns>原样返回输入值</returns>
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value;
        }
    }

    /// <summary>
    /// 主窗口：无边框悬浮窗。
    /// 
    /// 职责：
    /// - 窗口拖拽移动（自绘标题栏，按住任意空白处拖动）；
    /// - 默认位置计算（主屏靠右、距边缘 20px、不溢出）；
    /// - 多显示器与 DPI 变化的跟随；
    /// - 跨天检测定时器；
    /// - 各类用户交互事件的分发。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly JsonStore _store;
        private readonly MainViewModel _viewModel;

        /// <summary>跨天检测定时器。60 秒一次，开销可忽略。</summary>
        private DispatcherTimer _rolloverTimer;

        /// <summary>toast 提示的自动消失定时器。</summary>
        private DispatcherTimer _toastTimer;

        /// <summary>拖拽过程中记录的鼠标按下位置（相对于窗口左上角）。</summary>
        private Point _dragOffset;

        /// <summary>是否正在拖拽窗口（鼠标路径）。</summary>
        private bool _isDragging;

        /// <summary>是否正在用触摸拖拽窗口。</summary>
        private bool _isTouchDragging;

        /// <summary>
        /// 触摸拖拽起始点（屏幕坐标，物理像素）。
        /// 之所以记屏幕坐标而不是窗口内坐标：手指移动时窗口也在动，
        /// 用窗口内坐标会自我参照导致抖动甚至失控。
        /// </summary>
        private Point _touchStartScreen;

        /// <summary>触摸拖拽开始时窗口的左上角位置。</summary>
        private Point _touchStartWindowPos;

        /// <summary>触摸按下后是否已判定为拖拽（越过最小位移阈值）。</summary>
        private bool _touchDragConfirmed;

        /// <summary>
        /// 触摸拖拽的启动阈值（物理像素）。
        /// 手指按下去总会抖几像素，不加阈值会导致「点一下按钮也把窗口拖走」。
        /// 8 像素在触摸屏上是比较合适的值——足够过滤抖动，又不会让人觉得迟钝。
        /// </summary>
        private const double TouchDragThreshold = 8.0;

        /// <summary>是否已经完成过一次默认定位（防止重复定位导致窗口乱跳）。</summary>
        private bool _hasPositioned;

        /// <summary>上次状态栏文本，用于 toast 结束后恢复。</summary>
        private string _lastStatusText;

        // ==================================================================
        // 卡片排序（第 8 轮需求，第 2 版：改用 ▲ / ▼ 按钮）
        // ==================================================================

        /// <summary>
        /// 上一次已知的窗口宽度，用于判断是否真的发生了缩放（避免冗余保存）。
        /// </summary>
        private double _lastKnownWidth = 0;

        /// <summary>上一次已知的窗口高度。</summary>
        private double _lastKnownHeight = 0;

        /// <summary>
        /// 尺寸持久化用的防抖定时器。
        /// </summary>
        /// <remarks>
        /// 用户拖窗口边框时会连续产生大量 SizeChanged（每帧一次），
        /// 每次都写盘会造成几十次冗余 IO。用 600ms 防抖：
        /// 停止调整 600ms 后才写一次。<see cref="ThrottledSaver"/> 已在
        /// 视图模型层做过保存节流，但这里控制的是「是否认定为用户意图」的判定时机，
        /// 属于不同层面的问题，因此单独一个轻量定时器。
        /// </remarks>
        private DispatcherTimer _sizeSaveTimer;

        /// <summary>
        /// 尺寸防抖间隔（毫秒）。
        /// 取值理由同 ThrottledSaver 的 800ms，但略短：拖完边框用户马上松手，
        /// 500—600ms 足够区分「还在拖」与「拖完了」。
        /// </summary>
        private const int SizeSaveDebounceMs = 600;

        /// <summary>
        /// 构造主窗口。
        /// </summary>
        /// <param name="store">JSON 存储管理器，不可为 null</param>
        /// <exception cref="ArgumentNullException">store 为 null 时抛出</exception>
        /// <remarks>
        /// 构造流程刻意保持轻量：建视图模型（读两个小 JSON）-> 绑定 -> 定位。
        /// 不做任何磁盘遍历、不加载图片资源。
        /// </remarks>
        public MainWindow(JsonStore store)
        {
            if (store == null) throw new ArgumentNullException("store");

            InitializeComponent();

            _store = store;
            _viewModel = new MainViewModel(_store);
            _lastStatusText = string.Empty;

            this.DataContext = _viewModel;

            // 恢复上次手动调整过的窗口尺寸。
            // 之所以在 XAML 里写死 Width=380/Height=720 之外还要在这里赋一次：
            // XAML 的 Width 只是初始值，用户上次拖过握把后应该保持那个尺寸。
            // 位置按需求不记忆，但尺寸记忆（见 MainViewModel.SetManualWindowSize 的说明）。
            ApplyInitialWindowSize();

            // 尺寸与位置相关的钩子
            // 注意：定位必须在 ContentRendered 而不是 Loaded 里做。
            // Loaded 触发时布局尚未完成，ActualWidth 可能还是 0，
            // 会导致「靠右 20px」算出一个跑到屏幕外的坐标，表现为「程序启动了但看不到界面」。
            this.ContentRendered += OnWindowContentRendered;
            this.Closing += OnWindowClosing;
            this.LocationChanged += OnLocationChanged;
            this.DpiChanged += OnDpiChanged;
            this.SizeChanged += OnWindowSizeChanged;

            // 注册四周/四角的系统缩放热区。
            // 必须在 InitializeComponent 之后（此时 RootLayout 已构造完成）。
            // 这是让"无边框分层窗口也能拖边框改大小"的唯一可靠路径，详见方法注释。
            InstallResizeHotspots(RootLayout);

            // 订阅「设置面板调整窗口宽高」的请求。
            // 视图模型不持有 Window 引用，因此用事件解耦；
            // 没有这条订阅，设置里的宽/高滑块就只是改了 JSON、界面上毫无反应。
            _viewModel.WindowSizeRequested += OnWindowSizeRequested;

            ApplyTopmostState();
        }

        /// <summary>
        /// 响应「设置面板调整窗口宽高」的请求：实时改变本窗口尺寸。
        /// </summary>
        /// <param name="sender">事件源（主视图模型）</param>
        /// <param name="e">目标尺寸（已由视图模型夹紧）</param>
        /// <remarks>
        /// 为什么不再尝试拖边框缩放：
        /// AllowsTransparency="True" 使窗口成为分层窗口，系统不提供任何原生缩放热区，
        /// 自绘热区 + WM_SYSCOMMAND 的方案在实机上也不可靠。
        /// 改为「设置面板滑块 + 实时应用」是最确定可控的路径：
        /// 直接赋值 Width/Height，避开一切层窗口与命中测试的坑。
        /// 
        /// 先夹到 MaxHeight（它已与工作区高度取小值）再赋值，
        /// 否则用户把高度调大后窗口会压到任务栏下面。
        /// </remarks>
        private void OnWindowSizeRequested(object sender, WindowSizeEventArgs e)
        {
            try
            {
                if (e == null) return;

                double w = e.Width;
                double h = e.Height;

                // 与窗口自身的 Min/Max 属性对齐，避免赋值被 WPF 静默夹紧后
                // 与实际落盘值不一致（带来"设置显示 500、窗口却是 280"的困惑）
                if (w < this.MinWidth) w = this.MinWidth;
                if (h < this.MinHeight) h = this.MinHeight;

                double maxH = this.MaxHeight;
                if (!double.IsNaN(maxH) && maxH > 0 && h > maxH) h = maxH;

                if (w > 1) this.Width = w;
                if (h > 1) this.Height = h;

                _lastKnownWidth = this.Width;
                _lastKnownHeight = this.Height;

                // 宽度变了要立刻重算分行，否则卡片会按旧宽度排布
                SyncLayoutWidth();

                // 尺寸已由视图模型写入配置并触发节流保存，这里不必再存一次
            }
            catch
            {
                // 忽略：尺寸应用失败不影响使用
            }
        }

        /// <summary>
        /// 应用启动时的窗口尺寸（用户上次调整过的，或默认值）。
        /// </summary>
        /// <remarks>
        /// 尺寸改动后必须让视图模型知道新的宽度，否则第一次分行会用 380 估算，
        /// 表现为「宽窗口启动时卡片挤在一行左边，右边一大片空白」。
        /// 这里有意不调 SetLayoutWidth（阈值 20 像素内不生效），
        /// 而是等 ContentRendered 里窗口真正布局完成后由 OnWindowSizeChanged 触发。
        /// </remarks>
        private void ApplyInitialWindowSize()
        {
            try
            {
                double w = _viewModel.InitialWindowWidth;
                double h = _viewModel.InitialWindowHeight;

                if (w > 1) this.Width = w;
                if (h > 1) this.Height = h;

                _lastKnownWidth = this.Width;
                _lastKnownHeight = this.Height;
            }
            catch
            {
                // 取不到就用 XAML 里的默认值
            }
        }

        /// <summary>
        /// 窗口内容渲染完成：此时布局已确定，尺寸可靠，适合计算默认位置。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>
        /// 只执行一次定位。用标志位防止后续内容变化（如新增作业导致窗口变宽）
        /// 触发重复定位——那会让窗口在用户操作时乱跳。
        /// </remarks>
        private void OnWindowContentRendered(object sender, EventArgs e)
        {
            try
            {
                if (_hasPositioned)
                {
                    return;
                }
                _hasPositioned = true;

                // 把实测宽度告诉视图模型，让它按真实宽度分行。
                // 必须先于 PositionToDefaultLocation：后者依赖布局已完成。
                SyncLayoutWidth();

                PositionToDefaultLocation();
                StartRolloverTimer();
            }
            catch (Exception ex)
            {
                ShowToast("初始化位置时遇到小问题：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 把窗口摆到默认位置：主屏靠右、距边缘 edgeMargin 像素、顶部对齐。
        /// </summary>
        /// <remarks>
        /// 需求：「默认显示在主屏靠右，不溢出屏幕，距屏幕边缘约 20px」+「窗口位置不做记忆化」。
        /// 因此每次启动都重新计算，不读取也不保存位置。
        /// </remarks>
        private void PositionToDefaultLocation()
        {
            try
            {
                // 取窗口实际宽度。ContentRendered 时通常已有效，
                // 但仍加两层兜底：ActualWidth 无效 -> 用 Width 属性 -> 用硬编码默认值。
                double w = this.ActualWidth;
                if (double.IsNaN(w) || w <= 1)
                {
                    w = this.Width;
                }
                if (double.IsNaN(w) || w <= 1)
                {
                    w = 380; // 最终兜底：与默认列宽 320 + 内边距相当
                }

                // 取当前窗口所在显示器的 DPI 缩放
                double dpiScale = GetCurrentDpiScale();

                // 把 DPI 比例告知视图模型：MaxWindowHeight / InitialWindowHeight 需要用它在
                // 「配置高度」与「工作区高度」之间取小值，防止窗口长到任务栏下面。
                _viewModel.SetDpiScale(dpiScale);

                // 构造函数里应用初始尺寸时 _dpiScaleCache 还是默认的 1.0，
                // 高 DPI 屏上算出的高度上限会偏大（实际工作区逻辑高度更小）。
                // 现在 DPI 已知，重新夹紧一次高度，避免窗口一启动就压到任务栏。
                ReapplyHeightLimit();

                int margin = _viewModel.WindowSettings.EdgeMargin;
                if (margin <= 0) margin = 20;

                // 重新测一次尺寸：SetDpiScale 可能刚把 MaxWindowHeight 压低，
                // 布局需要走一遍才能得到新的实际高度，否则位置会按旧高度计算。
                this.UpdateLayout();

                Point pos = ScreenHelper.CalculateDefaultPosition(w, margin, dpiScale);
                this.Left = pos.X;
                this.Top = pos.Y;

                // 双重保险：再检查一次是否在可见范围内
                ScreenHelper.EnsureVisible(this);
            }
            catch
            {
                // 定位失败时用最保守的位置，保证窗口至少可见
                try
                {
                    this.Left = 40;
                    this.Top = 40;
                }
                catch { }
            }
        }

        /// <summary>
        /// 按当前 DPI 与工作区重新夹紧窗口高度。
        /// </summary>
        /// <remarks>
        /// 为什么需要这一步：InitialWindowHeight 依赖 MaxWindowHeight，
        /// 而 MaxWindowHeight 依赖 _dpiScaleCache（工作区物理高度 / 缩放比）。
        /// 构造函数阶段 DPI 尚未测出（缓存是 1.0），在 125% 缩放的屏幕上
        /// 会把工作区算高 25%，于是恢复出一个超出屏幕的高度。
        /// 这个方法在 ContentRendered 里 DPI 已知后调用，把高度压回合法范围。
        /// 
        /// 只在当前高度超过上限时才改，不做任何"往上撑"的操作——
        /// 用户把窗口调小是明确的意图，不该被程序改回去。
        /// </remarks>
        private void ReapplyHeightLimit()
        {
            try
            {
                double cap = _viewModel.MaxWindowHeight;
                if (cap < 1) return;

                double h = this.ActualHeight > 1 ? this.ActualHeight : this.Height;
                if (double.IsNaN(h) || h < 1) return;

                if (h > cap)
                {
                    this.Height = cap;
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 获取当前窗口所在显示器的 DPI 缩放比例。
        /// </summary>
        /// <returns>缩放比例，1.0 表示 100%；获取失败时返回 1.0</returns>
        /// <remarks>
        /// 通过 HwndSource 的 TransformToDevice 矩阵取 M11 分量，
        /// 该值在 PerMonitorV2 模式下就是当前显示器的实际缩放。
        /// </remarks>
        private double GetCurrentDpiScale()
        {
            try
            {
                System.Windows.Interop.HwndSource source =
                    System.Windows.Interop.HwndSource.FromHwnd(
                        new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if (source != null && source.CompositionTarget != null)
                {
                    double scale = source.CompositionTarget.TransformToDevice.M11;
                    if (scale > 0.01) return scale;
                }
            }
            catch
            {
                // 忽略
            }
            return 1.0;
        }

        /// <summary>
        /// 把窗口当前的实际宽度同步给视图模型（供分行算法使用）。
        /// </summary>
        /// <remarks>
        /// 取 ActualWidth 而非 Width：Width 是「期望值」，拖动窗口边框的过程中两者会短暂不一致，
        /// 用期望值算出来的分行结果会与实际渲染不符（换行点跳一下再跳回来）。
        /// </remarks>
        private void SyncLayoutWidth()
        {
            try
            {
                double w = this.ActualWidth;
                if (double.IsNaN(w) || w <= 1)
                {
                    w = this.Width;
                }
                if (double.IsNaN(w) || w <= 1) return;

                _viewModel.SetLayoutWidth(w);
            }
            catch
            {
                // 忽略：布局同步失败不应打断主流程
            }
        }

        /// <summary>
        /// 窗口尺寸变化事件：按新宽度重新分行，并防抖保存尺寸。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">包含新旧尺寸的事件参数</param>
        /// <remarks>
        /// 触发时机包括：程序应用启动尺寸、用户拖动窗口边框（WindowChrome 的系统热区）、
        /// 系统 DPI 变化。
        /// 
        /// 分行同步由 SetLayoutWidth 做 20 像素阈值过滤，高频触发无性能问题。
        /// 
        /// 尺寸保存走 600ms 防抖（RestartSizeSaveTimer）：
        /// 拖边框时 SizeChanged 每帧都会触发，直接写盘会造成几十次冗余 IO。
        /// 防抖窗口内若尺寸没再变化，才认定"用户拖完了"并落盘。
        /// 这样既避免了把"DIP 变化导致的程序性缩放"记成用户意图
        /// （那种情况尺寸变化后不会再变，防抖到点时会保存 —— 但那也是合理的实际尺寸）。
        /// </remarks>
        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                if (e.NewSize.Width <= 1) return;
                SyncLayoutWidth();

                _lastKnownWidth = e.NewSize.Width;
                _lastKnownHeight = e.NewSize.Height;

                // 只有尺寸真的变了才启动防抖（启动阶段 SizeChanged 可能因内容布局触发）
                if (e.PreviousSize.Width > 1 || e.PreviousSize.Height > 1)
                {
                    RestartSizeSaveTimer();
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 启动跨天检测定时器。
        /// </summary>
        /// <remarks>
        /// 间隔 60 秒。它是本程序唯一的常驻定时器，且回调里只做一次 DateTime.Today 比较，
        /// CPU 占用可以忽略（需求要求空闲 CPU 接近 0%）。
        /// </remarks>
        private void StartRolloverTimer()
        {
            try
            {
                _rolloverTimer = new DispatcherTimer(DispatcherPriority.Background);
                _rolloverTimer.Interval = TimeSpan.FromSeconds(60);
                _rolloverTimer.Tick += OnRolloverTick;
                _rolloverTimer.Start();
            }
            catch
            {
                // 定时器建不起来也不影响主功能，用户手动改日期仍可用
            }
        }

        /// <summary>
        /// 跨天检测回调。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>只有日期真的变了才会做重建操作，平时零开销。</remarks>
        private void OnRolloverTick(object sender, EventArgs e)
        {
            try
            {
                if (_viewModel.CheckDateRollover())
                {
                    // 跨天后窗口内容可能变少，重新摆一次位置避免留大片空白在屏幕外
                    ScreenHelper.EnsureVisible(this);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 标题栏鼠标按下：开始拖拽窗口。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">鼠标事件参数</param>
        /// <remarks>
        /// 需求：「支持鼠标拖拽移动窗口」。
        /// 使用「记录相对偏移 + LocationChanged 更新」的方式，比 DragMove() 更稳
        /// （DragMove 在 AllowsTransparency 窗口上偶发卡死）。
        /// 只响应鼠标左键，且排除点在交互控件上的情况（否则点按钮会变成拖窗口）。
        /// </remarks>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != MouseButton.Left) return;

                // 点在按钮等可交互控件上时不拖拽
                if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

                _dragOffset = e.GetPosition(this);
                _isDragging = true;
                this.CaptureMouse();

                e.Handled = true;
            }
            catch
            {
                _isDragging = false;
            }
        }

        /// <summary>
        /// 标题栏触摸按下：开始触摸拖拽流程。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">触摸事件参数</param>
        /// <remarks>
        /// 触屏设计的核心：WPF 会把触摸**同时**提升为鼠标事件，
        /// 如果两套逻辑都生效会互相打架（表现为窗口抖动或跳位）。
        /// 因此本方法会先标记 e.Handled = true 阻断后续的鼠标事件提升，
        /// 再自己完全接管拖拽。
        /// 
        /// 触摸点落在按钮等交互控件上时直接放行，让按钮正常响应点击。
        /// </remarks>
        private void TitleBar_TouchDown(object sender, TouchEventArgs e)
        {
            try
            {
                // 点在按钮等可交互控件上时不拖拽，让按钮自己处理
                if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

                // 记录触摸起点（屏幕物理坐标）
                TouchDevice device = e.TouchDevice;
                _touchStartScreen = device.GetTouchPoint(null).Position;

                // 记录窗口当前位置（逻辑坐标，WPF 的 Left/Top）
                _touchStartWindowPos = new Point(this.Left, this.Top);

                _isTouchDragging = true;
                _touchDragConfirmed = false;

                // 捕获触摸：手指移出标题栏范围后仍能继续拖动
                TitleBar.CaptureTouch(device);

                // 阻断鼠标事件提升，避免与鼠标拖拽逻辑冲突
                e.Handled = true;
            }
            catch
            {
                _isTouchDragging = false;
            }
        }

        /// <summary>
        /// 标题栏触摸移动：按位移量搬动窗口。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">触摸事件参数</param>
        /// <remarks>
        /// 关键点：使用**屏幕坐标差值**而不是窗口内坐标。
        /// 因为窗口本身在移动，若用窗口内坐标做参照系会形成自我参照，
        /// 导致手指不动窗口也自己跑（正反馈抖动）。
        /// 
        /// 屏幕坐标是物理像素，窗口 Left/Top 是逻辑像素，
        /// 因此除以 DPI 缩放比例完成换算——这是高 DPI 下「手指走 1cm 窗口也应走 1cm」的关键。
        /// </remarks>
        private void TitleBar_TouchMove(object sender, TouchEventArgs e)
        {
            if (!_isTouchDragging) return;

            try
            {
                TouchDevice device = e.TouchDevice;
                Point currentScreen = device.GetTouchPoint(null).Position;

                // 物理像素位移
                double dxPhysical = currentScreen.X - _touchStartScreen.X;
                double dyPhysical = currentScreen.Y - _touchStartScreen.Y;

                // 未越过阈值：视为手指抖动，先不动窗口
                if (!_touchDragConfirmed)
                {
                    if (Math.Abs(dxPhysical) < TouchDragThreshold && Math.Abs(dyPhysical) < TouchDragThreshold)
                    {
                        e.Handled = true;
                        return;
                    }
                    _touchDragConfirmed = true;
                }

                // 物理像素 -> 逻辑像素
                double dpiScale = GetCurrentDpiScale();
                if (dpiScale <= 0.01) dpiScale = 1.0;

                double newLeft = _touchStartWindowPos.X + (dxPhysical / dpiScale);
                double newTop = _touchStartWindowPos.Y + (dyPhysical / dpiScale);

                this.Left = Math.Round(newLeft);
                this.Top = Math.Round(newTop);

                e.Handled = true;
            }
            catch
            {
                EndTouchDrag();
            }
        }

        /// <summary>
        /// 标题栏触摸抬起：结束拖拽并做越界纠偏。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">触摸事件参数</param>
        /// <remarks>
        /// 只有真正拖动过（越过阈值）才做纠偏，单纯点一下标题栏不触发任何位置变化。
        /// </remarks>
        private void TitleBar_TouchUp(object sender, TouchEventArgs e)
        {
            try
            {
                if (_isTouchDragging && _touchDragConfirmed)
                {
                    ScreenHelper.ClampIntoWorkArea(this);
                }
            }
            catch
            {
                // 忽略
            }
            finally
            {
                EndTouchDrag();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 结束触摸拖拽状态。
        /// </summary>
        /// <remarks>释放触摸捕获，重置标志位。不抛异常。</remarks>
        private void EndTouchDrag()
        {
            try
            {
                _isTouchDragging = false;
                _touchDragConfirmed = false;

                if (TitleBar != null && TitleBar.AreAnyTouchesCaptured)
                {
                    TitleBar.ReleaseAllTouchCaptures();
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 鼠标移动：更新窗口位置。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">鼠标事件参数</param>
        /// <remarks>仅在想拖拽时生效。不做任何边界限制，允许用户自由摆放。</remarks>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!_isDragging) return;

            try
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    EndDrag();
                    return;
                }

                Point current = e.GetPosition(this);
                double dx = current.X - _dragOffset.X;
                double dy = current.Y - _dragOffset.Y;

                this.Left += dx;
                this.Top += dy;
            }
            catch
            {
                EndDrag();
            }
        }

        /// <summary>
        /// 鼠标松开：结束拖拽并纠偏。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">鼠标事件参数</param>
        /// <remarks>
        /// 需求：「多显示器环境下窗口不模糊、不越界、可正常拖拽」。
        /// 因此松手时做一次 ClampIntoWorkArea：保证标题栏不会被拖出屏幕顶部而找不回来。
        /// </remarks>
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_isDragging)
            {
                EndDrag();
                try
                {
                    ScreenHelper.ClampIntoWorkArea(this);
                }
                catch
                {
                    // 忽略
                }
            }
        }

        /// <summary>
        /// 结束拖拽状态。
        /// </summary>
        /// <remarks>不抛异常。</remarks>
        private void EndDrag()
        {
            try
            {
                _isDragging = false;
                if (this.IsMouseCaptured)
                {
                    this.ReleaseMouseCapture();
                }
            }
            catch
            {
                // 忽略
            }
        }

        // ==================================================================
        // 卡片排序（▲ / ▼ 按钮）
        // ==================================================================

        /// <summary>
        /// 「▲」上移按钮：把本卡片在排序中往前移一位。
        /// </summary>
        /// <param name="sender">被点击的按钮，其 Tag 绑定到对应的科目卡片视图模型</param>
        /// <param name="e">路由事件参数</param>
        /// <remarks>
        /// 实现取舍说明（为什么从长按拖拽改为按钮）：
        /// 上一版用「长按 0.5 秒后拖拽」的手势，实机验证失败。原因有二：
        /// ① 卡片位于 ScrollViewer 内，其 PanningMode="VerticalOnly" 会抢先消费触摸事件
        ///    （ScrollViewer 把 TouchDown 用于滚动", 卡片的 TouchDown 往往收不到，
        ///    或收到后立刻被 TouchLeave 取消）；
        /// ② 鼠标路径虽能触发，但用户已明确表示该交互"无法调整"。
        /// 改用按钮后完全不依赖手势识别，触屏与鼠标行为一致，且当前排序位（首/末）一眼可见。
        /// 
        /// 事务性：MoveCard 内部完成重排 + 落盘，成功率与索引合法性由 ViewModel 保证，
        /// 这里只负责取 Tag 与计算方向。移不动时（已在首位）不报错，只更新状态栏提示。
        /// </remarks>
        private void BtnCardUp_Click(object sender, RoutedEventArgs e)
        {
            MoveCardByButton(sender, -1);
        }

        /// <summary>
        /// 「▼」下移按钮：把本卡片在排序中往后移一位。
        /// </summary>
        /// <param name="sender">被点击的按钮，其 Tag 绑定到对应的科目卡片视图模型</param>
        /// <param name="e">路由事件参数</param>
        /// <remarks>与上移逻辑共用 <see cref="MoveCardByButton"/>，只有方向参数不同。</remarks>
        private void BtnCardDown_Click(object sender, RoutedEventArgs e)
        {
            MoveCardByButton(sender, 1);
        }

        /// <summary>
        /// 按钮排序的公共实现：按给定方向把卡片移动一位。
        /// </summary>
        /// <param name="sender">触发按钮，Tag 上挂着 SubjectCardViewModel</param>
        /// <param name="direction">-1 = 上移（往前），1 = 下移（往后）</param>
        /// <remarks>
        /// 边界处理：已经在首/末位时不做任何变更，只在状态栏告知用户。
        /// 之所以显式提示而不是静默忽略：按钮视觉上无禁用态（卡片模板里没做数据绑定到
        /// 首位/末位判断，为省一次绑定更新与一次计算），
        /// 点不动却毫无反馈会让用户以为程序卡了。
        /// </remarks>
        private void MoveCardByButton(object sender, int direction)
        {
            try
            {
                Button btn = sender as Button;
                if (btn == null) return;

                SubjectCardViewModel card = btn.Tag as SubjectCardViewModel;
                if (card == null) return;

                int from = _viewModel.IndexOfCard(card);
                if (from < 0)
                {
                    ShowToast("找不到这张卡片的信息，请点 ↻ 重新载入");
                    return;
                }

                int to = from + direction;
                int total = _viewModel.AllCards == null ? 0 : _viewModel.AllCards.Count;

                if (to < 0)
                {
                    ShowToast("「" + card.Name + "」已经在最前面了");
                    return;
                }
                if (to >= total)
                {
                    ShowToast("「" + card.Name + "」已经在最后面了");
                    return;
                }

                // MoveCard 内部会：重排内存列表 -> 重编 Order -> 同步 Subjects -> 重建布局 -> 节流保存
                bool ok = _viewModel.MoveCard(from, to);
                if (!ok)
                {
                    ShowToast("调整顺序失败，请稍后重试");
                }
            }
            catch
            {
                ShowToast("调整顺序时遇到小问题");
            }
        }

        // ==================================================================
        // 窗口尺寸持久化（配合 WindowChrome 的系统级缩放热区）
        // ==================================================================

        /// <summary>
        /// 启动尺寸持久化防抖定时器。
        /// </summary>
        /// <remarks>
        /// 每次 SizeChanged 都重置计时；只有连续 600ms 没有新的尺寸变化才真正保存。
        /// 这解决了「拖边框时每帧一次写盘」的浪费，也避免把中间过程的尺寸记下来。
        /// </remarks>
        private void RestartSizeSaveTimer()
        {
            try
            {
                if (_sizeSaveTimer == null)
                {
                    _sizeSaveTimer = new DispatcherTimer(DispatcherPriority.Background);
                    _sizeSaveTimer.Interval = TimeSpan.FromMilliseconds(SizeSaveDebounceMs);
                    _sizeSaveTimer.Tick += OnSizeSaveTick;
                }

                _sizeSaveTimer.Stop();
                _sizeSaveTimer.Start();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 尺寸防抖到点：把当前窗口尺寸写入配置。
        /// </summary>
        /// <param name="sender">计时器</param>
        /// <param name="e">事件参数</param>
        /// <remarks>
        /// 停掉计时器（一次性），再落盘。
        /// 只保存宽高，不保存位置（符合"位置每次回默认位"的需求约定）。
        /// </remarks>
        private void OnSizeSaveTick(object sender, EventArgs e)
        {
            try
            {
                if (_sizeSaveTimer != null) _sizeSaveTimer.Stop();

                double w = this.ActualWidth;
                double h = this.ActualHeight;
                if (double.IsNaN(w) || double.IsNaN(h)) return;
                if (w < 1 || h < 1) return;

                // 与上次已知值比较：尺寸没变就不写盘（例如 DPI 变化触发的 SizeChanged）
                if (Math.Abs(w - _knownSavedWidth) < 1 && Math.Abs(h - _knownSavedHeight) < 1) return;

                _knownSavedWidth = w;
                _knownSavedHeight = h;
                _viewModel.SetManualWindowSize(w, h);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>上一次已写入配置的窗口宽度，用于避免重复保存。</summary>
        private double _knownSavedWidth = 0;

        /// <summary>上一次已写入配置的窗口高度。</summary>
        private double _knownSavedHeight = 0;

        // ==================================================================
        // 分层窗口的 8 向缩放（核心修复）
        // ==================================================================

        /// <summary>
        /// WM_SYSCOMMAND 消息号（0x0112）。窗口缩放/移动等系统命令都走这条消息。
        /// </summary>
        private const int WM_SYSCOMMAND = 0x0112;

        /// <summary>
        /// 触发系统缩放的 SC_SIZE 命令基值（0xF000）+ 方向码，组合成下面 8 个常量。
        /// </summary>
        private const int SC_SIZE = 0xF000;

        /// <summary>Win32 SendMessage：向指定窗口过程发送同步消息。本程序只用于发送 WM_SYSCOMMAND 触发系统缩放。</summary>
        /// <param name="hWnd">目标窗口句柄</param>
        /// <param name="msg">消息号</param>
        /// <param name="wParam">消息参数（这里装 SC_SIZE + 方向码）</param>
        /// <param name="lParam">附加参数（缩放命令恒为 0）</param>
        /// <returns>窗口过程返回值</returns>
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>左上角缩放（SC_SIZE + WMSZ_TOPLEFT）。</summary>
        private static readonly IntPtr SC_SIZE_TOPLEFT = (IntPtr)(SC_SIZE + 4);
        /// <summary>上边缩放（SC_SIZE + WMSZ_TOP）。</summary>
        private static readonly IntPtr SC_SIZE_TOP = (IntPtr)(SC_SIZE + 3);
        /// <summary>右上角缩放（SC_SIZE + WMSZ_TOPRIGHT）。</summary>
        private static readonly IntPtr SC_SIZE_TOPRIGHT = (IntPtr)(SC_SIZE + 5);
        /// <summary>左边缩放（SC_SIZE + WMSZ_LEFT）。</summary>
        private static readonly IntPtr SC_SIZE_LEFT = (IntPtr)(SC_SIZE + 1);
        /// <summary>右边缩放（SC_SIZE + WMSZ_RIGHT）。</summary>
        private static readonly IntPtr SC_SIZE_RIGHT = (IntPtr)(SC_SIZE + 2);
        /// <summary>左下角缩放（SC_SIZE + WMSZ_BOTTOMLEFT）。</summary>
        private static readonly IntPtr SC_SIZE_BOTTOMLEFT = (IntPtr)(SC_SIZE + 7);
        /// <summary>下边缩放（SC_SIZE + WMSZ_BOTTOM）。</summary>
        private static readonly IntPtr SC_SIZE_BOTTOM = (IntPtr)(SC_SIZE + 6);
        /// <summary>右下角缩放（SC_SIZE + WMSZ_BOTTOMRIGHT）。</summary>
        private static readonly IntPtr SC_SIZE_BOTTOMRIGHT = (IntPtr)(SC_SIZE + 8);

        /// <summary>
        /// 窗口四周的缩放热区厚度（像素，DIP）。
        /// </summary>
        /// <remarks>
        /// 取 8：鼠标容易命中，触屏手指也能点得住；
        /// 再大就会侵占内容区点击（标题栏按钮、卡片边缘按钮）了。
        /// </remarks>
        private const double EdgeHitThickness = 8;

        /// <summary>
        /// 在无边框（AllowsTransparency=True）窗口的四周与四角注册 8 个自绘缩放热区。
        /// </summary>
        /// <param name="root">承载热区的根容器，通常是窗口最外层 Grid</param>
        /// <remarks>
        /// 为什么要自己搭热区（而不是靠 WindowChrome / ResizeMode）：
        /// AllowsTransparency=True 在 Win32 层面创建的是分层窗口（WS_EX_LAYERED），
        /// 分层窗口没有非客户区，系统原生的 8 个缩放热区全部不存在，
        /// 因此 ResizeMode 与 WindowChrome.ResizeBorderThickness 都不会生效
        /// （ResizeBorderThickness 只在有 NC 区时才被系统采用）。
        /// 
        /// 热区本身是透明 Border，贴在根 Grid 的上下左右与四角。
        /// 它们不改变视觉，只在 MouseLeftButtonDown 时发送 WM_SYSCOMMAND + SC_SIZE + 方向码，
        /// 该消息会进入 Windows 内建的缩放循环，之后鼠标移动全由系统处理
        /// （即使鼠标移出窗口边界也能继续拖，这正是原生缩放的手感）。
        /// 
        /// 时序要求：必须在窗口的根视觉树布局完成后调用（ctor 里调用即可，
        /// 因为 XAML 的 Grid 已在 InitializeComponent 中构造完成）。
        /// </remarks>
        private void InstallResizeHotspots(Grid root)
        {
            if (root == null) return;

            try
            {
                // ---- 四边（按顺序：左、右、上、下）----
                AddResizeHotspot(root,
                    HorizontalAlignment.Left, VerticalAlignment.Stretch,
                    double.NaN, EdgeHitThickness,
                    0, 0, EdgeHitThickness, EdgeHitThickness,
                    SC_SIZE_LEFT, Cursors.SizeWE);

                // 右边那条要特别处理：内容区的垂直滚动条贴在窗口右缘，
                // 若整条覆盖会把滚动条的拖动全部吃掉。
                // 因此右缘热区只在滚动条以外的区域生效 —— 用一段大 margin
                // 把中间那段"让"给滚动条（滚动条宽 17px + 余量）。
                AddResizeHotspot(root,
                    HorizontalAlignment.Right, VerticalAlignment.Stretch,
                    double.NaN, EdgeHitThickness,
                    EdgeHitThickness, EdgeHitThickness, 0, 0,
                    SC_SIZE_RIGHT, Cursors.SizeWE,
                    true);

                AddResizeHotspot(root,
                    HorizontalAlignment.Stretch, VerticalAlignment.Top,
                    EdgeHitThickness, double.NaN,
                    EdgeHitThickness, EdgeHitThickness, EdgeHitThickness, 0,
                    SC_SIZE_TOP, Cursors.SizeNS);

                AddResizeHotspot(root,
                    HorizontalAlignment.Stretch, VerticalAlignment.Bottom,
                    EdgeHitThickness, double.NaN,
                    EdgeHitThickness, 0, EdgeHitThickness, EdgeHitThickness,
                    SC_SIZE_BOTTOM, Cursors.SizeNS);

                // ---- 四角（宽高各 14，比边厚，便于拖动时不误触到相邻的边）----
                const double corner = 14;

                AddResizeHotspot(root,
                    HorizontalAlignment.Left, VerticalAlignment.Top,
                    corner, corner,
                    0, 0, corner, corner,
                    SC_SIZE_TOPLEFT, Cursors.SizeNWSE);

                AddResizeHotspot(root,
                    HorizontalAlignment.Right, VerticalAlignment.Top,
                    corner, corner,
                    corner, 0, 0, corner,
                    SC_SIZE_TOPRIGHT, Cursors.SizeNESW);

                AddResizeHotspot(root,
                    HorizontalAlignment.Left, VerticalAlignment.Bottom,
                    corner, corner,
                    0, corner, corner, 0,
                    SC_SIZE_BOTTOMLEFT, Cursors.SizeNESW);

                AddResizeHotspot(root,
                    HorizontalAlignment.Right, VerticalAlignment.Bottom,
                    corner, corner,
                    corner, corner, 0, 0,
                    SC_SIZE_BOTTOMRIGHT, Cursors.SizeNWSE);
            }
            catch
            {
                // 热区注册失败不影响主功能，静默忽略
            }
        }

        /// <summary>
        /// 创建一个缩放热区并挂到根容器上。
        /// </summary>
        /// <param name="root">承载热区的根容器</param>
        /// <param name="halign">水平对齐方式（Left / Stretch / Right）</param>
        /// <param name="valign">垂直对齐方式（Top / Stretch / Bottom）</param>
        /// <param name="width">宽度；double.NaN 表示不固定宽（由对齐方式 + Margin 决定）</param>
        /// <param name="height">高度；double.NaN 表示不固定高</param>
        /// <param name="marginLeft">左外边距，用来把"贴边"的细条撑成"环状"结构</param>
        /// <param name="marginTop">上外边距</param>
        /// <param name="marginRight">右外边距</param>
        /// <param name="marginBottom">下外边距</param>
        /// <param name="command">按下时要发送的 SC_SIZE 方向命令</param>
        /// <param name="cursor">悬停时的鼠标指针形状</param>
        /// <param name="leaveMiddleGap">
        /// 是否在整条热区中间挖空一段（给垂直滚动条让路）。
        /// 仅右边缘需要：内容区滚动条贴在窗口右缘，整条覆盖会导致滚动条拖不动。
        /// </param>
        /// <remarks>
        /// 边条通过「拉伸方向 + Margin 让位」实现：
        /// 例如左右两条是 Stretch 竖条，分别用左/右 margin 让出上下角的位置，
        /// 这样四角的热区就不会被边的热区盖住（后添加的角在 Z 序上层，优先命中）。
        /// 
        /// 事件用 PreviewMouseLeftButtonDown（隧道）而非 MouseLeftButtonDown（冒泡）：
        /// 隧道在到达任何子元素之前触发，不会被内容区的控件抢先处理。
        /// </remarks>
        private void AddResizeHotspot(Grid root,
                                      HorizontalAlignment halign,
                                      VerticalAlignment valign,
                                      double width,
                                      double height,
                                      double marginLeft,
                                      double marginTop,
                                      double marginRight,
                                      double marginBottom,
                                      IntPtr command,
                                      Cursor cursor,
                                      bool leaveMiddleGap = false)
        {
            try
            {
                // 中间挖空：把整条拆成上、下两段，各占窗口高度的 38%，
                // 中间约 24% 完全不设热区，让内容区的垂直滚动条能正常拖动。
                if (leaveMiddleGap && valign == VerticalAlignment.Stretch)
                {
                    Border upper = CreateHotspotBorder(halign, VerticalAlignment.Top,
                        width, marginLeft, marginTop, marginRight, 0, cursor);
                    BindRatioHeight(upper, root, 0.38);

                    Border lower = CreateHotspotBorder(halign, VerticalAlignment.Bottom,
                        width, marginLeft, 0, marginRight, marginBottom, cursor);
                    BindRatioHeight(lower, root, 0.38);

                    AttachResizeHandlers(upper, command);
                    AttachResizeHandlers(lower, command);

                    root.Children.Add(upper);
                    root.Children.Add(lower);
                    return;
                }

                Border hotspot = CreateHotspotBorder(halign, valign,
                    width, marginLeft, marginTop, marginRight, marginBottom, cursor);
                if (!double.IsNaN(height)) hotspot.Height = height;

                AttachResizeHandlers(hotspot, command);

                root.Children.Add(hotspot);
            }
            catch
            {
                // 忽略单个热区的注册失败
            }
        }

        /// <summary>
        /// 创建一个透明的热区 Border（只设外观与对齐，不挂事件）。
        /// </summary>
        /// <param name="halign">水平对齐方式</param>
        /// <param name="valign">垂直对齐方式</param>
        /// <param name="width">固定宽度；double.NaN 表示不固定</param>
        /// <param name="marginLeft">左外边距</param>
        /// <param name="marginTop">上外边距</param>
        /// <param name="marginRight">右外边距</param>
        /// <param name="marginBottom">下外边距</param>
        /// <param name="cursor">悬停指针形状</param>
        /// <returns>构造好的透明 Border</returns>
        /// <remarks>Background 必须显式设为 Transparent，否则空 Border 不参与命中测试。</remarks>
        private Border CreateHotspotBorder(HorizontalAlignment halign,
                                           VerticalAlignment valign,
                                           double width,
                                           double marginLeft,
                                           double marginTop,
                                           double marginRight,
                                           double marginBottom,
                                           Cursor cursor)
        {
            Border hotspot = new Border();
            hotspot.Background = Brushes.Transparent;   // 透明但可命中
            hotspot.HorizontalAlignment = halign;
            hotspot.VerticalAlignment = valign;
            hotspot.Margin = new Thickness(marginLeft, marginTop, marginRight, marginBottom);
            if (!double.IsNaN(width)) hotspot.Width = width;
            hotspot.Cursor = cursor;

            // ★ 必须跨满全部 Grid 行。
            // RootLayout 是 4 行 Grid（标题栏 Auto / 工具条 Auto / 内容 * / 状态栏 Auto）。
            // 热区若不设 Row 与 RowSpan，默认落在 Row 0 且只占 1 行，
            // 会参与 Row 0 的 Auto 高度计算 —— 表现为标题栏被撑到 180+ 像素
            // （子元素实测仅 36.8px，Grid 却 186.4px，多出来的全是被热区撑的）。
            // 跨满所有行后，热区只作为最上层覆盖物存在，不再影响任何行的高度。
            Grid.SetRow(hotspot, 0);
            Grid.SetRowSpan(hotspot, 4);

            return hotspot;
        }

        /// <summary>
        /// 把热区高度绑定为根容器实际高度的指定比例。
        /// </summary>
        /// <param name="target">目标热区</param>
        /// <param name="root">根容器（提供 ActualHeight）</param>
        /// <param name="ratio">比例，例如 0.38</param>
        /// <remarks>WPF 没有内置的百分比尺寸，故用绑定 + <see cref="RatioConverter"/> 实现。</remarks>
        private void BindRatioHeight(FrameworkElement target, FrameworkElement root, double ratio)
        {
            System.Windows.Data.Binding binding = new System.Windows.Data.Binding("ActualHeight");
            binding.Source = root;
            binding.Converter = new RatioConverter(ratio);
            target.SetBinding(FrameworkElement.HeightProperty, binding);
        }

        /// <summary>
        /// 给热区挂上鼠标/触摸的按下事件，按下即进入系统缩放。
        /// </summary>
        /// <param name="hotspot">热区</param>
        /// <param name="command">SC_SIZE + 方向码</param>
        /// <remarks>
        /// 用 PreviewMouseLeftButtonDown（隧道）而非冒泡版：
        /// 隧道在到达任何子元素之前触发，不会被内容区控件抢先处理。
        /// </remarks>
        private void AttachResizeHandlers(Border hotspot, IntPtr command)
        {
            hotspot.PreviewMouseLeftButtonDown += (s, e) =>
            {
                try
                {
                    BeginSystemResize(s as Visual, command);
                    e.Handled = true;
                }
                catch
                {
                    // 忽略
                }
            };

            // 触摸路径：手指按下也走同一套系统缩放
            hotspot.TouchDown += (s, e) =>
            {
                try
                {
                    BeginSystemResize(s as Visual, command);
                    e.Handled = true;
                }
                catch
                {
                    // 忽略
                }
            };
        }

        /// <summary>
        /// 向系统发送 SC_SIZE 命令，进入 Windows 内建的窗口缩放循环。
        /// </summary>
        /// <param name="source">用于取窗口句柄的依赖对象（这里是热区自身）</param>
        /// <param name="command">SC_SIZE + 方向码</param>
        /// <remarks>
        /// 这条路径完全绕开 WPF 的布局系统：系统接管鼠标捕获后，
        /// 按用户拖动量直接改窗口的 Win32 矩形，因此分层窗口也能正常改宽高。
        /// 之前自绘握把直接改 Width 之所以失效，是因为分层窗口的尺寸变更
        /// 需要由系统消息驱动渲染管线重排，程序内赋值容易被吞掉。
        /// 
        /// 注意：系统缩放循环会自动遵守 MinWidth/MinHeight/MaxWidth/MaxHeight，
        /// 因此不需要在这里做边界钳制。
        /// </remarks>
        private void BeginSystemResize(Visual source, IntPtr command)
        {
            try
            {
                HwndSource hwndSource = null;

                if (source != null)
                {
                    hwndSource = PresentationSource.FromVisual(source) as HwndSource;
                }

                if (hwndSource == null)
                {
                    // 兜底：直接从窗口取句柄
                    IntPtr handle = new WindowInteropHelper(this).Handle;
                    if (handle == IntPtr.Zero) return;
                    SendMessage(handle, WM_SYSCOMMAND, command, IntPtr.Zero);
                    return;
                }

                SendMessage(hwndSource.Handle, WM_SYSCOMMAND, command, IntPtr.Zero);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 判断元素是否为可交互控件（按钮、输入框、复选框等）。
        /// </summary>
        /// <param name="element">起始元素，可能为 null</param>
        /// <returns>true = 属于可交互控件，不应触发窗口拖拽</returns>
        /// <remarks>
        /// 沿视觉树向上查找，遇到 ButtonBase / TextBoxBase / CheckBox / Slider 等就判定为交互元素。
        /// 触摸场景下这一点尤其重要——手指精度低，很容易按到按钮边缘，
        /// 若不排除，用户想点「✕」删除却把整个窗口拖走了。
        /// </remarks>
        private static bool IsInteractiveElement(DependencyObject element)
        {
            try
            {
                DependencyObject cur = element;
                int guard = 0;
                while (cur != null && guard < 40)
                {
                    guard++;
                    if (cur is Button || cur is System.Windows.Controls.Primitives.ButtonBase) return true;
                    if (cur is TextBox || cur is System.Windows.Controls.Primitives.TextBoxBase) return true;
                    if (cur is CheckBox) return true;
                    if (cur is System.Windows.Controls.Primitives.ScrollBar) return true;
                    if (cur is System.Windows.Controls.Primitives.Thumb) return true;
                    if (cur is Slider) return true;
                    if (cur is ScrollViewer) return true;
                    cur = VisualTreeHelper.GetParent(cur);
                }
            }
            catch
            {
                // 忽略
            }
            return false;
        }

        /// <summary>
        /// 窗口位置变化：只在窗口完全离开可见区域时纠偏。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>
        /// 刻意不做「每次移动都夹紧」，否则用户拖到屏幕边缘时会被弹回来，手感很糟。
        /// </remarks>
        private void OnLocationChanged(object sender, EventArgs e)
        {
            if (_isDragging) return; // 拖拽中不干预

            try
            {
                // 只有完全不可见时才强制拉回（例如分辨率变小、显示器被拔掉）
                ScreenHelper.EnsureVisible(this);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 窗口被拖到不同 DPI 的显示器上时的回调。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">DPI 变化事件参数</param>
        /// <remarks>
        /// 需求：「多显示器环境下窗口不模糊」。
        /// PerMonitorV2 模式下 WPF 会自动重排，这里只需做一次越界检查即可。
        /// </remarks>
        private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
        {
            try
            {
                // DPI 变了，工作区高度对应的工作高度上限也要跟着重算
                _viewModel.SetDpiScale(GetCurrentDpiScale());
                this.UpdateLayout();
                ScreenHelper.ClampIntoWorkArea(this);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 应用置顶状态到窗口。
        /// </summary>
        /// <remarks>
        /// 同时更新标题栏按钮的高亮，让用户一眼看出当前是否置顶。
        /// </remarks>
        private void ApplyTopmostState()
        {
            try
            {
                bool top = _viewModel.WindowSettings.Topmost;
                this.Topmost = top;

                if (BtnTopmost != null)
                {
                    BtnTopmost.Foreground = top
                        ? (Brush)FindResource("BrushAccent")
                        : (Brush)FindResource("BrushTextTertiary");
                    BtnTopmost.ToolTip = top ? "当前：置顶（点击取消）" : "当前：不置顶（点击开启）";
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 置顶按钮点击：切换置顶状态并保存。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void BtnTopmost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool newState = !_viewModel.WindowSettings.Topmost;
                _viewModel.SetTopmost(newState);
                ApplyTopmostState();
                ShowToast(newState ? "已置顶" : "已取消置顶");
            }
            catch (Exception ex)
            {
                ShowToast("切换置顶失败：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 设置按钮点击：打开设置面板。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SettingsWindow(_viewModel);
                dlg.Owner = this;
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                ShowToast("打开设置失败：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 最小化按钮点击。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.WindowState = WindowState.Minimized;
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 关闭按钮点击。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.Close();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 「重新载入」按钮点击：从磁盘重读 JSON。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>用途：同学用记事本改完配置后无需重启程序。</remarks>
        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.ReloadFromDisk();
                ApplyTopmostState();
                ShowToast(_viewModel.StatusText);
            }
            catch (Exception ex)
            {
                ShowToast("重新载入失败：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 「立即保存」按钮点击。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void BtnSaveNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.SaveAllNow();
                ShowToast(_viewModel.StatusText);
            }
            catch (Exception ex)
            {
                ShowToast("保存失败：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 「打开文件夹」按钮点击：在资源管理器中定位到程序目录。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>
        /// 使用 explorer.exe /select 打开并选中 settings.json，方便同学直接编辑。
        /// 失败时退化为打开目录本身。不抛异常。
        /// </remarks>
        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = _store.DataDirectory;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    ShowToast("无法定位程序目录");
                    return;
                }

                string settingsFile = _store.SettingsPath;
                if (File.Exists(settingsFile))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "/select,\"" + settingsFile + "\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "\"" + dir + "\"",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                ShowToast("打开文件夹失败：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 科目卡片上的「＋」按钮：为本科目新增一条作业。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>
        /// Tag 上绑定了 SubjectCardViewModel，从中取科目键名。
        /// 打开编辑对话框，对话框里提供模板快选。
        /// </remarks>
        private void BtnAddItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var btn = sender as Button;
                if (btn == null) return;
                var card = btn.Tag as SubjectCardViewModel;
                if (card == null) return;

                var dlg = new EditItemWindow(_viewModel, card.Name, null);
                dlg.Owner = this;
                bool? ok = dlg.ShowDialog();
                if (ok == true)
                {
                    _viewModel.AddItem(card.Key, dlg.ResultContent);
                    ShowToast("已添加「" + card.Name + "」作业");
                }
            }
            catch (Exception ex)
            {
                ShowToast("添加失败：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 条目的「✎」按钮：编辑这条作业。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void BtnEditItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var btn = sender as Button;
                if (btn == null) return;
                var item = btn.Tag as HomeworkItemViewModel;
                if (item == null) return;

                SubjectCardViewModel card = null;
                foreach (SubjectCardViewModel c in _viewModel.AllCards)
                {
                    if (c.Key == item.SubjectKey) { card = c; break; }
                }
                string subjectName = card != null ? card.Name : item.SubjectKey;

                var dlg = new EditItemWindow(_viewModel, subjectName, item.Content);
                dlg.Owner = this;
                bool? ok = dlg.ShowDialog();
                if (ok == true)
                {
                    _viewModel.UpdateItemContent(item, dlg.ResultContent);
                    ShowToast("已保存修改");
                }
            }
            catch (Exception ex)
            {
                ShowToast("编辑失败：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 条目的「✕」按钮：删除这条作业。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>
        /// 删除前弹一次确认——这是唯一不可撤销的操作，需要防误触。
        /// </remarks>
        private void BtnDeleteItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var btn = sender as Button;
                if (btn == null) return;
                var item = btn.Tag as HomeworkItemViewModel;
                if (item == null) return;

                MessageBoxResult r = MessageBox.Show(
                    this,
                    "确定删除这条作业吗？\n\n" + item.DisplayContent,
                    "删除确认",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (r == MessageBoxResult.OK)
                {
                    _viewModel.RemoveItem(item);
                    ShowToast("已删除");
                }
            }
            catch (Exception ex)
            {
                ShowToast("删除失败：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 点击作业文字区域：切换完成状态（鼠标路径）。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">鼠标事件参数</param>
        /// <remarks>便捷操作：不用精确点中小勾选框。</remarks>
        private void ItemTextArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var border = sender as FrameworkElement;
                if (border == null) return;
                var item = border.DataContext as HomeworkItemViewModel;
                if (item == null) return;

                _viewModel.ToggleItemDone(item);
                e.Handled = true; // 阻断冒泡，避免被上层再次处理导致切换两次
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 轻触作业文字区域：切换完成状态（触摸路径）。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">触摸事件参数</param>
        /// <remarks>
        /// 触摸屏专用。必须阻断鼠标事件提升，否则同一次手指点击会走两遍
        /// （触摸一遍 + 提升出的鼠标一遍），表现为「点一下勾上又取消」。
        /// </remarks>
        private void ItemTextArea_TouchUp(object sender, TouchEventArgs e)
        {
            try
            {
                var border = sender as FrameworkElement;
                if (border == null) return;
                var item = border.DataContext as HomeworkItemViewModel;
                if (item == null) return;

                _viewModel.ToggleItemDone(item);
                e.Handled = true; // 阻断鼠标事件提升，避免重复触发
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 勾选框状态变化：刷新完成数统计。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>
        /// Done 属性已经双向绑定到底层模型，这里只需要请求一次保存并刷新计数。
        /// </remarks>
        private void ItemCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.RequestSave();
                _viewModel.RefreshCounters();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 显示一条温和提示（状态栏 + 自动恢复）。
        /// </summary>
        /// <param name="message">提示文本，可为空</param>
        /// <remarks>
        /// 需求：「给出温和提示，例如状态栏、气泡或非模态提示，不弹出错误堆栈」。
        /// 实现：把文本写进状态栏，5 秒后恢复为原文本。不使用动画。
        /// </remarks>
        public void ShowToast(string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message)) return;

                if (string.IsNullOrEmpty(_lastStatusText))
                {
                    _lastStatusText = _viewModel.StatusText;
                }

                _viewModel.StatusText = message;

                if (_toastTimer == null)
                {
                    _toastTimer = new DispatcherTimer(DispatcherPriority.Background);
                    _toastTimer.Interval = TimeSpan.FromSeconds(5);
                    _toastTimer.Tick += OnToastTick;
                }
                _toastTimer.Stop();
                _toastTimer.Start();
            }
            catch
            {
                // 提示失败不影响主流程
            }
        }

        /// <summary>
        /// toast 定时器回调：恢复状态栏原文。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void OnToastTick(object sender, EventArgs e)
        {
            try
            {
                if (_toastTimer != null) _toastTimer.Stop();
                if (!string.IsNullOrEmpty(_lastStatusText))
                {
                    _viewModel.StatusText = _lastStatusText;
                    _lastStatusText = string.Empty;
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 强制立即落盘（供 App 在发生严重异常时调用）。
        /// </summary>
        /// <remarks>不抛异常。</remarks>
        public void ForceFlush()
        {
            try
            {
                if (_viewModel != null)
                {
                    _viewModel.SaveAllNow();
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 窗口关闭中：停定时器、落盘。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">取消事件参数</param>
        /// <remarks>
        /// 需求：「变更时自动保存」。用户可能改完立刻点关闭，
        /// 因此这里必须同步 Flush，保证节流窗口内的修改也落盘。
        /// 任何异常都不阻止关闭——程序必须能正常退出。
        /// </remarks>
        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                // 先摘掉尺寸请求订阅，避免关闭过程中设置窗口还活着时收到回调。
                if (_viewModel != null)
                {
                    try { _viewModel.WindowSizeRequested -= OnWindowSizeRequested; }
                    catch { }
                }

                if (_rolloverTimer != null)
                {
                    _rolloverTimer.Stop();
                    _rolloverTimer.Tick -= OnRolloverTick;
                    _rolloverTimer = null;
                }

                if (_toastTimer != null)
                {
                    _toastTimer.Stop();
                    _toastTimer.Tick -= OnToastTick;
                    _toastTimer = null;
                }

                // 尺寸防抖定时器可能在关闭前刚被触发（用户拖完边框马上点关闭）。
                // 必须先停掉，再把当前尺寸同步落盘一次——否则"拖完立刻关"
                // 会丢掉最后这次尺寸调整（防抖还没到点就被 Dispose 了）。
                if (_sizeSaveTimer != null)
                {
                    _sizeSaveTimer.Stop();
                    _sizeSaveTimer.Tick -= OnSizeSaveTick;
                    _sizeSaveTimer = null;
                }

                if (_viewModel != null)
                {
                    try
                    {
                        double w = this.ActualWidth;
                        double h = this.ActualHeight;
                        if (!double.IsNaN(w) && !double.IsNaN(h) && w > 1 && h > 1)
                        {
                            _viewModel.SetManualWindowSize(w, h);
                        }
                    }
                    catch
                    {
                        // 尺寸保存失败不影响关闭
                    }
                }

                EndDrag();
                EndTouchDrag();

                if (_viewModel != null)
                {
                    _viewModel.Shutdown();
                }
            }
            catch
            {
                // 关闭过程中的异常不应阻止退出
            }
        }
    }
}
