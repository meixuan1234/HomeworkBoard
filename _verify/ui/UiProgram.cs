using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HomeworkBoard.Core;
using HomeworkBoard.ViewModels;
using HomeworkBoard.Views;

namespace HomeworkBoard.UiVerify
{
    /// <summary>
    /// 界面层验证程序。
    /// 
    /// 存在意义：
    /// 数据层自检（Verify.csproj）能证明 vm.Rows 里确实有 10 个科目，
    /// 但**证明不了这 10 个科目会真的出现在界面上**。
    /// 本程序真的把 MainWindow 建起来、跑一遍布局、遍历可视树数卡片，
    /// 是「窗口能看见但没有卡片」这类 bug 唯一可靠的回归测试。
    /// 
    /// 用法：dotnet run（退出码 0 = 全部通过，1 = 有失败项）
    /// </summary>
    internal static class UiProgram
    {
        /// <summary>断言通过计数。</summary>
        private static int _passCount;

        /// <summary>断言失败计数。</summary>
        private static int _failCount;

        /// <summary>收集的诊断信息（在 UI 线程外打印，避免 Console 与 WPF 抢线程）。</summary>
        private static readonly List<string> _lines = new List<string>();

        /// <summary>
        /// 程序入口。
        /// </summary>
        /// <param name="args">命令行参数（未使用）</param>
        /// <returns>退出码：0 = 全部通过，1 = 存在失败</returns>
        /// <remarks>
        /// WPF 控件必须在 STA 线程上创建，因此这里手动起一个 STA 线程，
        /// 不用 [STAThread] 特性（那样需要 Application.Run，会一直不返回）。
        /// </remarks>
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            int exitCode = 1;
            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                try
                {
                    exitCode = RunOnUiThread();
                }
                catch (Exception ex)
                {
                    _lines.Add("界面验证过程抛出异常：" + ex);
                    exitCode = 1;
                }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            t.Join();

            // 统一输出（此时 UI 线程已结束，不会有线程冲突）
            foreach (string line in _lines)
            {
                Console.WriteLine(line);
            }
            Console.WriteLine();
            Console.WriteLine("-------------------------------------------");
            Console.WriteLine("通过 " + _passCount + " 项，失败 " + _failCount + " 项");
            Console.WriteLine(_failCount == 0 ? "结果：全部通过" : "结果：存在失败项");

            // 同时落一份到文件：PowerShell 重定向 stdout 在本机不可靠（曾出现 0 字节），
            // 写文件再读取是唯一稳定的取结果方式。
            try
            {
                string logPath = Path.Combine(AppContext.BaseDirectory, "ui_verify_result.txt");
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (string line in _lines) sb.AppendLine(line);
                sb.AppendLine();
                sb.AppendLine("-------------------------------------------");
                sb.AppendLine("通过 " + _passCount + " 项，失败 " + _failCount + " 项");
                sb.AppendLine(_failCount == 0 ? "结果：全部通过" : "结果：存在失败项");
                File.WriteAllText(logPath, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // 落盘失败不影响退出码判定
            }

            return exitCode;
        }

        /// <summary>
        /// 在 STA 线程上执行真正的界面构建与断言。
        /// </summary>
        /// <returns>退出码</returns>
        private static int RunOnUiThread()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "hb_uiverify_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(testDir);

            try
            {
                Log("=== 作业可视化悬浮窗 · 界面层验证 ===");
                Log("临时目录：" + testDir);
                Log();

                // ------------------------------------------------------------
                // 关键前置：MainWindow 的 XAML 用了 {StaticResource ...} 引用 DarkTheme.xaml。
                // 这些资源在真实程序里由 App.xaml 的 MergedDictionaries 提供。
                // 本验证程序没有跑 App.OnStartup，因此必须手动把主题装进
                // Application.Current.Resources，否则 FindResource 会抛异常，
                // 表现为「界面全空白」——那会是验证程序的 bug 而不是产品的 bug。
                // ------------------------------------------------------------
                EnsureApplicationWithTheme();

                JsonStore store = new JsonStore(testDir);
                store.LoadSettings();
                store.LoadHomework();

                // ---------- 1. 建窗口 ----------
                Log("[1] 构建 MainWindow");
                MainWindow win = new MainWindow(store);

                // ---------- 2. 真实显示并排布 ----------
                // 关键：只调 Measure/Arrange 是不够的——窗口没真正 Show 时，
                // WPF 不会跑完整的窗口初始化与可视树构建流程（实测 ActualWidth 恒为 0，
                // 可视树里一个 ItemsControl 都找不到，产生假阴性）。
                // 因此必须真 Show()，但把它挪到屏幕外 + 不占任务栏 + 不抢焦点，
                // 用户完全无感。
                Log("[2] 真实显示（屏幕外，不干扰用户）");
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.ShowInTaskbar = false;
                win.ShowActivated = false;
                win.Left = -30000;
                win.Top = -30000;
                win.Show();

                // 泵两轮 Dispatcher，让绑定求值与 DataTemplate 实例化跑完
                Pump();
                win.UpdateLayout();
                Pump();

                Log("     窗口 ActualWidth = " + win.ActualWidth.ToString("F1")
                    + "  ActualHeight = " + win.ActualHeight.ToString("F1"));

                // ---------- 3. 找到内容区的 ItemsControl ----------
                Log();
                Log("[3] 定位内容区并统计渲染出的卡片");
                MainViewModel vm = win.DataContext as MainViewModel;
                Assert("窗口 DataContext 是 MainViewModel", vm != null);
                if (vm == null)
                {
                    return Finish(testDir);
                }

                Assert("vm.Rows 不为 null", vm.Rows != null);
                Assert("vm.Rows 行数 >= 1", vm.Rows != null && vm.Rows.Count >= 1);
                if (vm.Rows != null)
                {
                    Log("     vm.Rows.Count = " + vm.Rows.Count);
                }

                // 可视树里找所有 ItemsControl
                List<ItemsControl> allItemsControls = new List<ItemsControl>();
                CollectVisuals(win, allItemsControls);

                // 找到「承载 Rows 的那个」ItemsControl：它的 ItemsSource 就是 vm.Rows
                ItemsControl rowsControl = null;
                foreach (ItemsControl ic in allItemsControls)
                {
                    if (ic.ItemsSource == vm.Rows)
                    {
                        rowsControl = ic;
                        break;
                    }
                }
                Assert("可视树中找到了绑定 Rows 的 ItemsControl", rowsControl != null);
                if (rowsControl == null)
                {
                    Log("     !! 可视树里共 " + allItemsControls.Count + " 个 ItemsControl");
                    return Finish(testDir);
                }

                // ---------- 4. 核心断言：行控件的子项元素 ----------
                Log();
                Log("[4] 核心断言：实际渲染出的行元素");
                int renderedRows = VisualTreeHelper.GetChildrenCount(rowsControl);
                Log("     渲染出的行元素数 = " + renderedRows);

                // ItemContainerGenerator 更准确（ItemsControl 的子项是容器，不是内容）
                int containerCount = rowsControl.Items.Count;
                Log("     ItemsControl.Items.Count = " + containerCount);

                Assert("ItemsControl 拿到了 Items（>0）", containerCount > 0);
                Assert("行元素已实际生成（ItemContainerGenerator 完成）",
                       rowsControl.ItemContainerGenerator.ContainerFromIndex(0) != null);

                // ---------- 5. 逐层往下数卡片 ----------
                Log();
                Log("[5] 逐层统计最终渲染出的科目卡片 Border 数量");
                int cardCount = CountRenderedCards(rowsControl);
                Log("     实际渲染出的卡片数 = " + cardCount);
                Log("     期望卡片数（= 启用科目数）= " + (vm.AllCards == null ? -1 : vm.AllCards.Count));

                // 这是本验证程序存在的唯一理由：
                // 用户报告「窗口能看见但没有卡片」，就是这里应该 > 0 却等于 0。
                Assert("界面上真实渲染出了卡片", cardCount > 0);
                Assert("渲染出的卡片数等于启用科目数",
                       vm.AllCards != null && cardCount == vm.AllCards.Count);

                // ---------- 6. 每张卡片都能找到科目名 TextBlock ----------
                Log();
                Log("[6] 卡片内文字是否真的渲染出来");
                List<TextBlock> texts = new List<TextBlock>();
                CollectVisuals(rowsControl, texts);
                int nonEmptyTexts = 0;
                foreach (TextBlock tb in texts)
                {
                    if (!string.IsNullOrEmpty(tb.Text)) nonEmptyTexts++;
                }
                Log("     TextBlock 总数 = " + texts.Count + "，非空 = " + nonEmptyTexts);
                Assert("卡片区域内有非空文本", nonEmptyTexts > 0);

                // 抽查：语文 / 数学 这两个科目名应能出现在某处
                bool hasChinese = false, hasMath = false;
                foreach (TextBlock tb in texts)
                {
                    if (tb.Text == "语文") hasChinese = true;
                    if (tb.Text == "数学") hasMath = true;
                }
                Assert("界面上能找到「语文」标题", hasChinese);
                Assert("界面上能找到「数学」标题", hasMath);

                // ---------- 6.5 高度控制：窗口高度上限 + 空卡片折叠 ----------
                Log();
                Log("[6.5] 高度控制（本次改动）");

                // (a) MaxWindowHeight 必须不超过显示器工作区高度
                //     否则窗口会长到任务栏下面（用户反馈的原始问题）
                double maxH = vm.MaxWindowHeight;
                double workH = 0;
                try
                {
                    HomeworkBoard.Core.ScreenHelper.WorkArea wa =
                        HomeworkBoard.Core.ScreenHelper.GetPrimaryWorkArea();
                    workH = wa.Height / 1.0; // 本机 DPI 100% 时物理=逻辑
                }
                catch { }
                Log("     vm.MaxWindowHeight = " + maxH.ToString("F0"));
                Log("     主显示器工作区高度（物理像素）= " + workH.ToString("F0"));
                Assert("MaxWindowHeight 为有效正数", maxH > 100);
                Assert("MaxWindowHeight 不超过工作区高度（不会压到任务栏）",
                       workH <= 0 || maxH <= workH);

                // (b) 窗口实际高度不应超过 MaxWindowHeight
                Log("     窗口实际高度 = " + win.ActualHeight.ToString("F1"));
                Assert("窗口实际高度 <= MaxWindowHeight + 留白",
                       win.ActualHeight <= maxH + 40);

                // (c) 空卡片折叠：空科目卡片必须明显矮于有作业的卡片
                double emptyCardH = MeasureFirstCardHeight(rowsControl, true);
                Log("     空科目卡片高度 = " + emptyCardH.ToString("F1"));

                // 给第一个科目加几条作业后，测有作业卡片的高度
                string keyForHeight = (vm.AllCards != null && vm.AllCards.Count > 0) ? vm.AllCards[0].Key : null;
                double filledCardH = 0;
                if (keyForHeight != null)
                {
                    vm.AddItem(keyForHeight, "高度测试条目 1");
                    vm.AddItem(keyForHeight, "高度测试条目 2");
                    win.UpdateLayout();
                    Pump();
                    filledCardH = MeasureFirstCardHeight(rowsControl, false);
                    Log("     有作业卡片高度（2 条）= " + filledCardH.ToString("F1"));
                    vm.RemoveItem(vm.AllCards[0].Items[0]);
                    vm.RemoveItem(vm.AllCards[0].Items[0]);
                    win.UpdateLayout();
                    Pump();
                }
                Assert("空科目卡片高度已测得", emptyCardH > 0);
                Assert("有作业卡片高于空科目卡片（折叠生效）",
                       filledCardH <= 0 || emptyCardH < filledCardH);

                // (d) 空卡片高度应落在紧凑范围内（经验值：60—110 逻辑像素）
                Assert("空科目卡片高度在紧凑区间（<=110）", emptyCardH > 0 && emptyCardH <= 110);

                // (e) 设置里改高度后，窗口应跟着变（SetMaxHeight 的运行时行为）
                vm.SetMaxHeight(500);
                win.UpdateLayout();
                Pump();
                double afterSmall = win.ActualHeight;
                Log("     SetMaxHeight(500) 后窗口高度 = " + afterSmall.ToString("F1"));
                Assert("调低最大高度后窗口确实变矮", afterSmall < 720);

                vm.SetMaxHeight(720);
                win.UpdateLayout();
                Pump();
                double afterRestore = win.ActualHeight;
                Log("     SetMaxHeight(720) 后窗口高度 = " + afterRestore.ToString("F1"));

                // 注意：提高上限**不应该**主动把窗口拉高。
                // 窗口高度是用户/配置设定的值，上限只做约束不做驱动——
                // 若"提高上限就自动长高"，用户会把最大高度调回 720 时窗口莫名变大，属于意外行为。
                // 因此这里断言的是「窗口保持在被约束后的高度」，而不是"恢复到 720"。
                Assert("提高上限不会主动把窗口拉高（高度保持不变）",
                       Math.Abs(afterRestore - afterSmall) < 2);

                // 上限真正生效的方向是"调低时把超限的窗口压下来"，用下面的显式设置来验证
                vm.RequestWindowSize(0, 700);
                win.UpdateLayout();
                Pump();
                double beforeCap = win.ActualHeight;
                Log("     设为 700 后窗口高度 = " + beforeCap.ToString("F1"));

                vm.SetMaxHeight(400);
                win.UpdateLayout();
                Pump();
                double afterCap = win.ActualHeight;
                Log("     SetMaxHeight(400) 后窗口高度 = " + afterCap.ToString("F1"));
                Assert("调低上限会把超限的窗口压下来", afterCap < beforeCap - 20);

                // (f) 标题栏高度必须紧凑。
                //     回归点：左侧信息块若用 VerticalAlignment=Top 与右侧 34px 按钮栈顶端齐平，
                //     文字下方会留出一大块空白，标题栏被撑到 60px 以上，
                //     这是用户反馈的"日期那一栏过高"。
                FrameworkElement titleBar = win.FindName("TitleBar") as FrameworkElement;
                double titleBarH = titleBar != null ? titleBar.ActualHeight : 0;
                Log("     标题栏实际高度 = " + titleBarH.ToString("F1"));
                Assert("标题栏能被找到", titleBar != null);

                // 诊断：把标题栏内每个子元素的高度打出来，定位撑高来源
                if (titleBar != null)
                {
                    Log("     标题栏 Padding = " + titleBar.Margin + " / " + titleBar.GetType().Name);
                    Border tbBorder = titleBar as Border;
                    if (tbBorder != null)
                    {
                        Log("     标题栏 Padding 实际 = " + tbBorder.Padding);
                        FrameworkElement childFe = tbBorder.Child as FrameworkElement;
                        Log("     标题栏 Child 高度 = "
                            + (childFe != null ? childFe.ActualHeight.ToString("F1") : "null"));
                        Grid g = tbBorder.Child as Grid;
                        if (g != null)
                        {
                            Log("     TitleBar 内 Grid 高度 = " + g.ActualHeight.ToString("F1"));
                            for (int i = 0; i < g.Children.Count; i++)
                            {
                                UIElement rawChild = g.Children[i];
                                FrameworkElement ch = rawChild as FrameworkElement;
                                if (ch == null) continue;
                                string align = "";
                                if (ch is StackPanel)
                                {
                                    align = " align=" + ((StackPanel)ch).VerticalAlignment
                                          + " orient=" + ((StackPanel)ch).Orientation;
                                }
                                Log("       child[" + i + "] " + ch.GetType().Name
                                    + " h=" + ch.ActualHeight.ToString("F1")
                                    + " desired=" + ch.DesiredSize.Height.ToString("F1")
                                    + align);
                            }
                        }
                    }
                }

                Assert("标题栏高度紧凑（<= 52 逻辑像素）", titleBarH > 0 && titleBarH <= 52);

                // (g) 设置面板调窗口宽/高后，窗口必须实时跟着变。
                //     回归点：视图模型不持有 Window 引用，靠 WindowSizeRequested 事件通知。
                //     若忘记订阅，滑块就只改 JSON、界面毫无反应 —— 这正是用户反馈的问题。
                // 先把上限恢复正常，否则下面"调小高度"会被上限提前压住，测不出实时响应。
                vm.SetMaxHeight(1400);
                vm.RequestWindowSize(380, 720);
                win.UpdateLayout();
                Pump();
                vm.SetMaxHeight(720);
                win.UpdateLayout();
                Pump();

                double beforeW = win.ActualWidth;
                double beforeH = win.ActualHeight;
                Log("     起始尺寸 = " + beforeW.ToString("F1") + " x " + beforeH.ToString("F1"));

                vm.RequestWindowSize(700, 0);
                win.UpdateLayout();
                Pump();
                Log("     RequestWindowSize(700, 0) 后宽度 = " + win.ActualWidth.ToString("F1"));
                Assert("调宽度后窗口确实变宽", win.ActualWidth > beforeW + 50);

                vm.RequestWindowSize(0, 400);
                win.UpdateLayout();
                Pump();
                Log("     RequestWindowSize(0, 400) 后高度 = " + win.ActualHeight.ToString("F1"));
                Assert("调高度后窗口确实变矮", win.ActualHeight < beforeH - 50);

                // 宽度不能小于下限
                vm.RequestWindowSize(50, 0);
                win.UpdateLayout();
                Pump();
                Log("     RequestWindowSize(50, 0) 后宽度 = " + win.ActualWidth.ToString("F1"));
                Assert("宽度被钳制在 MinWidth 之上（>= 280）", win.ActualWidth >= 280 - 1);

                // 高度不能超过 MaxWindowHeight
                vm.RequestWindowSize(0, 5000);
                win.UpdateLayout();
                Pump();
                Log("     RequestWindowSize(0, 5000) 后高度 = " + win.ActualHeight.ToString("F1"));
                Assert("高度被钳制在 MaxWindowHeight 之下",
                       win.ActualHeight <= vm.MaxWindowHeight + 40);

                // 恢复一个正常尺寸，避免影响后续断言
                vm.RequestWindowSize(380, 720);
                win.UpdateLayout();
                Pump();

                // ---------- 7. 添加作业后卡片应重排且条数变化 ----------
                Log();
                Log("[7] 添加作业后界面应同步刷新");
                string firstKey = null;
                if (vm.AllCards != null && vm.AllCards.Count > 0)
                {
                    firstKey = vm.AllCards[0].Key;
                }
                if (firstKey != null)
                {
                    vm.AddItem(firstKey, "UI 验证用作业条目");
                    win.UpdateLayout();
                    Pump();

                    List<TextBlock> texts2 = new List<TextBlock>();
                    CollectVisuals(rowsControl, texts2);
                    bool hasItemText = false;
                    foreach (TextBlock tb in texts2)
                    {
                        if (tb.Text != null && tb.Text.IndexOf("UI 验证用作业条目", StringComparison.Ordinal) >= 0)
                        {
                            hasItemText = true;
                            break;
                        }
                    }
                    Assert("新加的作业条目文字出现在界面上", hasItemText);
                }

                // ---------- 8. 窗口关闭不崩溃 ----------
                Log();
                Log("[8] 关闭窗口（确保清理逻辑不抛异常）");
                try
                {
                    win.Close();
                    Assert("窗口可以正常关闭", true);
                }
                catch (Exception ex)
                {
                    Assert("窗口可以正常关闭（异常：" + ex.GetType().Name + "）", false);
                }
            }
            catch (Exception ex)
            {
                Log();
                Log("!! 界面验证异常：" + ex);
                _failCount++;
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }

            return _failCount == 0 ? 0 : 1;
        }

        /// <summary>
        /// 结束并返回退出码。
        /// </summary>
        /// <param name="testDir">临时目录</param>
        /// <returns>退出码</returns>
        private static int Finish(string testDir)
        {
            try { Directory.Delete(testDir, true); } catch { }
            return _failCount == 0 ? 0 : 1;
        }

        /// <summary>
        /// 保证 Application.Current 存在，并把 DarkTheme.xaml 合并进资源字典。
        /// </summary>
        /// <remarks>
        /// 这是本验证程序最关键的环境准备：
        /// MainWindow.xaml 大量使用 {StaticResource AppFontFamily} / {StaticResource BrushXxx} 等，
        /// 这些键都在 Themes/DarkTheme.xaml 里。真实运行时由 App.xaml 合并，
        /// 此处必须手动合并，否则 XAML 加载会直接抛 XamlParseException。
        /// 
        /// 踩过的坑（务必保留这段说明，否则下次还会踩）：
        /// 用 XamlReader.Load(FileStream) 直接读源码 .xaml 是**行不通**的——
        /// 该重载运行在「宽松模式」，它无法解析 XAML 里的 clr-namespace 类型引用，
        /// 会报「无法创建未知类型 {clr-namespace:HomeworkBoard.Converters}HexToBrushConverter」。
        /// 正确做法是用 Application.LoadComponent + 相对 URI，走 BAML 路径，
        /// 类型引用由编译期生成的 BAML 记录承载，可以正确解析。
        /// </remarks>
        private static void EnsureApplicationWithTheme()
        {
            if (Application.Current == null)
            {
                // 只创建 Application 实例，不调用 Run()，因此不会进入消息循环
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            // 若已合并过则不重复（避免重复键异常）
            if (Application.Current.Resources.MergedDictionaries.Count > 0)
            {
                return;
            }

            // 资源键实测为 "Themes/DarkTheme.xaml"（在 UiVerify.g.resources 里扫出来的），
            // 注意是 .xaml 后缀而非 .baml，且大小写敏感。
            Exception last = null;
            string[] candidates = new string[]
            {
                "pack://application:,,,/UiVerify;component/Themes/DarkTheme.xaml",
                "/UiVerify;component/Themes/DarkTheme.xaml"
            };

            foreach (string uriText in candidates)
            {
                try
                {
                    ResourceDictionary rd = new ResourceDictionary();
                    rd.Source = new Uri(uriText, UriKind.RelativeOrAbsolute);
                    // 主动触发实际加载：访问 Count 会立刻解析 Source，失败即抛出
                    int probe = rd.Count;
                    _ = probe;
                    // 还要确认里面真的有 AppFontFamily，避免"加载成功但内容不对"
                    if (!rd.Contains("AppFontFamily"))
                    {
                        last = new InvalidOperationException("资源字典里没有 AppFontFamily 键");
                        continue;
                    }
                    Application.Current.Resources.MergedDictionaries.Add(rd);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw new InvalidOperationException(
                "无法加载主题资源 Themes/DarkTheme.xaml。验证程序无法继续——" +
                "这是验证工程的环境问题，不是产品 bug。最后一次错误：" +
                (last == null ? "(无)" : last.GetType().Name + " " + last.Message));
        }

        /// <summary>
        /// 泵一次 Dispatcher 队列，让绑定、布局、模板生成等低优先级任务完成。
        /// </summary>
        /// <remarks>
        /// 关键：WPF 的数据绑定求值与 DataTemplate 实例化通常排在 DispatcherPriority.DataBind
        /// 或更低优先级上。不泵队列的话，Measure/Arrange 之后可视树里可能还是空的，
        /// 会产生「明明有数据却数不到卡片」的假阴性。
        /// </remarks>
        private static void Pump()
        {
            try
            {
                DispatcherFrame frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.ContextIdle,
                    new DispatcherOperationCallback(delegate (object arg)
                    {
                        frame.Continue = false;
                        return null;
                    }),
                    null);
                Dispatcher.PushFrame(frame);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 递归收集可视树中指定类型的所有元素。
        /// </summary>
        /// <typeparam name="T">目标元素类型</typeparam>
        /// <param name="root">起始元素</param>
        /// <param name="result">结果收集列表</param>
        private static void CollectVisuals<T>(DependencyObject root, List<T> result) where T : DependencyObject
        {
            if (root == null || result == null) return;
            try
            {
                int n = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < n; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(root, i);
                    T typed = child as T;
                    if (typed != null) result.Add(typed);
                    CollectVisuals<T>(child, result);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 测量第一列中指定状态的首张科目卡片的实际渲染高度。
        /// </summary>
        /// <param name="rowsControl">绑定 Rows 的 ItemsControl</param>
        /// <param name="wantEmpty">true = 找空卡片；false = 找有作业的卡片</param>
        /// <returns>卡片高度（逻辑像素）；找不到时返回 0</returns>
        /// <remarks>
        /// 用途：验证「空科目折叠」是否真的生效——空卡片必须比有作业的卡片矮。
        /// 只做数值比较而不写死期望值，因为字号、DPI 都会影响绝对值，
        /// 写死数字会让测试变脆（换个字号就挂）。
        /// </remarks>
        private static double MeasureFirstCardHeight(ItemsControl rowsControl, bool wantEmpty)
        {
            try
            {
                int colCount = rowsControl.Items.Count;
                for (int i = 0; i < colCount; i++)
                {
                    DependencyObject container = rowsControl.ItemContainerGenerator.ContainerFromIndex(i);
                    if (container == null) continue;

                    // 该列里的 Cards ItemsControl
                    List<ItemsControl> inner = new List<ItemsControl>();
                    CollectVisuals(container, inner);
                    foreach (ItemsControl ic in inner)
                    {
                        if (ic == rowsControl) continue;
                        if (ic.Items.Count == 0) continue;
                        if (!(ic.Items[0] is SubjectCardViewModel)) continue;

                        // 遍历该列的卡片容器，找第一个状态匹配的
                        for (int j = 0; j < ic.Items.Count; j++)
                        {
                            SubjectCardViewModel cardVm = ic.Items[j] as SubjectCardViewModel;
                            if (cardVm == null) continue;
                            if (wantEmpty != cardVm.IsEmpty) continue;

                            DependencyObject cardContainer = ic.ItemContainerGenerator.ContainerFromIndex(j);
                            if (cardContainer == null) continue;
                            FrameworkElement fe = cardContainer as FrameworkElement;
                            if (fe == null) continue;
                            if (fe.ActualHeight > 1) return fe.ActualHeight;
                            // ContentPresenter 有时高度为 0，退一步取它的第一个可视子元素
                            if (VisualTreeHelper.GetChildrenCount(fe) > 0)
                            {
                                FrameworkElement child =
                                    VisualTreeHelper.GetChild(fe, 0) as FrameworkElement;
                                if (child != null && child.ActualHeight > 1) return child.ActualHeight;
                            }
                        }
                        break;
                    }
                }
            }
            catch
            {
                // 忽略
            }
            return 0;
        }

        /// <summary>
        /// 统计内容区最终渲染出的科目卡片数量。
        /// </summary>
        /// <param name="rowsControl">绑定 Rows 的 ItemsControl</param>
        /// <returns>实际渲染出的卡片 Border 数量</returns>
        /// <remarks>
        /// 判定方式：卡片最外层是一个带 CornerRadius=10 且 Margin="0,0,0,10" 的 Border。
        /// 更稳妥的做法是数 ItemContainerGenerator 的容器，
        /// 但 ItemsControl 默认容器是 ContentPresenter，不便于辨别层级，
        /// 因此这里用「逐层下钻 + 数 Cards ItemsControl 的子元素」的方式：
        /// 先取第 0 列，再在列里找绑定 col.Cards 的 ItemsControl，数它的容器数。
        /// </remarks>
        private static int CountRenderedCards(ItemsControl rowsControl)
        {
            int total = 0;
            try
            {
                int colCount = rowsControl.Items.Count;
                for (int i = 0; i < colCount; i++)
                {
                    DependencyObject container = rowsControl.ItemContainerGenerator.ContainerFromIndex(i);
                    if (container == null) continue;

                    // 该列里的 Cards ItemsControl
                    List<ItemsControl> inner = new List<ItemsControl>();
                    CollectVisuals(container, inner);
                    foreach (ItemsControl ic in inner)
                    {
                        if (ic == rowsControl) continue;
                        // Cards 是 ObservableCollection<SubjectCardViewModel>，
                        // 其 ItemsSource 的项类型即为 SubjectCardViewModel
                        if (ic.Items.Count > 0 && ic.Items[0] is SubjectCardViewModel)
                        {
                            total += ic.Items.Count;
                            break; // 每列只有一个 Cards 控件
                        }
                    }
                }
            }
            catch
            {
                // 忽略
            }
            return total;
        }

        /// <summary>
        /// 记录一行输出。
        /// </summary>
        /// <param name="text">文本内容</param>
        private static void Log(string text)
        {
            _lines.Add(text == null ? string.Empty : text);
        }

        /// <summary>
        /// 记录一个空行（分隔用）。
        /// </summary>
        /// <remarks>XAML 编译要求重载明确，因此单列一个无参版本而不是给 text 设默认值。</remarks>
        private static void Log()
        {
            _lines.Add(string.Empty);
        }

        /// <summary>
        /// 断言辅助方法。
        /// </summary>
        /// <param name="name">断言描述</param>
        /// <param name="condition">断言条件</param>
        /// <remarks>失败不中断，一次运行看到全部问题。</remarks>
        private static void Assert(string name, bool condition)
        {
            if (condition)
            {
                _passCount++;
                Log("  [通过] " + name);
            }
            else
            {
                _failCount++;
                Log("  [失败] " + name);
            }
        }
    }
}
