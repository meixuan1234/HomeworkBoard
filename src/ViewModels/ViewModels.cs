using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using HomeworkBoard.Models;

namespace HomeworkBoard.ViewModels
{
    /// <summary>
    /// 视图模型基类，提供 INotifyPropertyChanged 的最小实现。
    /// 
    /// 设计说明：不引入 MVVM 框架（如 Prism / CommunityToolkit.Mvvm），
    /// 因为本程序界面简单，手写通知可减少依赖、缩小体积、加快启动。
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        /// <summary>
        /// 属性变更事件。
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 触发属性变更通知。
        /// </summary>
        /// <param name="propertyName">变更的属性名，默认由编译器自动填入</param>
        /// <remarks>不抛异常。</remarks>
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            try
            {
                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null && propertyName != null)
                {
                    handler(this, new PropertyChangedEventArgs(propertyName));
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 设置字段值并在变化时触发通知。
        /// </summary>
        /// <typeparam name="T">字段类型</typeparam>
        /// <param name="field">字段引用</param>
        /// <param name="value">新值</param>
        /// <param name="propertyName">属性名</param>
        /// <returns>true = 值确实发生了变化</returns>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>
    /// 单条作业的视图模型。
    /// </summary>
    public class HomeworkItemViewModel : ObservableObject
    {
        private string _content;
        private bool _done;

        /// <summary>底层数据模型引用，用于回写保存。</summary>
        public HomeworkItem Model { get; private set; }

        /// <summary>所属科目的键名，用于定位到数据存储中的位置。</summary>
        public string SubjectKey { get; private set; }

        /// <summary>
        /// 作业内容文本。修改后需要调用方触发保存。
        /// </summary>
        public string Content
        {
            get { return _content; }
            set
            {
                if (SetField(ref _content, value))
                {
                    // 同步到底层模型，保持数据一致
                    if (Model != null) Model.Content = _content;
                    OnPropertyChanged("DisplayContent");
                }
            }
        }

        /// <summary>
        /// 展示用文本：内容为空时显示占位符，避免界面上出现空白行让人以为程序出错。
        /// </summary>
        public string DisplayContent
        {
            get { return string.IsNullOrWhiteSpace(_content) ? "(未填写内容)" : _content; }
        }

        /// <summary>
        /// 是否已完成。勾选后文字变暗并加删除线。
        /// </summary>
        public bool Done
        {
            get { return _done; }
            set
            {
                if (SetField(ref _done, value))
                {
                    if (Model != null) Model.Done = _done;
                }
            }
        }

        /// <summary>
        /// 构造作业条目视图模型。
        /// </summary>
        /// <param name="model">底层数据模型，不可为 null</param>
        /// <param name="subjectKey">所属科目键名</param>
        /// <exception cref="ArgumentNullException">model 为 null 时抛出（由调用方保证不为 null）</exception>
        public HomeworkItemViewModel(HomeworkItem model, string subjectKey)
        {
            if (model == null) throw new ArgumentNullException("model");
            Model = model;
            SubjectKey = subjectKey ?? string.Empty;
            _content = model.Content ?? string.Empty;
            _done = model.Done;
        }
    }

    /// <summary>
    /// 单个科目卡片的视图模型。
    /// </summary>
    public class SubjectCardViewModel : ObservableObject
    {
        private readonly SubjectConfig _config;

        /// <summary>科目配置引用（键名、显示名、颜色等）。</summary>
        public SubjectConfig Config { get { return _config; } }

        /// <summary>科目键名。</summary>
        public string Key { get { return _config.Key; } }

        /// <summary>科目显示名称。</summary>
        public string Name { get { return _config.Name; } }

        /// <summary>科目主题色字符串（十六进制）。</summary>
        public string ColorHex { get { return _config.Color; } }

        /// <summary>该科目下的所有作业条目。</summary>
        public ObservableCollection<HomeworkItemViewModel> Items { get; private set; }

        /// <summary>卡片标题上的条目数角标文本，如 "3 项"。</summary>
        public string CountText
        {
            get
            {
                int n = Items == null ? 0 : Items.Count;
                return n + " 项";
            }
        }

        /// <summary>
        /// 该科目下是否有作业。
        /// </summary>
        /// <remarks>
        /// 用于驱动卡片的两种视觉状态：
        /// - true  = 有作业，正常显示（亮色条、深背景、科目色标题、列出条目）；
        /// - false = 无作业，淡化占位显示（半透明色条、更暗背景、灰色标题、显示"暂无"）。
        /// 常驻显示的意义：教室大屏上科目位置固定，学生扫一眼就知道该看哪，
        /// 老师也永远能在同一个位置点到 ＋ 添加作业。
        /// </remarks>
        public bool HasItems
        {
            get { return Items != null && Items.Count > 0; }
        }

        /// <summary>
        /// 卡片是否处于淡化（空）状态。供 XAML 绑定，避免写取反转换器。
        /// </summary>
        public bool IsEmpty
        {
            get { return !HasItems; }
        }

        /// <summary>
        /// 空状态下的占位提示文本。
        /// </summary>
        /// <remarks>显示在无作业科目的卡片里，替换条目列表的位置。</remarks>
        public string EmptyHint
        {
            get { return "暂无作业"; }
        }

        /// <summary>
        /// 标题的显示颜色。
        /// </summary>
        /// <remarks>
        /// 有作业时用科目主题色（醒目，一眼分辨科目）；
        /// 无作业时退化为次级灰（不抢注意力，但位置仍然占着）。
        /// 之所以在 ViewModel 里算好而不是用 DataTrigger：
        /// 颜色来自 settings.json 的动态值，DataTrigger 无法直接引用绑定后的字符串做转换。
        /// </remarks>
        public string TitleColorHex
        {
            get { return HasItems ? _config.Color : "#6E6E73"; }
        }

        /// <summary>
        /// 左色条的颜色（带透明度）。
        /// </summary>
        /// <remarks>
        /// 有作业时用不透明的科目主题色；无作业时用半透明版本，
        /// 保留科目辨识度但视觉上后退一层。
        /// </remarks>
        public string BarColorHex
        {
            get { return HasItems ? _config.Color : "#4A4A4E"; }
        }

        /// <summary>
        /// 构造科目卡片视图模型。
        /// </summary>
        /// <param name="config">科目配置，不可为 null</param>
        /// <exception cref="ArgumentNullException">config 为 null 时抛出</exception>
        public SubjectCardViewModel(SubjectConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            _config = config;
            Items = new ObservableCollection<HomeworkItemViewModel>();
        }

        /// <summary>
        /// 条目集合发生变化后调用，刷新角标文本与卡片状态。
        /// </summary>
        /// <remarks>
        /// 必须同时通知 HasItems / IsEmpty / TitleColorHex / BarColorHex：
        /// 卡片从「空」变「有作业」时，色条、标题颜色、占位提示都要跟着切换。
        /// </remarks>
        public void RefreshCount()
        {
            OnPropertyChanged("CountText");
            OnPropertyChanged("HasItems");
            OnPropertyChanged("IsEmpty");
            OnPropertyChanged("TitleColorHex");
            OnPropertyChanged("BarColorHex");
        }
    }

    /// <summary>
    /// 一行卡片。网格布局用：一行从左往右铺，铺不下就换到下一行。
    /// </summary>
    /// <remarks>
    /// 历史沿革：早期版本是「纵向填满一列，放不下往左另起一列」的多列布局，
    /// 当时这个类叫 ColumnViewModel，装的是「一列卡片」。
    /// 第 8 轮需求改为「横着堆不下了再竖着」，语义从「列」变成「行」，
    /// 但 WPF 绑定的是属性名而非类型名，改类名不会影响 XAML 的 DataTemplate 匹配，
    /// 因此这里直接改名以保持语义准确（DataTemplate 上的 DataType 已同步更新）。
    /// </remarks>
    public class RowViewModel : ObservableObject
    {
        /// <summary>本行包含的科目卡片（从左到右）。</summary>
        public ObservableCollection<SubjectCardViewModel> Cards { get; private set; }

        /// <summary>本行的宽度估算值（用于分行算法）。</summary>
        public double EstimatedWidth { get; set; }

        /// <summary>
        /// 构造行视图模型。
        /// </summary>
        public RowViewModel()
        {
            Cards = new ObservableCollection<SubjectCardViewModel>();
            EstimatedWidth = 0;
        }
    }

    /// <summary>
    /// 窗口尺寸变更请求的事件参数。
    /// </summary>
    /// <remarks>
    /// 用于把「设置面板里调滑块」的意图从视图模型传到宿主窗口。
    /// 视图模型不持有 Window 引用（保持可测试性），因此用事件解耦。
    /// </remarks>
    public class WindowSizeEventArgs : EventArgs
    {
        /// <summary>请求的目标宽度（逻辑像素）。</summary>
        public double Width { get; private set; }

        /// <summary>请求的目标高度（逻辑像素）。</summary>
        public double Height { get; private set; }

        /// <summary>
        /// 构造事件参数。
        /// </summary>
        /// <param name="width">目标宽度（逻辑像素，已夹紧）</param>
        /// <param name="height">目标高度（逻辑像素，已夹紧）</param>
        public WindowSizeEventArgs(double width, double height)
        {
            Width = width;
            Height = height;
        }
    }
}
