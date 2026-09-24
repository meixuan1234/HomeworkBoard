using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HomeworkBoard.Core;
using HomeworkBoard.Models;

namespace HomeworkBoard.ViewModels
{
    /// <summary>
    /// 主视图模型：承载全部业务状态与操作。
    /// 
    /// 职责边界：
    /// - 持有 settings / homework 数据；
    /// - 提供科目卡片的增删改；
    /// - 计算多列布局（居右放不下就往左另起一列）；
    /// - 触发节流保存。
    /// 
    /// 不负责：窗口移动、DPI 换算、Win32 调用（这些在 MainWindow 与 ScreenHelper 中）。
    /// </summary>
    public class MainViewModel : ObservableObject
    {
        /// <summary>JSON 存储管理器。之所以不是 readonly，是因为 ReloadFromDisk 会重建它。</summary>
        private JsonStore _store;
        private readonly ThrottledSaver _saver;

        private AppSettings _settings;
        private HomeworkStore _homework;

        private DateTime _currentDate;
        private string _statusText;
        private int _columnCount;

        /// <summary>
        /// 当前显示器 DPI 缩放比例的缓存（1.0 = 100%）。
        /// 由 MainWindow 在定位与 DPI 变化时通过 SetDpiScale 写入。
        /// 用途：把 Win32 返回的物理像素工作区高度换算成 WPF 逻辑像素，
        /// 从而正确计算 MaxWindowHeight 的硬上限（否则高 DPI 下会算小一半）。
        /// </summary>
        private double _dpiScaleCache = 1.0;

        /// <summary>
        /// 窗口内容区的最近一次实测宽度（逻辑像素）。
        /// 由 MainWindow 在 ContentRendered 与 SizeChanged 时通过 SetLayoutWidth 写入。
        /// 用途：分行算法需要知道「一行能放几张卡片」，而窗口现在可手动缩放（第 8 轮需求），
        /// 宽度不再是固定值，必须在运行时跟随。
        /// 初值 0 表示尚未测量，此时 RefreshLayout 会退回 DefaultWindowWidth 估算。
        /// </summary>
        private double _layoutWindowWidth = 0;

        /// <summary>窗口默认宽度（逻辑像素），与 MainWindow.xaml 的 Width 保持一致。</summary>
        private const double DefaultWindowWidth = 380;

        /// <summary>窗口默认高度（逻辑像素），与 MainWindow.xaml 的 Height 保持一致。</summary>
        private const double DefaultWindowHeight = 720;

        /// <summary>窗口最小宽度（逻辑像素），与 MainWindow.xaml 的 MinWidth 保持一致。</summary>
        private const double MinWindowWidth = 280;

        /// <summary>窗口最小高度（逻辑像素），与 MainWindow.xaml 的 MinHeight 保持一致。</summary>
        private const double MinWindowHeight = 180;

        /// <summary>当前显示的日期（只取日期部分，时分秒恒为 0）。</summary>
        public DateTime CurrentDate
        {
            get { return _currentDate; }
            private set
            {
                if (SetField(ref _currentDate, value))
                {
                    OnPropertyChanged("CurrentDateText");
                    OnPropertyChanged("CurrentWeekdayText");
                }
            }
        }

        /// <summary>界面标题上显示的日期文本，如 "2026年9月18日 星期五"。</summary>
        public string CurrentDateText
        {
            get
            {
                string[] weekdays = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
                return _currentDate.ToString("yyyy年M月d日") + " " + weekdays[(int)_currentDate.DayOfWeek];
            }
        }

        /// <summary>状态栏文本，用于展示保存结果、容错提示等。</summary>
        public string StatusText
        {
            get { return _statusText; }
            set { SetField(ref _statusText, value); }
        }

        /// <summary>当前列数（由布局算法算出）。</summary>
        public int ColumnCount
        {
            get { return _columnCount; }
            private set { SetField(ref _columnCount, value); }
        }

        /// <summary>所有可见的科目卡片（按 order 排序后）。</summary>
        public List<SubjectCardViewModel> AllCards { get; private set; }

        /// <summary>当前是否处于「目录不可写」的降级模式。</summary>
        public bool IsReadOnlyMode { get { return !_store.IsWritable; } }

        /// <summary>窗口设置（透明度、置顶、字号等）的快捷访问。</summary>
        public WindowSettings WindowSettings { get { return _settings.Window; } }

        /// <summary>
        /// 窗口不透明度（0.0—1.0），供 XAML 直接绑定。
        /// </summary>
        /// <remarks>
        /// 单独暴露一个 double 属性，而不是让 XAML 绑定 WindowSettings.OpacityPercent 再做换算，
        /// 因为 WPF 没有内置的「百分比 -> 小数」转换，写个转换器不如直接算好。
        /// </remarks>
        public double OpacityValue
        {
            get
            {
                int p = _settings.Window.OpacityPercent;
                if (p < 70) p = 70;
                if (p > 95) p = 95;
                return p / 100.0;
            }
        }

        /// <summary>正文字号，供 XAML 绑定卡片内作业条目的字号。</summary>
        public double FontSize { get { return _settings.Window.FontSize; } }

        /// <summary>标题字号，供 XAML 绑定科目名与卡片标题的字号。</summary>
        public double TitleFontSize { get { return _settings.Window.TitleFontSize; } }

        /// <summary>
        /// 空科目占位文字「暂无作业 · 点 ＋ 添加」的字号。
        /// </summary>
        /// <remarks>
        /// 取正文字号的 80%，并夹在 12—24 之间。
        /// 上限 24 是为了防止用户把正文调到 48 时占位文字也跟着巨大，
        /// 那样空卡片就不"折叠"了，反而比有作业的卡片还占地方。
        /// </remarks>
        public double EmptyHintFontSize
        {
            get
            {
                double size = _settings.Window.FontSize * 0.8;
                if (size < 12) size = 12;
                if (size > 24) size = 24;
                return Math.Round(size);
            }
        }

        /// <summary>
        /// 空科目占位文字的行高。
        /// </summary>
        /// <remarks>
        /// 显式设行高（而非用默认行距）是为了让空卡片高度稳定可控：
        /// 默认行距在中文与数字混排时偏大，会让占位行比预期高一截。
        /// </remarks>
        public double EmptyHintLineHeight
        {
            get { return Math.Round(EmptyHintFontSize * 1.35); }
        }

        /// <summary>列宽，供 XAML 控制单列宽度。</summary>
        public double ColumnWidth { get { return _settings.Window.ColumnWidth; } }

        /// <summary>
        /// 单个科目卡片的实际渲染宽度（逻辑像素）。
        /// </summary>
        /// <remarks>
        /// 与 ColumnWidth 的关系：ColumnWidth 是用户配置的「卡片内容宽度」，
        /// 卡片左右各有 10 像素外间距（Margin="0,0,10,10"，用于卡片之间的透气），
        /// 因此卡片总宽必须额外加 10，否则实际占位会比配置值大，
        /// 分行计算出来的「一行放几个」就会多算一个，导致最后一张卡片被挤出屏幕。
        /// 
        /// 历史：早期多列布局时这个间距在外层列容器上，卡片自身没有左右 Margin，
        /// 所以直接用 ColumnWidth 即可；改成横向网格后间距移到卡片上，必须在这里补回来。
        /// </remarks>
        public double CardWidth
        {
            get
            {
                double w = _settings.Window.ColumnWidth;
                if (w < 200) w = 200;
                if (w > 800) w = 800;
                return w;
            }
        }

        /// <summary>
        /// 一行卡片占用的总宽度（卡片宽 + 卡片右侧间距）。
        /// </summary>
        /// <remarks>分行算法用，避免在多处重复写 +10。</remarks>
        public double CardSlotWidth { get { return CardWidth + 10; } }

        /// <summary>
        /// 窗口最大高度，供 XAML 绑定。
        /// </summary>
        /// <remarks>
        /// 内容超出时由 ScrollViewer 滚动，保证窗口不会长到超出屏幕高度。
        /// 
        /// 除配置里的 MaxHeight 外，还会与**当前显示器工作区高度**取小值：
        /// 配置值是用户偏好，工作区高度是硬约束。这样在小屏（如 768p 一体机）上
        /// 即使配置写了 720，也不会把窗口撑到任务栏下面去。
        /// 预留高度 = 上边距(edgeMargin) + 下边距(edgeMargin) + 少数余量。
        /// </remarks>
        public double MaxWindowHeight
        {
            get
            {
                double configured = _settings.Window.MaxHeight;

                // 工作区高度（物理像素）换算到逻辑像素后作为硬上限
                try
                {
                    ScreenHelper.WorkArea wa = ScreenHelper.GetPrimaryWorkArea();
                    double scale = _dpiScaleCache;
                    if (scale <= 0.01) scale = 1.0;
                    double workHeightDip = wa.Height / scale;

                    // 上下各留 edgeMargin（至少 20），保证窗口不贴死屏幕边缘
                    double margin = _settings.Window.EdgeMargin;
                    if (margin < 20) margin = 20;
                    double hardLimit = workHeightDip - (margin * 2);

                    if (hardLimit < 300) hardLimit = 300; // 极端小屏兜底
                    if (configured > hardLimit) return hardLimit;
                }
                catch
                {
                    // 取不到屏幕信息就只用配置值，绝不因此让界面异常
                }

                return configured;
            }
        }

        /// <summary>
        /// 窗口启动时的宽度（逻辑像素）。
        /// </summary>
        /// <remarks>
        /// 取值优先级：用户上次手动调整的宽度 -> 默认 380。
        /// 会与显示器工作区宽度取小值并夹到 MinWidth—1600，
        /// 防止换到小屏（如 1024x768 一体机）后窗口宽过屏幕。
        /// </remarks>
        public double InitialWindowWidth
        {
            get
            {
                double w = _settings.Window.ManualWidth > 0
                    ? _settings.Window.ManualWidth
                    : DefaultWindowWidth;

                // 工作区宽度硬约束：物理像素换算到逻辑像素
                try
                {
                    ScreenHelper.WorkArea wa = ScreenHelper.GetPrimaryWorkArea();
                    double scale = _dpiScaleCache;
                    if (scale <= 0.01) scale = 1.0;
                    double workWidthDip = wa.Width / scale;

                    double margin = _settings.Window.EdgeMargin;
                    if (margin < 20) margin = 20;
                    double hardLimit = workWidthDip - (margin * 2);
                    if (hardLimit < MinWindowWidth) hardLimit = MinWindowWidth;
                    if (w > hardLimit) w = hardLimit;
                }
                catch
                {
                    // 取不到屏幕信息就只用配置值
                }

                if (w < MinWindowWidth) w = MinWindowWidth;
                if (w > 1600) w = 1600;
                return Math.Round(w);
            }
        }

        /// <summary>
        /// 窗口启动时的高度（逻辑像素）。
        /// </summary>
        /// <remarks>
        /// 取值优先级：用户上次手动调整的高度 -> 默认 720。
        /// 最终仍与 MaxWindowHeight 取小值（即与显示器工作区高度取小值），
        /// 保证小屏上不会把窗口撑到任务栏下面去。
        /// </remarks>
        public double InitialWindowHeight
        {
            get
            {
                double h = _settings.Window.ManualHeight > 0
                    ? _settings.Window.ManualHeight
                    : DefaultWindowHeight;

                double cap = MaxWindowHeight;
                if (h > cap) h = cap;
                if (h < MinWindowHeight) h = MinWindowHeight;
                return Math.Round(h);
            }
        }

        /// <summary>
        /// 记住用户手动调整后的窗口尺寸。
        /// </summary>
        /// <param name="width">窗口宽度（逻辑像素）</param>
        /// <param name="height">窗口高度（逻辑像素）</param>
        /// <remarks>
        /// 只在「用户确实改变了尺寸」时才写入，由 MainWindow 在缩放热区拖动结束时调用，
        /// 不在 SizeChanged 里调用——那会把程序自身的布局调整（如换行导致的高度变化，
        /// 如果将来启用 SizeToContent）也当成用户意图记录下来。
        /// 
        /// 位置按需求不保存，但尺寸保存：尺寸是阅读偏好，位置是每次都回默认位。
        /// </remarks>
        public void SetManualWindowSize(double width, double height)
        {
            try
            {
                int w = (int)Math.Round(width);
                int h = (int)Math.Round(height);

                if (w < MinWindowWidth) w = (int)MinWindowWidth;
                if (w > 1600) w = 1600;
                if (h < MinWindowHeight) h = (int)MinWindowHeight;
                if (h > 1400) h = 1400;

                if (_settings.Window.ManualWidth == w && _settings.Window.ManualHeight == h) return;

                _settings.Window.ManualWidth = w;
                _settings.Window.ManualHeight = h;
                OnPropertyChanged("WindowSettings");
                _saver.RequestSave();
            }
            catch
            {
                // 忽略：尺寸记录失败不影响使用
            }
        }

        /// <summary>
        /// 把给定尺寸按有效范围夹紧，返回真正可用的宽高。
        /// </summary>
        /// <param name="width">期望宽度（逻辑像素）</param>
        /// <param name="height">期望高度（逻辑像素）</param>
        /// <param name="clampedWidth">输出：夹紧后的宽度</param>
        /// <param name="clampedHeight">输出：夹紧后的高度</param>
        /// <remarks>
        /// 供设置窗口的宽/高滑块使用。夹紧规则与启动时的 InitialWindowWidth / InitialWindowHeight
        /// 完全一致，避免"设置里调的值"和"重启后实际得到的值"不一致。
        /// 
        /// 三条约束：
        /// ① 下限 MinWindowWidth / MinWindowHeight；
        /// ② 宽度不超过「工作区宽度 − 左右各一个 EdgeMargin」（换到小屏也不会宽过屏幕）；
        /// ③ 高度不超过 MaxWindowHeight（它本身已与显示器工作区高度取过小值）。
        /// </remarks>
        public void ClampWindowSize(double width, double height,
                                    out double clampedWidth, out double clampedHeight)
        {
            double w = width;
            double h = height;

            // 宽度：先按工作区宽度限，再夹到绝对区间
            try
            {
                ScreenHelper.WorkArea wa = ScreenHelper.GetPrimaryWorkArea();
                double scale = _dpiScaleCache;
                if (scale <= 0.01) scale = 1.0;
                double workWidthDip = wa.Width / scale;

                double margin = _settings.Window.EdgeMargin;
                if (margin < 20) margin = 20;
                double hardLimit = workWidthDip - (margin * 2);
                if (hardLimit < MinWindowWidth) hardLimit = MinWindowWidth;
                if (w > hardLimit) w = hardLimit;
            }
            catch
            {
                // 取不到屏幕信息就只用绝对区间
            }

            if (w < MinWindowWidth) w = MinWindowWidth;
            if (w > 1600) w = 1600;

            // 高度：与 MaxWindowHeight 取小值
            double cap = MaxWindowHeight;
            if (h > cap) h = cap;
            if (h < MinWindowHeight) h = MinWindowHeight;

            clampedWidth = Math.Round(w);
            clampedHeight = Math.Round(h);
        }

        /// <summary>
        /// 设置窗口宽度的可调上限（逻辑像素）。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="ClampWindowSize"/> 的宽度约束保持一致，
        /// 供设置窗口给滑块设 Maximum，避免用户拖到超出屏幕的值上。
        /// </remarks>
        public double MaxWindowWidth
        {
            get
            {
                try
                {
                    ScreenHelper.WorkArea wa = ScreenHelper.GetPrimaryWorkArea();
                    double scale = _dpiScaleCache;
                    if (scale <= 0.01) scale = 1.0;
                    double workWidthDip = wa.Width / scale;

                    double margin = _settings.Window.EdgeMargin;
                    if (margin < 20) margin = 20;
                    double hardLimit = workWidthDip - (margin * 2);
                    if (hardLimit < MinWindowWidth) hardLimit = MinWindowWidth;
                    if (hardLimit > 1600) hardLimit = 1600;
                    return Math.Round(hardLimit);
                }
                catch
                {
                    return 1600;
                }
            }
        }

        /// <summary>窗口宽度下限（逻辑像素），供设置窗口的滑块使用。</summary>
        public double MinWindowWidthValue { get { return MinWindowWidth; } }

        /// <summary>窗口高度下限（逻辑像素），供设置窗口的滑块使用。</summary>
        public double MinWindowHeightValue { get { return MinWindowHeight; } }

        /// <summary>窗口高度上限（逻辑像素），供设置窗口的滑块使用。</summary>
        public double MaxWindowHeightValue { get { return MaxWindowHeight; } }

        /// <summary>
        /// 请求把主窗口尺寸改成指定值（由设置窗口的宽/高滑块触发）。
        /// </summary>
        /// <param name="width">期望宽度（逻辑像素）</param>
        /// <param name="height">期望高度（逻辑像素）</param>
        /// <remarks>
        /// 视图模型不直接持有 Window 引用（保持可测试性），
        /// 因此这里只夹紧并保存配置，实际改窗口由 MainWindow 订阅
        /// <see cref="WindowSizeRequested"/> 完成。
        /// 
        /// 传 0 或负数表示"该维度不变"，用于只调宽度或只调高度的场景。
        /// </remarks>
        public void RequestWindowSize(double width, double height)
        {
            try
            {
                double curW = _settings.Window.ManualWidth > 0
                    ? _settings.Window.ManualWidth : DefaultWindowWidth;
                double curH = _settings.Window.ManualHeight > 0
                    ? _settings.Window.ManualHeight : DefaultWindowHeight;

                double wantW = width > 0 ? width : curW;
                double wantH = height > 0 ? height : curH;

                double cw, ch;
                ClampWindowSize(wantW, wantH, out cw, out ch);

                // 写配置（复用同一套夹紧，保证与下次启动一致）
                SetManualWindowSize(cw, ch);

                // 通知宿主改真实窗口尺寸
                if (WindowSizeRequested != null)
                {
                    WindowSizeRequested(this, new WindowSizeEventArgs(cw, ch));
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 请求把主窗口尺寸改成指定值的事件。
        /// </summary>
        /// <remarks>由 MainWindow 在构造时订阅，实现"设置面板调宽高 → 悬浮窗实时变化"。</remarks>
        public event EventHandler<WindowSizeEventArgs> WindowSizeRequested;

        /// <summary>今天全部作业条目数。</summary>
        public int TodayCount { get { return GetTodayItemCount(); } }

        /// <summary>今天已完成的作业条目数。</summary>
        public int DoneCount
        {
            get
            {
                if (AllCards == null) return 0;
                return AllCards.Sum(c => c.Items.Count(i => i.Done));
            }
        }

        /// <summary>
        /// 通知界面刷新统计类属性（条目数、完成数）。
        /// </summary>
        /// <remarks>在条目增删、勾选状态变化后调用。</remarks>
        public void RefreshCounters()
        {
            OnPropertyChanged("TodayCount");
            OnPropertyChanged("DoneCount");
        }

        /// <summary>全部科目配置。</summary>
        public List<SubjectConfig> Subjects { get { return _settings.Subjects; } }

        /// <summary>全部模板配置（仅返回启用的）。</summary>
        public List<TemplateConfig> EnabledTemplates
        {
            get
            {
                if (_settings.Templates == null) return new List<TemplateConfig>();
                List<TemplateConfig> list = _settings.Templates.Where(t => t != null && t.Enabled).ToList();
                return list;
            }
        }

        /// <summary>
        /// 构造主视图模型。
        /// </summary>
        /// <param name="store">JSON 存储管理器，不可为 null</param>
        /// <exception cref="ArgumentNullException">store 为 null 时抛出</exception>
        /// <remarks>
        /// 构造过程会读取两个 JSON 文件，但都是小文件（几 KB），耗时通常在 1-3ms。
        /// </remarks>
        public MainViewModel(JsonStore store)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            // ThrottledSaver 只需要 Action，而 SaveAllNow 返回 bool（供调用方判断结果），
            // 因此这里包一层 lambda 丢弃返回值。
            _saver = new ThrottledSaver(delegate { SaveAllNow(); });

            _settings = _store.LoadSettings();
            _homework = _store.LoadHomework();

            AllCards = new List<SubjectCardViewModel>();
            Rows = new ObservableCollection<RowViewModel>();
            _currentDate = DateTime.Today;
            _columnCount = 1;

            // 优先显示容错提示；没有异常则显示常规就绪信息
            if (!string.IsNullOrEmpty(_store.LastNotice))
            {
                _statusText = _store.LastNotice;
            }
            else if (!_store.IsWritable)
            {
                _statusText = "提示：当前目录不可写，修改不会保存。建议把程序解压到桌面或 D 盘再运行。";
            }
            else
            {
                _statusText = "就绪";
            }

            RebuildCards();

            // 必须紧接着算一次分列：界面绑定的是 Columns，而 Columns 只在 RefreshLayout 里赋值。
            // 漏掉这一句会导致 Columns 保持 null，ItemsControl 拿不到数据源，
            // 表现为窗口能显示（标题栏/工具条/状态栏都在）但内容区完全空白。
            RefreshLayout();
        }

        /// <summary>
        /// 重新构建卡片列表的公开入口。
        /// </summary>
        /// <remarks>
        /// 用途：设置窗口里勾选/取消勾选科目后，需要让主窗口立刻反映变化。
        /// 不能调用 ReloadFromDisk（那会丢弃内存中未保存的修改），
        /// 因此单独暴露这个方法：只重建卡片 + 重算布局。
        /// </remarks>
        public void RebuildCardsPublic()
        {
            try
            {
                RebuildCards();
                RefreshLayout();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 把一张卡片移动到新的位置（长按拖拽排序的后端实现）。
        /// </summary>
        /// <param name="fromIndex">源卡片在 AllCards 中的索引</param>
        /// <param name="toIndex">目标位置索引（插入到该位置之前）</param>
        /// <returns>true = 顺序确实发生了变化；false = 索引无效或位置未变</returns>
        /// <remarks>
        /// 实现思路：
        /// 1. 先在 AllCards（当前界面上看到的内存顺序）里做 List 重排，得到新顺序；
        /// 2. 再按新顺序把 Order 值重新编号（10, 20, 30 ... 步长 10），
        ///    这样 order 永远保持紧凑，手工编辑 JSON 时也好认；
        /// 3. 重排 _settings.Subjects 使其内部顺序与之一致（order 相同时的稳定依据）；
        /// 4. 重建卡片 + 重算布局 + 触发节流保存。
        /// 
        /// 为什么不只改 Order 而不动 _settings.Subjects 的列表顺序：
        /// RebuildCards 的排序是 OrderBy(Order).ThenBy(原索引)，
        /// 两个科目 order 相同时会退回配置顺序。如果只改 order 值，
        /// 虽然本例中不会相等，但把列表顺序也同步过去可以让数据源保持自洽，
        /// 用户手工打开 settings.json 时看到的顺序就是界面顺序，不会有认知错位。
        /// 
        /// 注意：只允许在**启用**科目之间重排。被禁用的科目（Enabled=false）不在 AllCards 里，
        /// 其 order 保持不变，但会被重新编号时挤到后面——这是可接受的，
        /// 因为它们本来就不显示，重新启用时会按 order 排到末尾，用户可再次拖拽调整。
        /// </remarks>
        public bool MoveCard(int fromIndex, int toIndex)
        {
            try
            {
                if (AllCards == null) return false;
                if (fromIndex < 0 || fromIndex >= AllCards.Count) return false;
                if (toIndex < 0) toIndex = 0;
                if (toIndex >= AllCards.Count) toIndex = AllCards.Count - 1;
                if (fromIndex == toIndex) return false;

                // ---- 步骤 1：在内存列表里重排 ----
                var reordered = new List<SubjectCardViewModel>(AllCards);
                SubjectCardViewModel moved = reordered[fromIndex];
                reordered.RemoveAt(fromIndex);
                reordered.Insert(toIndex, moved);

                // ---- 步骤 2：同步回 AllCards ----
                AllCards.Clear();
                AllCards.AddRange(reordered);

                // ---- 步骤 3：按新顺序重编 Order（步长 10，方便手工再插入） ----
                if (_settings.Subjects != null)
                {
                    int order = 10;
                    foreach (SubjectCardViewModel card in AllCards)
                    {
                        if (card == null || card.Config == null) continue;
                        card.Config.Order = order;
                        order += 10;
                    }
                }

                // ---- 步骤 4：让 _settings.Subjects 的列表顺序与界面一致 ----
                // 只重排「本就在列表里」的项，禁用的科目一律沉到末尾（保持其相对顺序），
                // 避免误删或误改用户数据。
                List<SubjectConfig> src = _settings.Subjects;
                if (src != null && src.Count > 0)
                {
                    var visibleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (SubjectCardViewModel card in AllCards)
                    {
                        if (card != null && !string.IsNullOrEmpty(card.Key)) visibleKeys.Add(card.Key);
                    }

                    int maxOrder = 0;
                    foreach (SubjectConfig cfg in src)
                    {
                        if (cfg != null && cfg.Order > maxOrder) maxOrder = cfg.Order;
                    }

                    var rebuilt = new List<SubjectConfig>(src.Count);
                    // ① 可见科目：按界面顺序
                    foreach (SubjectCardViewModel card in AllCards)
                    {
                        if (card == null) continue;
                        SubjectConfig hit = src.FirstOrDefault(c =>
                            c != null && string.Equals(c.Key, card.Key, StringComparison.OrdinalIgnoreCase));
                        if (hit != null) rebuilt.Add(hit);
                    }
                    // ② 不可见科目：保持原相对顺序，order 顺延到可见科目之后
                    int tail = maxOrder + 10;
                    foreach (SubjectConfig cfg in src)
                    {
                        if (cfg == null) continue;
                        if (visibleKeys.Contains(cfg.Key)) continue;
                        cfg.Order = tail;
                        tail += 10;
                        rebuilt.Add(cfg);
                    }

                    if (rebuilt.Count == src.Count)
                    {
                        src.Clear();
                        src.AddRange(rebuilt);
                    }
                }

                // ---- 步骤 5：重建界面 + 落盘 ----
                RebuildCards();
                RefreshLayout();
                _saver.RequestSave();
                StatusText = "已调整「" + moved.Name + "」的位置";
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 按当前 AllCards 的顺序，查出一张卡片所在的索引。
        /// </summary>
        /// <param name="card">目标卡片，可为 null</param>
        /// <returns>索引；找不到返回 -1</returns>
        /// <remarks>供界面层的拖拽逻辑做命中判定用。</remarks>
        public int IndexOfCard(SubjectCardViewModel card)
        {
            if (card == null || AllCards == null) return -1;
            for (int i = 0; i < AllCards.Count; i++)
            {
                if (ReferenceEquals(AllCards[i], card)) return i;
            }
            return -1;
        }

        /// <summary>
        /// 重新构建科目卡片列表（科目配置变化或切换日期后调用）。
        /// </summary>
        /// <remarks>
        /// 排序规则：先按 order 升序，order 相同时按配置中的出现顺序。
        /// 只保留 enabled 为 true 的科目。
        /// 界面上「没有作业的学科不显示对应窗口」由布局阶段过滤，此处保留全部启用科目。
        /// </remarks>
        private void RebuildCards()
        {
            AllCards.Clear();

            if (_settings.Subjects == null)
            {
                OnPropertyChanged("EnabledSubjectCards");
                return;
            }

            // 带原始索引排序，保证 order 相同时结果稳定
            var ordered = _settings.Subjects
                .Select((cfg, idx) => new { cfg = cfg, idx = idx })
                .Where(x => x.cfg != null && x.cfg.Enabled)
                .OrderBy(x => x.cfg.Order)
                .ThenBy(x => x.idx)
                .Select(x => x.cfg)
                .ToList();

            foreach (SubjectConfig cfg in ordered)
            {
                var card = new SubjectCardViewModel(cfg);
                List<HomeworkItem> items = GetItemsFor(_currentDate, cfg.Key);
                foreach (HomeworkItem item in items)
                {
                    card.Items.Add(new HomeworkItemViewModel(item, cfg.Key));
                }
                AllCards.Add(card);
            }

            // AllCards 是普通 List，原地增删不会触发绑定更新，必须手动通知。
            // 空状态页的科目选择按钮依赖这个通知才能刷新。
            OnPropertyChanged("EnabledSubjectCards");
        }

        /// <summary>
        /// 从数据存储中取出指定日期、指定科目的作业条目列表。
        /// </summary>
        /// <param name="date">目标日期</param>
        /// <param name="subjectKey">科目键名</param>
        /// <returns>条目列表；不存在时返回空列表（不返回 null）</returns>
        /// <remarks>日期键使用 yyyy-MM-dd 格式。查不到时返回空列表而非 null，可避免大量空引用判断。</remarks>
        public List<HomeworkItem> GetItemsFor(DateTime date, string subjectKey)
        {
            if (_homework == null || _homework.Dates == null) return new List<HomeworkItem>();
            if (string.IsNullOrWhiteSpace(subjectKey)) return new List<HomeworkItem>();

            string dateKey = date.ToString("yyyy-MM-dd");
            Dictionary<string, List<HomeworkItem>> dayMap;
            if (!_homework.Dates.TryGetValue(dateKey, out dayMap) || dayMap == null)
            {
                return new List<HomeworkItem>();
            }

            List<HomeworkItem> list;
            if (!dayMap.TryGetValue(subjectKey, out list) || list == null)
            {
                return new List<HomeworkItem>();
            }
            return list;
        }

        /// <summary>
        /// 获取（必要时创建）指定日期、指定科目的条目列表，用于写入。
        /// </summary>
        /// <param name="date">目标日期</param>
        /// <param name="subjectKey">科目键名</param>
        /// <returns>可直接增删的 List 引用（保证非 null）</returns>
        /// <remarks>
        /// 这是唯一会修改 _homework.Dates 结构的入口，保证写入路径集中可控。
        /// </remarks>
        private List<HomeworkItem> EnsureItemsList(DateTime date, string subjectKey)
        {
            if (_homework.Dates == null)
            {
                _homework.Dates = new Dictionary<string, Dictionary<string, List<HomeworkItem>>>(StringComparer.OrdinalIgnoreCase);
            }

            string dateKey = date.ToString("yyyy-MM-dd");
            Dictionary<string, List<HomeworkItem>> dayMap;
            if (!_homework.Dates.TryGetValue(dateKey, out dayMap) || dayMap == null)
            {
                dayMap = new Dictionary<string, List<HomeworkItem>>(StringComparer.OrdinalIgnoreCase);
                _homework.Dates[dateKey] = dayMap;
            }

            List<HomeworkItem> list;
            if (!dayMap.TryGetValue(subjectKey, out list) || list == null)
            {
                list = new List<HomeworkItem>();
                dayMap[subjectKey] = list;
            }
            return list;
        }

        /// <summary>
        /// 为指定科目新增一条作业。
        /// </summary>
        /// <param name="subjectKey">科目键名，必须存在于配置中</param>
        /// <param name="content">作业内容</param>
        /// <returns>新增的条目视图模型；参数非法时返回 null</returns>
        /// <remarks>
        /// 新条目会追加到列表末尾，并立即触发一次节流保存请求。
        /// 内部会自动清理「空内容」的历史残留条目？不会——只有用户显式删除才会移除。
        /// </remarks>
        public HomeworkItemViewModel AddItem(string subjectKey, string content)
        {
            if (string.IsNullOrWhiteSpace(subjectKey)) return null;

            SubjectCardViewModel card = AllCards.FirstOrDefault(c => c.Key == subjectKey);
            if (card == null)
            {
                // 科目可能被禁用了，但也允许写入（容错：不因为配置改动而丢用户输入）
                SubjectConfig cfg = _settings.Subjects.FirstOrDefault(x => x != null && x.Key == subjectKey);
                if (cfg == null) return null;
                card = new SubjectCardViewModel(cfg);
                AllCards.Add(card);
            }

            List<HomeworkItem> list = EnsureItemsList(_currentDate, subjectKey);
            HomeworkItem model = HomeworkItem.Create(content);
            list.Add(model);

            var vm = new HomeworkItemViewModel(model, subjectKey);
            card.Items.Add(vm);
            card.RefreshCount();

            _saver.RequestSave();
            RefreshLayout();
            RefreshCounters();

            return vm;
        }

        /// <summary>
        /// 删除一条作业。
        /// </summary>
        /// <param name="item">待删除的条目视图模型</param>
        /// <returns>true = 删除成功</returns>
        /// <remarks>
        /// 通过 id 精确定位底层数据，避免内容重复时误删。
        /// 删除后若该科目已无条目，卡片的角标会刷新。
        /// </remarks>
        public bool RemoveItem(HomeworkItemViewModel item)
        {
            if (item == null || item.Model == null) return false;

            bool removedFromUi = false;
            SubjectCardViewModel card = AllCards.FirstOrDefault(c => c.Key == item.SubjectKey);
            if (card != null)
            {
                removedFromUi = card.Items.Remove(item);
                card.RefreshCount();
            }

            // 从底层数据中按 id 移除
            List<HomeworkItem> list = GetItemsFor(_currentDate, item.SubjectKey);
            string targetId = item.Model.Id;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] != null && list[i].Id == targetId)
                {
                    list.RemoveAt(i);
                    break;
                }
            }

            // 若该科目当天已无任何条目，把空列表也一并清掉，保持 JSON 干净
            if (list.Count == 0 && _homework.Dates != null)
            {
                string dateKey = _currentDate.ToString("yyyy-MM-dd");
                Dictionary<string, List<HomeworkItem>> dayMap;
                if (_homework.Dates.TryGetValue(dateKey, out dayMap) && dayMap != null)
                {
                    dayMap.Remove(item.SubjectKey);
                    if (dayMap.Count == 0)
                    {
                        _homework.Dates.Remove(dateKey);
                    }
                }
            }

            _saver.RequestSave();
            RefreshLayout();
            RefreshCounters();

            return removedFromUi;
        }

        /// <summary>
        /// 修改一条作业的内容。
        /// </summary>
        /// <param name="item">目标条目</param>
        /// <param name="newContent">新内容</param>
        /// <returns>true = 修改成功</returns>
        /// <remarks>内容未变化时不会触发保存。</remarks>
        public bool UpdateItemContent(HomeworkItemViewModel item, string newContent)
        {
            if (item == null) return false;
            string target = newContent ?? string.Empty;
            if (item.Content == target) return false;

            item.Content = target;
            _saver.RequestSave();
            RefreshLayout();
            return true;
        }

        /// <summary>
        /// 切换一条作业的完成状态。
        /// </summary>
        /// <param name="item">目标条目</param>
        /// <returns>true = 状态已改变</returns>
        /// <remarks>勾选操作只写内存 + 请求节流保存，不立即落盘，避免连续勾选时频繁写盘。</remarks>
        public bool ToggleItemDone(HomeworkItemViewModel item)
        {
            if (item == null) return false;
            item.Done = !item.Done;
            _saver.RequestSave();
            RefreshCounters();
            return true;
        }

        /// <summary>
        /// 请求一次保存（供外部在修改窗口设置后调用）。
        /// </summary>
        /// <remarks>不抛异常。</remarks>
        public void RequestSave()
        {
            _saver.RequestSave();
        }

        /// <summary>
        /// 立即把所有数据落盘（退出前调用，或用户点「立即保存」时调用）。
        /// </summary>
        /// <returns>true = 两个文件都保存成功</returns>
        /// <remarks>
        /// 目录不可写时返回 false 并把提示语写入 StatusText，不抛异常。
        /// </remarks>
        public bool SaveAllNow()
        {
            try
            {
                if (!_store.IsWritable)
                {
                    StatusText = "当前目录不可写，修改仅保存在内存中（关闭后丢失）。";
                    return false;
                }

                bool ok1 = _store.SaveSettings(_settings);
                bool ok2 = _store.SaveHomework(_homework);

                if (ok1 && ok2)
                {
                    StatusText = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
                    return true;
                }

                StatusText = "保存失败：文件可能被其它程序占用或磁盘已满，请检查后重试。";
                return false;
            }
            catch (Exception ex)
            {
                // 兜底：任何意外都不让程序崩溃
                StatusText = "保存时发生异常：" + ex.GetType().Name + "（数据仍保留在内存中）";
                return false;
            }
        }

        /// <summary>
        /// 重新载入 JSON 文件（用户在菜单里点「重新载入」时调用）。
        /// </summary>
        /// <returns>true = 载入成功</returns>
        /// <remarks>
        /// 用途：同学用记事本改完 JSON 后无需重启程序即可生效。
        /// 会丢弃内存中尚未保存的修改——因此调用前会先 Flush 一次。
        /// </remarks>
        public bool ReloadFromDisk()
        {
            try
            {
                _saver.FlushNow(); // 先保住当前修改

                _store = new JsonStore(_store.DataDirectory);
                _settings = _store.LoadSettings();
                _homework = _store.LoadHomework();

                _currentDate = DateTime.Today;
                OnPropertyChanged("CurrentDate");
                OnPropertyChanged("CurrentDateText");
                OnPropertyChanged("WindowSettings");

                RebuildCards();
                RefreshLayout();

                if (!string.IsNullOrEmpty(_store.LastNotice))
                {
                    StatusText = _store.LastNotice;
                }
                else
                {
                    StatusText = "已重新载入配置文件 " + DateTime.Now.ToString("HH:mm:ss");
                }
                return true;
            }
            catch (Exception ex)
            {
                StatusText = "重新载入失败：" + ex.GetType().Name + "（界面继续使用原有数据）";
                return false;
            }
        }

        /// <summary>
        /// 检查是否已跨天。若已跨天则切换到新日期。
        /// </summary>
        /// <returns>true = 发生了跨天切换</returns>
        /// <remarks>
        /// 由 MainWindow 用一个 60 秒的轻量定时器调用（一分钟一次，CPU 占用可忽略）。
        /// 只有日期确实变化时才重建卡片，平时几乎零开销。
        /// </remarks>
        public bool CheckDateRollover()
        {
            DateTime today = DateTime.Today;
            if (today == _currentDate) return false;

            _currentDate = today;
            OnPropertyChanged("CurrentDate");
            OnPropertyChanged("CurrentDateText");

            RebuildCards();
            RefreshLayout();
            StatusText = "已切换到新的一天：" + CurrentDateText;
            return true;
        }

        /// <summary>
        /// 强制切换到指定日期（用于「查看明天/昨天」这类临时浏览，不写入数据）。
        /// </summary>
        /// <param name="date">目标日期</param>
        /// <remarks>仅用于界面浏览，新增的作业仍会写入 CurrentDate。</remarks>
        public void SetDate(DateTime date)
        {
            _currentDate = date.Date;
            OnPropertyChanged("CurrentDate");
            OnPropertyChanged("CurrentDateText");
            RebuildCards();
            RefreshLayout();
        }

        /// <summary>
        /// 刷新网格布局：把全部启用科目分行排布（先横向铺满一行，放不下再换下一行）。
        /// </summary>
        /// <remarks>
        /// 设计变更说明（第 8 轮需求，经用户确认）：
        /// 原需求是「多列，屏幕右侧放不下就往左另起一列」（纵向优先）；
        /// 现改为「先横向铺满一行，放不下换下一行」（横向优先）。
        /// 理由是教室大屏横向空间充足、纵向空间被任务栏挤压，
        /// 横向铺排能让同一行的科目在视觉上成组，扫读更快；
        /// 行高不一也不影响，CardBorder 用 VerticalAlignment=Top 顶对齐。
        /// 
        /// 算法（贪心装箱，按宽度累加）：
        /// 1. 按 order 顺序遍历全部启用科目卡片；
        /// 2. 把卡片追加到当前行，累计已占宽度（含卡片间距）；
        /// 3. 当前行已有内容且再放一张会超出可用宽度 -> 另起一行；
        /// 4. 结果暴露为 Rows 集合。
        /// 
        /// 可用宽度来源：窗口当前实际宽度减去内容区左右内边距（各 10）与滚动条余量。
        /// 刻意用「当前宽度」而非配置值：窗口现在可以手动缩放（第 8 轮需求），
        /// 缩窄后必须重新分行，否则卡片会被裁掉。
        /// 因此 MainWindow 的 SizeChanged 会调本方法重算。
        /// 
        /// 空状态的兜底：一列都没有（全部科目被禁用）时保留一个空行，
        /// 界面由 ShowEmptyState 切换成整页空状态提示。
        /// </remarks>
        public void RefreshLayout()
        {
            var rows = new List<RowViewModel>();

            // 可用宽度：优先用窗口传进来的实测宽度，未测量时按默认窗口宽估算。
            double windowWidth = _layoutWindowWidth;
            if (windowWidth < 120) windowWidth = DefaultWindowWidth;

            // 减去：ScrollViewer 左右 Padding(10+10) + 右侧滚动条预留(14，Auto 模式下不悬停时可能不占位，
            // 但宁可少放一张卡片出现横向留白，也不要多放一张被裁掉半截)
            double availableWidth = windowWidth - 20 - 14;
            if (availableWidth < 200) availableWidth = 200;

            double slotWidth = CardSlotWidth;
            if (slotWidth < 100) slotWidth = 100;

            // 一行至少放得下一张卡片，防止极窄窗口下除零或死循环
            int perRowCap = (int)Math.Floor(availableWidth / slotWidth);
            if (perRowCap < 1) perRowCap = 1;

            RowViewModel current = new RowViewModel();
            double used = 0;
            int inRow = 0;

            foreach (SubjectCardViewModel card in AllCards)
            {
                // 当前行已有内容，且再放一张就超出可用宽度 -> 另起一行
                if (inRow > 0 && (used + slotWidth) > availableWidth)
                {
                    rows.Add(current);
                    current = new RowViewModel();
                    used = 0;
                    inRow = 0;
                }

                current.Cards.Add(card);
                used += slotWidth;
                inRow++;
            }

            // 收尾：即使一行都没有（全部科目被禁用）也保留一个空行，由界面显示空状态
            rows.Add(current);

            Rows = new ObservableCollection<RowViewModel>(rows);
            ColumnCount = rows.Count;

            // 关键：Rows 是普通属性，整体替换后必须手动通知绑定，
            // 否则界面会一直显示最初那个空集合——表现为「窗口能看见但没有卡片」。
            OnPropertyChanged("Rows");

            bool hasAny = AllCards.Any(c => c.Items.Count > 0);
            OnPropertyChanged("HasAnyHomework");
            OnPropertyChanged("ShowEmptyState");
            RefreshCounters();
        }

        /// <summary>
        /// 记录窗口实际内容宽度（由 MainWindow 在 SizeChanged 时写入）。
        /// </summary>
        /// <param name="width">窗口的逻辑像素宽度</param>
        /// <remarks>
        /// 窗口可手动缩放后，分行必须跟随实际宽度变化。
        /// 这个方法有意做「变化才重算」的判断：SizeChanged 在拖动过程中会高频触发，
        /// 每帧重算布局会造成可达数十次的冗余计算（需求要求空闲 CPU ≈ 0%，拖动时也应尽量省）。
        /// 阈值取 20 像素：小于这个幅度的变化不可能改变「一行放几张」的结果。
        /// </remarks>
        public void SetLayoutWidth(double width)
        {
            if (width < 1) return;
            if (Math.Abs(width - _layoutWindowWidth) < 20) return;

            _layoutWindowWidth = width;
            try
            {
                RefreshLayout();
            }
            catch
            {
                // 布局异常绝不允许影响主流程
            }
        }

        /// <summary>
        /// 分行结果，界面用 ItemsControl 绑定。
        /// </summary>
        /// <remarks>
        /// 初值为空集合而非 null：即使 RefreshLayout 因异常未能执行，
        /// ItemsControl 拿到空集合也只是不显示内容，不会因为 null 抛绑定异常。
        /// 
        /// 命名沿革：早期是「多列」布局，属性名 Columns；第 8 轮改为横向网格后
        /// 语义变为「一行卡片」，故改名为 Rows。XAML 绑定已同步。
        /// </remarks>
        public ObservableCollection<RowViewModel> Rows { get; private set; }

        /// <summary>当天是否存在任何作业。</summary>
        /// <remarks>仅用于统计展示，不再决定卡片是否显示（空科目也常驻）。</remarks>
        public bool HasAnyHomework
        {
            get { return AllCards != null && AllCards.Any(c => c.Items.Count > 0); }
        }

        /// <summary>
        /// 是否显示整页空状态。
        /// </summary>
        /// <remarks>
        /// 语义已变更：空科目现在会常驻显示卡片，因此整页空状态只在
        /// **一个启用科目都没有**（全部被禁用或配置异常）时才出现。
        /// 这种情况下需要提示用户去检查 settings.json 的 subjects 配置。
        /// </remarks>
        public bool ShowEmptyState
        {
            get { return AllCards == null || AllCards.Count == 0; }
        }

        /// <summary>
        /// 全部启用科目的卡片列表。
        /// </summary>
        /// <remarks>正常使用时 Rows 已经包含全部科目，此属性保留供兼容与统计。</remarks>
        public List<SubjectCardViewModel> EnabledSubjectCards
        {
            get { return AllCards; }
        }

        /// <summary>
        /// 更新透明度设置。
        /// </summary>
        /// <param name="percent">目标透明度百分比，会被夹紧到 70—95</param>
        /// <returns>夹紧后的实际值</returns>
        /// <remarks>需求约定透明度仅限 70—95。</remarks>
        public int SetOpacity(int percent)
        {
            if (percent < 70) percent = 70;
            if (percent > 95) percent = 95;
            _settings.Window.OpacityPercent = percent;
            OnPropertyChanged("WindowSettings");
            OnPropertyChanged("OpacityValue");
            _saver.RequestSave();
            return percent;
        }

        /// <summary>
        /// 更新置顶状态。
        /// </summary>
        /// <param name="topmost">是否置顶</param>
        /// <remarks>置顶状态会保存，窗口位置不保存（需求约定）。</remarks>
        public void SetTopmost(bool topmost)
        {
            _settings.Window.Topmost = topmost;
            OnPropertyChanged("WindowSettings");
            _saver.RequestSave();
        }

        /// <summary>
        /// 记录当前显示器的 DPI 缩放比例，用于把工作区物理像素换算成逻辑像素。
        /// </summary>
        /// <param name="dpiScale">缩放比例（1.0 = 100%，1.25 = 125%）</param>
        /// <remarks>
        /// 由 MainWindow 在 ContentRendered 与 DpiChanged 时调用。
        /// 比例不变时直接返回，避免无意义地刷新绑定（需求要求空闲 CPU ≈ 0%）。
        /// 比例变化时需要通知 MaxWindowHeight，因为它依赖该值。
        /// </remarks>
        public void SetDpiScale(double dpiScale)
        {
            if (dpiScale <= 0.01) return;
            if (Math.Abs(dpiScale - _dpiScaleCache) < 0.001) return;

            _dpiScaleCache = dpiScale;
            OnPropertyChanged("MaxWindowHeight");
        }

        /// <summary>
        /// 更新字号设置。
        /// </summary>
        /// <param name="fontSize">正文字号，夹紧到 12—48</param>
        /// <param name="titleFontSize">标题字号，夹紧到 12—56</param>
        /// <remarks>字号变化会影响分行高度估算，因此需要重新计算布局。</remarks>
        public void SetFontSizes(int fontSize, int titleFontSize)
        {
            if (fontSize < 12) fontSize = 12;
            if (fontSize > 48) fontSize = 48;
            if (titleFontSize < 12) titleFontSize = 12;
            if (titleFontSize > 56) titleFontSize = 56;

            _settings.Window.FontSize = fontSize;
            _settings.Window.TitleFontSize = titleFontSize;
            OnPropertyChanged("WindowSettings");
            OnPropertyChanged("FontSize");
            OnPropertyChanged("TitleFontSize");
            // 占位文字字号/行高由正文字号推导，必须一起通知，否则改字号后占位行不更新
            OnPropertyChanged("EmptyHintFontSize");
            OnPropertyChanged("EmptyHintLineHeight");
            _saver.RequestSave();
            RefreshLayout();
        }

        /// <summary>
        /// 更新列宽设置。
        /// </summary>
        /// <param name="width">列宽像素，夹紧到 200—800</param>
        /// <remarks>列宽变化会影响分列结果，需要重新计算布局。</remarks>
        public void SetColumnWidth(int width)
        {
            if (width < 200) width = 200;
            if (width > 800) width = 800;
            _settings.Window.ColumnWidth = width;
            OnPropertyChanged("WindowSettings");
            OnPropertyChanged("ColumnWidth");
            _saver.RequestSave();
            RefreshLayout();
        }

        /// <summary>
        /// 更新窗口最大高度设置。
        /// </summary>
        /// <param name="maxHeight">高度像素，夹紧到 300—1400</param>
        /// <remarks>
        /// 上限设为 1400 而非 3000：即使写 3000，运行时也会被显示器工作区高度压回去
        /// （见 MaxWindowHeight），留一个贴近实际可用的范围，滑块更好拖。
        /// 高度变化会改变一屏能放下几行，必须重算分行。
        /// </remarks>
        public void SetMaxHeight(int maxHeight)
        {
            if (maxHeight < 300) maxHeight = 300;
            if (maxHeight > 1400) maxHeight = 1400;
            _settings.Window.MaxHeight = maxHeight;
            OnPropertyChanged("WindowSettings");
            OnPropertyChanged("MaxWindowHeight");
            _saver.RequestSave();
            RefreshLayout();
        }

        /// <summary>
        /// 立即保存并释放资源（窗口关闭时调用）。
        /// </summary>
        /// <remarks>先 Flush 再 Dispose，确保最后的修改不丢失。</remarks>
        public void Shutdown()
        {
            try
            {
                _saver.FlushNow();
                _saver.Dispose();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 获取当前日期下的所有作业条目总数（统计用）。
        /// </summary>
        /// <returns>条目总数</returns>
        public int GetTodayItemCount()
        {
            if (AllCards == null) return 0;
            return AllCards.Sum(c => c.Items.Count);
        }
    }
}
