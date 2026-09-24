using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HomeworkBoard.ViewModels;

namespace HomeworkBoard.Views
{
    /// <summary>
    /// 设置窗口。
    /// 
    /// 提供四类设置：
    /// 1. 外观：透明度（70—95，步长 5）、正文字号、标题字号、列宽；
    /// 2. 科目：勾选启用/隐藏；
    /// 3. 数据文件：显示路径、可写性提示、打开文件夹、立即保存、重新载入；
    /// 4. 底部「完成」按钮关闭。
    /// 
    /// 所有改动都是即时生效 + 自动保存（走主视图模型的节流保存），
    /// 因此不需要额外的「应用」按钮。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly MainViewModel _viewModel;

        /// <summary>防止初始化期间控件赋值触发 ValueChanged 造成重复保存。</summary>
        private bool _isInitializing;

        /// <summary>
        /// 构建设置窗口。
        /// </summary>
        /// <param name="viewModel">主视图模型，不可为 null</param>
        /// <remarks>
        /// 初始化时先把 _isInitializing 置 true，把当前值灌进各控件，
        /// 完成后置 false，避免启动瞬间就写好几次磁盘。
        /// </remarks>
        public SettingsWindow(MainViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            _isInitializing = true;

            try
            {
                LoadCurrentValues();
            }
            catch
            {
                // 加载失败时保持控件默认值，不影响窗口打开
            }
            finally
            {
                _isInitializing = false;
            }
        }

        /// <summary>
        /// 把当前设置值加载到各控件。
        /// </summary>
        /// <remarks>不抛异常。</remarks>
        private void LoadCurrentValues()
        {
            if (_viewModel == null) return;

            var w = _viewModel.WindowSettings;
            if (w == null) return;

            int opacity = w.OpacityPercent;
            if (opacity < 70) opacity = 70;
            if (opacity > 95) opacity = 95;
            OpacitySlider.Value = opacity;
            OpacityValueText.Text = opacity + "%";

            int fs = w.FontSize;
            if (fs < 12) fs = 12;
            if (fs > 48) fs = 48;
            FontSlider.Value = fs;
            FontSizeValueText.Text = fs + " px";

            int tfs = w.TitleFontSize;
            if (tfs < 12) tfs = 12;
            if (tfs > 56) tfs = 56;
            TitleFontSlider.Value = tfs;
            TitleFontValueText.Text = tfs + " px";

            int cw = w.ColumnWidth;
            if (cw < 200) cw = 200;
            if (cw > 800) cw = 800;
            ColumnWidthSlider.Value = cw;
            ColumnWidthValueText.Text = cw + " px";

            int mh = w.MaxHeight;
            if (mh < 300) mh = 300;
            if (mh > 1400) mh = 1400;
            MaxHeightSlider.Value = mh;
            MaxHeightValueText.Text = mh + " px";

            // ---- 窗口尺寸滑块 ----
            // 先把滑块的范围按实际屏幕能力对齐，再灌当前值。
            // 顺序很重要：若先赋值再改 Maximum，WPF 会把超出新上限的 Value 静默夹掉，
            // 导致"设置里显示的值"和"实际窗口尺寸"不一致。
            LoadWindowSizeSliders();

            // 科目列表
            SubjectList.ItemsSource = _viewModel.Subjects;

            // 数据目录
            try
            {
                string dir = AppContext.BaseDirectory;
                DataDirText.Text = dir;

                if (!_viewModel.IsReadOnlyMode)
                {
                    // 可写，无需提示
                    WritableHintText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    WritableHintText.Text = "⚠ 当前目录不可写（可能位于 C:\\Program Files 等受保护位置）。"
                        + "程序仍可正常使用，但修改不会保存。建议把整个程序文件夹解压到桌面或 D 盘后重新运行。";
                    WritableHintText.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 透明度滑块变化：更新设置并即时应用。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">滑块值变化参数</param>
        /// <remarks>滑块设置了 IsSnapToTickEnabled，因此值会自动吸附到 5 的倍数。</remarks>
        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;
            try
            {
                int v = (int)Math.Round(e.NewValue);
                OpacityValueText.Text = v + "%";
                _viewModel.SetOpacity(v);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 正文字号滑块变化。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">滑块值变化参数</param>
        /// <remarks>字号会同步影响主窗口的条目文字与分行高度估算。</remarks>
        private void FontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;
            try
            {
                int v = (int)Math.Round(e.NewValue);
                FontSizeValueText.Text = v + " px";
                _viewModel.SetFontSizes(v, (int)Math.Round(TitleFontSlider.Value));
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 标题字号滑块变化。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">滑块值变化参数</param>
        private void TitleFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;
            try
            {
                int v = (int)Math.Round(e.NewValue);
                TitleFontValueText.Text = v + " px";
                _viewModel.SetFontSizes((int)Math.Round(FontSlider.Value), v);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 列宽滑块变化。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">滑块值变化参数</param>
        private void ColumnWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;
            try
            {
                int v = (int)Math.Round(e.NewValue);
                ColumnWidthValueText.Text = v + " px";
                _viewModel.SetColumnWidth(v);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 窗口最大高度滑块变化。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>
        /// 立即写回视图模型并刷新主窗口布局：高度上限变化会影响分行结果
        /// （可用高度变了，每行能放几个科目也变了）。
        /// 
        /// 同时要同步「窗口高度」滑块的上限：两者是同一套约束的两处入口，
        /// 若不同步会出现"最大高度调到 500，窗口高度滑块还能拖到 1400"的矛盾。
        /// 若当前窗口高度已超过新上限，把它一并拉回并应用，避免配置与实际不一致。
        /// </remarks>
        private void MaxHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;
            try
            {
                int v = (int)Math.Round(e.NewValue);
                MaxHeightValueText.Text = v + " px";
                _viewModel.SetMaxHeight(v);

                // 同步窗口高度滑块的上限
                double maxH = _viewModel.MaxWindowHeightValue;
                if (maxH < 300) maxH = 300;
                WinHeightSlider.Maximum = maxH;

                // 当前窗口高度超了新上限：拉回并实时应用
                if (WinHeightSlider.Value > maxH)
                {
                    WinHeightSlider.Value = SnapToTick(maxH);
                    WinHeightValueText.Text = (int)WinHeightSlider.Value + " px";
                    _viewModel.RequestWindowSize(0, WinHeightSlider.Value);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 初始化「窗口宽度」「窗口高度」两个滑块的范围与当前值。
        /// </summary>
        /// <remarks>
        /// 三个要点：
        /// ① 范围必须先设（Maximum/Minimum）再设 Value：WPF 的 Slider 在 Maximum 变小时
        ///    会立刻把超出的 Value 夹到新上限，若顺序反了会丢掉用户原来的尺寸值；
        /// ② 上限从视图模型取实际可用值（宽度按屏幕宽度算、高度按 MaxWindowHeight 算），
        ///    避免用户拖到"程序根本给不了"的值上，产生"拖了没反应"的错觉；
        /// ③ 用 _isInitializing 屏蔽赋值过程中的 ValueChanged，避免一打开设置就写盘。
        /// </remarks>
        private void LoadWindowSizeSliders()
        {
            if (_viewModel == null) return;

            try
            {
                // ---- 宽度 ----
                double maxW = _viewModel.MaxWindowWidth;
                if (maxW < 400) maxW = 400;      // 极端小屏兜底，保证滑块有可调空间

                WinWidthSlider.Minimum = 280;
                WinWidthSlider.Maximum = maxW;

                double curW = _viewModel.InitialWindowWidth;
                if (curW < WinWidthSlider.Minimum) curW = WinWidthSlider.Minimum;
                if (curW > WinWidthSlider.Maximum) curW = WinWidthSlider.Maximum;
                // 吸附到 20 的倍数，与 TickFrequency 一致，避免显示 381 px 这种零碎值
                WinWidthSlider.Value = SnapToTick(curW);
                WinWidthValueText.Text = (int)WinWidthSlider.Value + " px";

                // ---- 高度 ----
                double maxH = _viewModel.MaxWindowHeightValue;
                if (maxH < 300) maxH = 300;

                WinHeightSlider.Minimum = 180;
                WinHeightSlider.Maximum = maxH;

                double curH = _viewModel.InitialWindowHeight;
                if (curH < WinHeightSlider.Minimum) curH = WinHeightSlider.Minimum;
                if (curH > WinHeightSlider.Maximum) curH = WinHeightSlider.Maximum;
                WinHeightSlider.Value = SnapToTick(curH);
                WinHeightValueText.Text = (int)WinHeightSlider.Value + " px";
            }
            catch
            {
                // 取不到就保持 XAML 里的默认范围
            }
        }

        /// <summary>
        /// 把数值吸附到 20 的整数倍（与滑块的 TickFrequency 一致）。
        /// </summary>
        /// <param name="value">原始值</param>
        /// <returns>吸附后的值</returns>
        /// <remarks>手动赋值 Slider.Value 不会自动吸附，必须自己算，否则显示值会带零头。</remarks>
        private static double SnapToTick(double value)
        {
            return Math.Round(value / 20.0) * 20.0;
        }

        /// <summary>
        /// 窗口宽度滑块变化：实时改变悬浮窗宽度。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">滑块值变化参数</param>
        /// <remarks>
        /// 高度传 0 表示"保持不变"（视图模型会沿用当前配置值），
        /// 这样拖宽度滑块不会意外把高度重置成默认值。
        /// </remarks>
        private void WinWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;
            try
            {
                int v = (int)Math.Round(e.NewValue);
                WinWidthValueText.Text = v + " px";
                _viewModel.RequestWindowSize(v, 0);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 窗口高度滑块变化：实时改变悬浮窗高度。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">滑块值变化参数</param>
        /// <remarks>宽度传 0 表示"保持不变"。</remarks>
        private void WinHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;
            try
            {
                int v = (int)Math.Round(e.NewValue);
                WinHeightValueText.Text = v + " px";
                _viewModel.RequestWindowSize(0, v);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 科目启用勾选框变化：刷新主窗口的卡片显示。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>
        /// Enabled 已双向绑定到 SubjectConfig，这里只需要请求保存并让主窗口重建卡片。
        /// 主窗口的重建通过 ReloadFromDisk 会丢掉内存修改，所以改用直接刷新方式。
        /// </remarks>
        private void SubjectCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            try
            {
                _viewModel.RequestSave();

                // 通知主窗口重新构建卡片列表：这里用一个轻量方法
                _viewModel.RebuildCardsPublic();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 打开程序文件夹。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                if (!Directory.Exists(dir)) return;
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + dir + "\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 立即保存全部数据。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void BtnSaveNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.SaveAllNow();
                MessageBox.Show(this, _viewModel.StatusText, "保存结果",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 从磁盘重新载入配置。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>载入后需要把新值刷回控件，因此重新走一次 LoadCurrentValues。</remarks>
        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.ReloadFromDisk();

                _isInitializing = true;
                LoadCurrentValues();
                _isInitializing = false;
            }
            catch
            {
                _isInitializing = false;
            }
        }

        /// <summary>
        /// 关闭按钮。
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
        /// 标题栏拖拽移动窗口。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">鼠标事件参数</param>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != MouseButton.Left) return;
                if (e.OriginalSource is Button) return;
                this.DragMove();
            }
            catch
            {
                // 忽略
            }
        }
    }
}
