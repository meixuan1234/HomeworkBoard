using System;
using System.Collections.Generic;
using System.IO;
using HomeworkBoard.Core;
using HomeworkBoard.Models;
using HomeworkBoard.ViewModels;

namespace HomeworkBoard.Verify
{
    /// <summary>
    /// 视图模型自检程序。
    /// 
    /// 存在意义：界面 bug（如「窗口能看见但没有卡片」）靠肉眼观察效率极低，
    /// 且无法区分「数据没准备好」和「绑定没生效」。
    /// 本程序直接实例化 MainViewModel，断言关键集合的状态，
    /// 把问题定位在数据层还是界面层。
    /// 
    /// 用法：dotnet run（退出码 0 = 全部通过，1 = 有失败项）
    /// </summary>
    internal static class Program
    {
        /// <summary>断言失败计数。</summary>
        private static int _failCount;

        /// <summary>断言通过计数。</summary>
        private static int _passCount;

        /// <summary>
        /// 程序入口。
        /// </summary>
        /// <param name="args">命令行参数（未使用）</param>
        /// <returns>退出码：0 = 全部通过，1 = 存在失败</returns>
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // 用独立临时目录，避免污染真实程序目录
            string testDir = Path.Combine(Path.GetTempPath(), "hb_verify_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(testDir);

            try
            {
                Console.WriteLine("=== 作业可视化悬浮窗 · 视图模型自检 ===");
                Console.WriteLine("临时目录：" + testDir);
                Console.WriteLine();

                RunTests(testDir);

                Console.WriteLine();
                Console.WriteLine("-------------------------------------------");
                Console.WriteLine("通过 " + _passCount + " 项，失败 " + _failCount + " 项");
                Console.WriteLine(_failCount == 0 ? "结果：全部通过" : "结果：存在失败项");
                return _failCount == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("自检过程抛出异常：" + ex);
                return 1;
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        /// <summary>
        /// 执行全部检查项。
        /// </summary>
        /// <param name="testDir">测试用目录</param>
        private static void RunTests(string testDir)
        {
            // ---------- 1. 首次运行：配置文件应被自动生成 ----------
            Console.WriteLine("[1] 首次运行，自动生成配置文件");
            JsonStore store = new JsonStore(testDir);
            Assert("目录被判定为可写", store.IsWritable);

            // 说明：JsonStore 构造函数只做路径准备与写权限探测（<1ms），
            // 真正的落盘发生在 LoadSettings/LoadHomework 里。
            // 这与真实程序流程一致（MainViewModel 构造时先 Load 再 Rebuild）。
            Assert("构造后尚未落盘（设计如此，构造函数不做 IO）", !File.Exists(store.SettingsPath));

            store.LoadSettings();
            store.LoadHomework();
            Assert("LoadSettings 后 settings.json 已生成", File.Exists(store.SettingsPath));
            Assert("LoadHomework 后 homework.json 已生成", File.Exists(store.HomeworkPath));

            // ---------- 2. 视图模型构造后，关键集合必须非 null 且已填充 ----------
            Console.WriteLine();
            Console.WriteLine("[2] 构造 MainViewModel 后的集合状态");
            MainViewModel vm = new MainViewModel(store);

            // 这是本次修复的核心断言：Rows 若为 null，界面内容区会完全空白
            Assert("Rows 不为 null", vm.Rows != null);
            Assert("AllCards 不为 null", vm.AllCards != null);
            Assert("默认配置有 10 个科目", vm.AllCards.Count == 10);
            Console.WriteLine("     实际科目数：" + vm.AllCards.Count);

            // ---------- 3. 空科目应常驻显示（本次需求变更的核心） ----------
            Console.WriteLine();
            Console.WriteLine("[3] 空科目常驻显示（经用户确认的偏离需求）");
            Assert("Rows 至少有 1 行", vm.Rows.Count >= 1);
            Console.WriteLine("     实际行数：" + vm.Rows.Count);

            int cardsInRows = 0;
            foreach (RowViewModel row in vm.Rows)
            {
                cardsInRows += row.Cards.Count;
            }
            Console.WriteLine("     行中卡片总数：" + cardsInRows);

            // 原本「没有作业的学科不显示」会把这里变成 0，正是用户遇到的现象
            Assert("行中包含全部 10 个科目（空科目未被过滤）", cardsInRows == 10);

            // ---------- 4. 每个空科目的状态属性应正确 ----------
            Console.WriteLine();
            Console.WriteLine("[4] 空科目的状态属性");
            if (vm.AllCards.Count > 0)
            {
                SubjectCardViewModel first = vm.AllCards[0];
                Console.WriteLine("     首个科目：" + first.Name);
                Assert("初始状态 HasItems 为 false", first.HasItems == false);
                Assert("初始状态 IsEmpty 为 true", first.IsEmpty == true);
                Assert("空科目标题色为灰色 #6E6E73", first.TitleColorHex == "#6E6E73");
                Assert("空科目色条为暗灰 #4A4A4E", first.BarColorHex == "#4A4A4E");
            }

            // ---------- 5. 添加作业后，卡片状态应切换到「有作业」 ----------
            Console.WriteLine();
            Console.WriteLine("[5] 添加作业后卡片状态切换");
            SubjectCardViewModel target = vm.AllCards[0];
            string targetKey = target.Key;
            HomeworkItemViewModel added = vm.AddItem(targetKey, "练习册第 12 页 1-8 题");

            Assert("AddItem 返回非 null", added != null);
            Assert("条目已加入卡片", target.Items.Count == 1);
            Assert("HasItems 变为 true", target.HasItems == true);
            Assert("IsEmpty 变为 false", target.IsEmpty == false);
            Assert("标题色变为科目主题色", target.TitleColorHex == target.Config.Color);
            Assert("色条变为科目主题色", target.BarColorHex == target.Config.Color);
            Assert("Rows 仍然非 null", vm.Rows != null);

            // ---------- 6. 统计属性应正确 ----------
            Console.WriteLine();
            Console.WriteLine("[6] 统计属性");
            Assert("TodayCount 为 1", vm.TodayCount == 1);
            Assert("DoneCount 为 0", vm.DoneCount == 0);
            Assert("HasAnyHomework 为 true", vm.HasAnyHomework == true);
            Assert("ShowEmptyState 为 false", vm.ShowEmptyState == false);

            // ---------- 7. 删除最后一条作业后，卡片应转回淡化而非消失 ----------
            Console.WriteLine();
            Console.WriteLine("[7] 删除最后一条作业后卡片常驻");
            vm.RemoveItem(added);
            Assert("条目已移除", target.Items.Count == 0);
            Assert("HasItems 变回 false", target.HasItems == false);
            Assert("IsEmpty 变回 true", target.IsEmpty == true);
            Assert("卡片仍在列中（未被过滤）", CountCards(vm) == 10);

            // ---------- 8. 数据持久化往返 ----------
            Console.WriteLine();
            Console.WriteLine("[8] 数据持久化往返");
            vm.AddItem(targetKey, "测试内容 ABC");
            bool saved = vm.SaveAllNow();
            Assert("保存返回成功", saved == true);

            HomeworkStore reloaded = store.LoadHomework();
            string todayKey = DateTime.Today.ToString("yyyy-MM-dd");
            bool foundDate = reloaded.Dates.ContainsKey(todayKey);
            Assert("重新载入后能找到今天的日期键", foundDate);
            if (foundDate)
            {
                bool foundSubject = reloaded.Dates[todayKey].ContainsKey(targetKey);
                Assert("重新载入后能找到对应科目", foundSubject);
                if (foundSubject)
                {
                    List<Models.HomeworkItem> items = reloaded.Dates[todayKey][targetKey];
                    Assert("条目内容正确保存", items.Count == 1 && items[0].Content == "测试内容 ABC");
                }
            }

            // ---------- 9. 损坏 JSON 的容错 ----------
            Console.WriteLine();
            Console.WriteLine("[9] 损坏 JSON 容错");
            File.WriteAllText(store.SettingsPath, "{ \"version\": 1, \"window\": { ");
            JsonStore store2 = new JsonStore(testDir);
            Models.AppSettings recovered = store2.LoadSettings();
            Assert("损坏后仍返回可用配置", recovered != null);
            Assert("配置的 Subjects 非 null", recovered.Subjects != null && recovered.Subjects.Count > 0);
            Assert("产生了 LastNotice 提示", !string.IsNullOrEmpty(store2.LastNotice));
            Console.WriteLine("     提示语：" + store2.LastNotice);

            string[] backups = Directory.GetFiles(testDir, "*.corrupt_*.json");
            Assert("生成了 corrupt 备份文件", backups.Length > 0);

            // ---------- 10. 数值越界应被夹紧 ----------
            Console.WriteLine();
            Console.WriteLine("[10] 数值越界夹紧");
            Models.AppSettings bad = JsonStore.CreateDefaultSettings();
            bad.Window.OpacityPercent = 999;
            bad.Window.FontSize = 1;
            JsonStore.NormalizeSettings(bad);
            Assert("透明度被夹到 95", bad.Window.OpacityPercent == 95);
            Assert("字号被夹到 12", bad.Window.FontSize == 12);
            Console.WriteLine("     透明度=" + bad.Window.OpacityPercent + " 字号=" + bad.Window.FontSize);

            // ---------- 11. 分行算法：横向优先，一行放不下才换行 ----------
            Console.WriteLine();
            Console.WriteLine("[11] 分行算法（先横向铺满一行）");
            Assert("CardWidth 等于配置列宽 320", Math.Abs(vm.CardWidth - 320) < 0.01);
            Console.WriteLine("     CardWidth=" + vm.CardWidth + " CardSlotWidth=" + vm.CardSlotWidth);

            // 每行卡片数必须 <= 理论容量（列宽+间距 -> 一行几个）
            int maxPerRow = 0;
            int sumCheck = 0;
            foreach (RowViewModel r in vm.Rows)
            {
                int n = r.Cards == null ? 0 : r.Cards.Count;
                if (n > maxPerRow) maxPerRow = n;
                sumCheck += n;
            }
            Console.WriteLine("     每行最多卡片数：" + maxPerRow + "，卡片总数：" + sumCheck);
            Assert("所有卡片都被分配进行（数量守恒）", sumCheck == vm.AllCards.Count);
            Assert("每行至少 1 张卡片（不会产生空行）", maxPerRow > 0);

            // ---------- 12. 卡片排序：MoveCard 应改变顺序并持久化 Order ----------
            Console.WriteLine();
            Console.WriteLine("[12] 卡片长按拖拽排序（MoveCard）");
            string firstNameBefore = vm.AllCards[0].Name;
            string secondNameBefore = vm.AllCards[1].Name;
            Console.WriteLine("     移动前：[0]=" + firstNameBefore + " [1]=" + secondNameBefore);

            bool moved = vm.MoveCard(0, 1);
            Assert("MoveCard(0,1) 返回 true", moved == true);
            Assert("第 1 位变成了原来的第 2 个科目",
                   string.Equals(vm.AllCards[1].Name, firstNameBefore, StringComparison.Ordinal));
            Assert("第 0 位变成了原来的第 1 个科目",
                   string.Equals(vm.AllCards[0].Name, secondNameBefore, StringComparison.Ordinal));
            Console.WriteLine("     移动后：[0]=" + vm.AllCards[0].Name + " [1]=" + vm.AllCards[1].Name);

            // Order 应被重编为紧凑的 10,20,30..., 且与界面顺序一致
            bool orderOk = true;
            int expect = 10;
            foreach (SubjectCardViewModel c in vm.AllCards)
            {
                if (c.Config.Order != expect) { orderOk = false; break; }
                expect += 10;
            }
            Assert("Order 按界面顺序重编为 10,20,30...", orderOk);

            // 原地不动应返回 false（不做无意义的保存）
            Assert("MoveCard 相同索引返回 false", vm.MoveCard(2, 2) == false);
            Assert("MoveCard 越界索引被安全处理", vm.MoveCard(0, 9999) == true || vm.AllCards.Count <= 1);

            // ---------- 13. 窗口尺寸持久化 ----------
            Console.WriteLine();
            Console.WriteLine("[13] 窗口尺寸持久化");
            Assert("初始 ManualWidth 为 0（未设置过）", bad.Window.ManualWidth == 0);
            Assert("初始 InitialWindowWidth 为默认 380", Math.Abs(vm.InitialWindowWidth - 380) < 0.01);

            vm.SetManualWindowSize(520, 640);
            Assert("设置后 ManualWidth 为 520", vm.WindowSettings.ManualWidth == 520);
            Assert("设置后 ManualHeight 为 640", vm.WindowSettings.ManualHeight == 640);
            Assert("InitialWindowWidth 跟随变为 520", Math.Abs(vm.InitialWindowWidth - 520) < 0.01);
            Console.WriteLine("     宽=" + vm.InitialWindowWidth + " 高=" + vm.InitialWindowHeight);

            // 越界应被夹紧
            vm.SetManualWindowSize(10, 99999);
            Assert("过小宽度被夹到 280", vm.WindowSettings.ManualWidth == 280);
            Assert("过大高度被夹到 1400", vm.WindowSettings.ManualHeight == 1400);

            // ManualWidth=0 是「未设置」哨兵，不能被夹成 280
            Models.AppSettings fresh = JsonStore.CreateDefaultSettings();
            JsonStore.NormalizeSettings(fresh);
            Assert("ManualWidth=0 经过 Normalize 后仍为 0", fresh.Window.ManualWidth == 0);

            // ---------- 14. 布局宽度变化应重新分行 ----------
            Console.WriteLine();
            Console.WriteLine("[14] 窗口缩放后重新分行");
            vm.SetManualWindowSize(380, 720);
            vm.SetLayoutWidth(380);
            int rowsNarrow = vm.Rows.Count;
            vm.SetLayoutWidth(1600);
            int rowsWide = vm.Rows.Count;
            Console.WriteLine("     380 宽 -> " + rowsNarrow + " 行；1600 宽 -> " + rowsWide + " 行");
            Assert("窗口变宽后行数不增加", rowsWide <= rowsNarrow);
        }

        /// <summary>
        /// 统计分行结果中的卡片总数。
        /// </summary>
        /// <param name="vm">视图模型</param>
        /// <returns>卡片总数</returns>
        /// <remarks>用于验证「空科目未被过滤」。</remarks>
        private static int CountCards(MainViewModel vm)
        {
            if (vm == null || vm.Rows == null) return 0;
            int n = 0;
            foreach (RowViewModel row in vm.Rows)
            {
                if (row != null && row.Cards != null) n += row.Cards.Count;
            }
            return n;
        }

        /// <summary>
        /// 断言辅助方法。
        /// </summary>
        /// <param name="name">断言描述</param>
        /// <param name="condition">断言条件</param>
        /// <remarks>
        /// 失败时打印 [失败] 并累加计数，不中断执行——
        /// 这样一次运行能看到全部问题，而不是逐个排查。
        /// </remarks>
        private static void Assert(string name, bool condition)
        {
            if (condition)
            {
                _passCount++;
                Console.WriteLine("  [通过] " + name);
            }
            else
            {
                _failCount++;
                Console.WriteLine("  [失败] " + name);
            }
        }
    }
}
