using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HomeworkBoard.Models;
using HomeworkBoard.ViewModels;

namespace HomeworkBoard.Views
{
    /// <summary>
    /// 作业编辑对话框（新增 / 修改共用）。
    /// 
    /// 设计要点：
    /// - 提供模板快选胶囊按钮，点一下把「练习册第 ___ 页」这类骨架填进输入框，
    ///   并把光标停在需要填数字的位置，减少打字量；
    /// - 回车确定、Esc 取消，键盘操作顺手；
    /// - 输入为空时不允许确定，避免产生无意义的空条目。
    /// </summary>
    public partial class EditItemWindow : Window
    {
        private readonly MainViewModel _viewModel;

        /// <summary>已保存的结果文本。仅在 DialogResult 为 true 时有效。</summary>
        public string ResultContent { get; private set; }

        /// <summary>
        /// 构造编辑对话框。
        /// </summary>
        /// <param name="viewModel">主视图模型，用于取模板列表，不可为 null</param>
        /// <param name="subjectName">科目显示名，显示在副标题上</param>
        /// <param name="initialContent">初始内容。传 null 表示新增模式</param>
        /// <remarks>
        /// initialContent 为 null 或空 -> 标题显示「添加作业」，输入框留空；
        /// 否则显示「编辑作业」并填入原内容。
        /// </remarks>
        public EditItemWindow(MainViewModel viewModel, string subjectName, string initialContent)
        {
            InitializeComponent();

            _viewModel = viewModel;
            ResultContent = string.Empty;

            bool isEdit = !string.IsNullOrEmpty(initialContent);

            TitleText.Text = isEdit ? "编辑作业" : "添加作业";
            SubtitleText.Text = "科目：" + (string.IsNullOrWhiteSpace(subjectName) ? "(未知)" : subjectName);

            // 填充模板胶囊
            try
            {
                if (_viewModel != null)
                {
                    TemplateList.ItemsSource = _viewModel.EnabledTemplates;
                }
            }
            catch
            {
                // 模板加载失败不影响手工输入
            }

            if (isEdit)
            {
                ContentBox.Text = initialContent;
            }

            // 焦点落在输入框，启动即可直接打字
            this.Loaded += delegate
            {
                try
                {
                    ContentBox.Focus();
                    ContentBox.CaretIndex = ContentBox.Text.Length;
                }
                catch
                {
                    // 忽略
                }
            };
        }

        /// <summary>
        /// 模板胶囊点击：把模板骨架填入输入框，并把光标放在 prefix 与 suffix 之间。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        /// <remarks>
        /// 例如模板为「练习册第 」+「 页」，填入后输入框显示「练习册第  页」，
        /// 光标停在中间，同学直接按数字键就能补成「练习册第 12 页」。
        /// 不覆盖用户已输入的文字——采取追加策略，避免手滑丢内容。
        /// </remarks>
        private void TemplateChip_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var btn = sender as Button;
                if (btn == null) return;
                var tpl = btn.Tag as TemplateConfig;
                if (tpl == null) return;

                string prefix = tpl.Prefix ?? string.Empty;
                string suffix = tpl.Suffix ?? string.Empty;
                string insert = prefix + suffix;

                // 已有内容时先补一个分隔符，避免两句黏在一起
                string existing = ContentBox.Text ?? string.Empty;
                if (existing.Length > 0 && !existing.EndsWith("；") && !existing.EndsWith(";")
                    && !existing.EndsWith("，") && !existing.EndsWith(",") && !existing.EndsWith(" "))
                {
                    insert = "；" + insert;
                }

                int baseLen = existing.Length;
                ContentBox.Text = existing + insert;

                // 光标定位到 prefix 结束处（即需要填数字的位置）
                int caret = baseLen + ((insert.StartsWith("；") ? 1 : 0)) + prefix.Length;
                ContentBox.CaretIndex = caret;
                ContentBox.Focus();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 输入框按键：回车确定，Esc 取消。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">键盘事件参数</param>
        private void ContentBox_KeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    ConfirmAndClose();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CancelAndClose();
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 确定按钮：校验并关闭。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            ConfirmAndClose();
        }

        /// <summary>
        /// 取消按钮：直接关闭。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">事件参数</param>
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CancelAndClose();
        }

        /// <summary>
        /// 标题栏拖拽：按住标题区域可移动对话框。
        /// </summary>
        /// <param name="sender">事件源</param>
        /// <param name="e">鼠标事件参数</param>
        /// <remarks>无边框窗口需要自己实现，否则对话框无法移动。</remarks>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != MouseButton.Left) return;
                // 点在按钮上就不拖
                if (e.OriginalSource is Button) return;
                this.DragMove();
            }
            catch
            {
                // DragMove 在极少数情况下会抛异常（鼠标已释放），忽略即可
            }
        }

        /// <summary>
        /// 校验输入并确认关闭。
        /// </summary>
        /// <remarks>
        /// 内容为空时用状态方式提示（把提示文字写到副标题），不弹 MessageBox 打断操作。
        /// 注意：不 Trim 内部空格，只判断整体是否全空白，保留用户排版习惯。
        /// </remarks>
        private void ConfirmAndClose()
        {
            try
            {
                string text = ContentBox.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    SubtitleText.Text = "内容不能为空，请填写后再确定。";
                    SubtitleText.Foreground = (System.Windows.Media.Brush)FindResource("BrushWarning");
                    ContentBox.Focus();
                    return;
                }

                ResultContent = text;
                this.DialogResult = true;
                this.Close();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 取消并关闭。
        /// </summary>
        /// <remarks>不抛异常。</remarks>
        private void CancelAndClose()
        {
            try
            {
                this.DialogResult = false;
                this.Close();
            }
            catch
            {
                // 忽略
            }
        }
    }
}
