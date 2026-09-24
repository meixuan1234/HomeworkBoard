using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeworkBoard.Models;

namespace HomeworkBoard.Core
{
    /// <summary>
    /// JSON 数据文件读写管理器。
    /// 
    /// 设计目标（对应需求「容错与兜底」章节）：
    /// 1. 文件缺失 -> 自动创建默认文件，不报错；
    /// 2. 文件损坏 -> 备份为 xxx.corrupt_时间戳.json，重建默认文件，返回温和提示；
    /// 3. 字段异常/类型错误/空值 -> 逐字段兜底，缺失的补默认值，类型不符的用默认值替换；
    /// 4. 目录不可写 -> 内存模式运行，IsWritable=false，界面给出温和提示，任何保存都不抛异常。
    /// 
    /// 写入策略：临时文件 + File.Replace 的原子替换，避免写到一半断电导致 JSON 半截损坏。
    /// </summary>
    public class JsonStore
    {
        /// <summary>JSON 序列化选项：缩进美化、允许中文原样输出、忽略 null 字段。</summary>
        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        /// <summary>读取时的解析选项：忽略大小写、容忍尾随逗号、允许注释跳过、允许从字符串读数字。</summary>
        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        /// <summary>settings.json 的完整路径。</summary>
        public string SettingsPath { get; private set; }

        /// <summary>homework.json 的完整路径。</summary>
        public string HomeworkPath { get; private set; }

        /// <summary>程序数据目录（即 exe 所在目录）。</summary>
        public string DataDirectory { get; private set; }

        /// <summary>
        /// 当前目录是否可写。
        /// 为 false 时程序进入「内存模式」：功能照常可用，但关闭后不保存。
        /// </summary>
        public bool IsWritable { get; private set; }

        /// <summary>
        /// 最近一次读写过程中产生的提示信息（给状态栏显示用）。
        /// 例如「settings.json 损坏，已备份并重建」。为空表示一切正常。
        /// </summary>
        public string LastNotice { get; private set; }

        /// <summary>
        /// 构造存储管理器并探测目录可写性。
        /// </summary>
        /// <param name="dataDirectory">数据目录。传 null 或空时自动使用 exe 所在目录。</param>
        /// <remarks>
        /// 不读文件内容，只做路径准备与写权限探测，因此非常快（&lt;1ms）。
        /// 任何异常都被内部消化，构造函数不会抛出。
        /// </remarks>
        public JsonStore(string dataDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dataDirectory))
                {
                    // AppContext.BaseDirectory 指向 exe 所在目录（单文件发布下同样正确）
                    dataDirectory = AppContext.BaseDirectory;
                }
                DataDirectory = dataDirectory;
                SettingsPath = Path.Combine(dataDirectory, "settings.json");
                HomeworkPath = Path.Combine(dataDirectory, "homework.json");
            }
            catch
            {
                // 极端情况下（路径非法）退化为当前工作目录
                DataDirectory = ".";
                SettingsPath = "settings.json";
                HomeworkPath = "homework.json";
            }

            LastNotice = string.Empty;
            IsWritable = ProbeWritable();
        }

        /// <summary>
        /// 探测数据目录是否可写。
        /// </summary>
        /// <returns>true = 可正常写入；false = 不可写（如程序放在 C:\Program Files 且未提权）</returns>
        /// <remarks>
        /// 方法：在目标目录建一个临时文件再删掉。
        /// 这是最可靠的探测方式——比检查 ACL 权限准确得多。
        /// 目录不存在时会尝试创建一次。任何异常都视为不可写，不向上抛出。
        /// </remarks>
        private bool ProbeWritable()
        {
            string probeFile = null;
            try
            {
                if (!Directory.Exists(DataDirectory))
                {
                    Directory.CreateDirectory(DataDirectory);
                }
                probeFile = Path.Combine(DataDirectory, ".write_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var fs = new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(0);
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (probeFile != null)
                {
                    try { if (File.Exists(probeFile)) File.Delete(probeFile); } catch { /* 清理失败无所谓 */ }
                }
            }
        }

        /// <summary>
        /// 读取配置文件。文件不存在 / 损坏 / 字段异常时都会返回一份可用的默认配置。
        /// </summary>
        /// <returns>保证非 null 的 AppSettings 实例，且各子对象与集合均已初始化为非 null。</returns>
        /// <remarks>
        /// 不会抛出异常。若发生修复行为，提示语会被写入 LastNotice。
        /// </remarks>
        public AppSettings LoadSettings()
        {
            AppSettings result = ReadJsonSafe<AppSettings>(SettingsPath, "settings.json");
            if (result == null)
            {
                // 文件不存在或彻底损坏，使用默认配置并尝试落盘
                result = CreateDefaultSettings();
                TryWriteFile(SettingsPath, result);
            }
            NormalizeSettings(result);
            return result;
        }

        /// <summary>
        /// 读取作业数据文件。缺失/损坏时返回空数据并尝试重建。
        /// </summary>
        /// <returns>保证非 null 的 HomeworkStore 实例，Dates 字段保证非 null。</returns>
        /// <remarks>不会抛出异常。</remarks>
        public HomeworkStore LoadHomework()
        {
            HomeworkStore result = ReadJsonSafe<HomeworkStore>(HomeworkPath, "homework.json");
            if (result == null)
            {
                result = CreateDefaultHomework();
                TryWriteFile(HomeworkPath, result);
            }
            NormalizeHomework(result);
            return result;
        }

        /// <summary>
        /// 安全读取任意 JSON 文件为指定类型。
        /// </summary>
        /// <typeparam name="T">目标类型</typeparam>
        /// <param name="path">文件绝对路径</param>
        /// <param name="displayName">用于提示语的文件显示名</param>
        /// <returns>解析成功时返回对象；文件不存在或解析失败时返回 null</returns>
        /// <remarks>
        /// 失败时不会直接向下抛出，而是：
        /// - 若文件存在且可写 -> 备份成 xxx.corrupt_yyyyMMdd_HHmmss.json；
        /// - 记录提示语到 LastNotice；
        /// - 返回 null 交由调用方重建默认值。
        /// </remarks>
        private T ReadJsonSafe<T>(string path, string displayName) where T : class
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text))
                {
                    // 空文件视为损坏
                    BackupCorrupt(path, displayName, "文件内容为空");
                    return null;
                }

                T obj = JsonSerializer.Deserialize<T>(text, ReadOptions);
                if (obj == null)
                {
                    BackupCorrupt(path, displayName, "解析结果为空");
                    return null;
                }
                return obj;
            }
            catch (JsonException)
            {
                // JSON 语法错误（缺逗号、括号不配对、多写了中文引号等）
                BackupCorrupt(path, displayName, "JSON 格式错误");
                return null;
            }
            catch (Exception)
            {
                // IO 异常、占用、路径过长等
                BackupCorrupt(path, displayName, "读取失败");
                return null;
            }
        }

        /// <summary>
        /// 把损坏的文件改名备份，保留现场供同学手工修复。
        /// </summary>
        /// <param name="path">原文件路径</param>
        /// <param name="displayName">文件显示名（提示语用）</param>
        /// <param name="reason">损坏原因简述</param>
        /// <remarks>
        /// 备份名格式：原文件名.corrupt_yyyyMMdd_HHmmss.json
        /// 例如 settings.json.corrupt_20260918_203000.json
        /// 目录不可写或备份失败时静默跳过，只记录提示语，绝不抛异常。
        /// </remarks>
        private void BackupCorrupt(string path, string displayName, string reason)
        {
            try
            {
                if (File.Exists(path))
                {
                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string bakPath = path + ".corrupt_" + stamp + ".json";
                    // 避免同一秒内重复覆盖
                    if (File.Exists(bakPath))
                    {
                        bakPath = path + ".corrupt_" + stamp + "_" + Guid.NewGuid().ToString("N").Substring(0, 4) + ".json";
                    }
                    File.Move(path, bakPath);
                    LastNotice = displayName + " " + reason + "，已备份为 " + Path.GetFileName(bakPath) + "，并重建默认内容。";
                    return;
                }
            }
            catch
            {
                // 备份失败（目录只读等），继续走后续流程
            }
            LastNotice = displayName + " " + reason + "，已重建默认内容（原文件无法备份）。";
        }

        /// <summary>
        /// 原子写入 JSON 文件：先写临时文件，再替换目标文件。
        /// </summary>
        /// <param name="path">目标文件路径</param>
        /// <param name="obj">要序列化的对象</param>
        /// <returns>true = 写入成功；false = 失败（已静默处理）</returns>
        /// <remarks>
        /// 采用 File.Replace 保证原子性：任何时刻磁盘上要么是完整的旧文件，要么是完整的新文件，
        /// 不会出现「写了一半」的半截 JSON。
        /// 目标文件尚不存在时退化为 File.Move（同样是原子操作）。
        /// 目录不可写或文件被记事本等程序占用时返回 false，不会抛异常、不会导致崩溃。
        /// </remarks>
        public bool TryWriteFile(string path, object obj)
        {
            if (!IsWritable)
            {
                return false;
            }

            string tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string json = JsonSerializer.Serialize(obj, WriteOptions);
                // 明确以 UTF-8 (无 BOM) 写出，保证记事本打开不乱码
                File.WriteAllText(tmpPath, json, new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    // 第三个参数表示备份文件；传 null 表示不需要额外备份
                    File.Replace(tmpPath, path, null, true);
                }
                else
                {
                    File.Move(tmpPath, path);
                }
                return true;
            }
            catch
            {
                // 写入失败（只读目录、文件被占用、磁盘满等）——静默失败，由调用方决定是否提示
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 保存配置。
        /// </summary>
        /// <param name="settings">配置对象，为 null 时直接返回 false</param>
        /// <returns>true = 保存成功</returns>
        /// <remarks>保证不抛异常。用于节流后的延迟保存。</remarks>
        public bool SaveSettings(AppSettings settings)
        {
            if (settings == null) return false;
            return TryWriteFile(SettingsPath, settings);
        }

        /// <summary>
        /// 保存作业数据。
        /// </summary>
        /// <param name="store">数据对象，为 null 时直接返回 false</param>
        /// <returns>true = 保存成功</returns>
        /// <remarks>保证不抛异常。</remarks>
        public bool SaveHomework(HomeworkStore store)
        {
            if (store == null) return false;
            return TryWriteFile(HomeworkPath, store);
        }

        /// <summary>
        /// 构造默认配置（首次运行或文件损坏后重建时使用）。
        /// </summary>
        /// <returns>包含 6 个主科目 + 4 个「小四门」子科目、8 个常用模板的默认配置</returns>
        /// <remarks>
        /// 科目顺序即 order 值，按 school 常规排课习惯：主科在前，小四门在后。
        /// 每个科目配了不同主题色，方便一眼分辨。
        /// </remarks>
        public static AppSettings CreateDefaultSettings()
        {
            var s = new AppSettings
            {
                Version = 1,
                Note = "本文件是＜作业可视化悬浮窗＞的配置文件，可用记事本直接编辑。所有以 \"_\" 开头的字段都是说明文字，程序会忽略。修改后保存，程序会在几秒内自动重新读取（也可以右键悬浮窗点「重新载入」）。",
                Window = new WindowSettings
                {
                    OpacityPercent = 92,
                    Topmost = true,
                    FontSize = 20,
                    TitleFontSize = 22,
                    ColumnWidth = 320,
                    MaxHeight = 720,
                    EdgeMargin = 20
                },
                Subjects = new List<SubjectConfig>
                {
                    new SubjectConfig { Key = "chinese",  Name = "语文",       Color = "#FF6B81", Enabled = true, Order = 10 },
                    new SubjectConfig { Key = "math",     Name = "数学",       Color = "#4A9EFF", Enabled = true, Order = 20 },
                    new SubjectConfig { Key = "english",  Name = "英语",       Color = "#34C759", Enabled = true, Order = 30 },
                    new SubjectConfig { Key = "physics",  Name = "物理",       Color = "#AF52DE", Enabled = true, Order = 40 },
                    new SubjectConfig { Key = "chemistry",Name = "化学",       Color = "#FF9500", Enabled = true, Order = 50 },
                    new SubjectConfig { Key = "politics", Name = "道德与法治", Color = "#FF3B30", Enabled = true, Order = 60 },
                    new SubjectConfig { Key = "history",  Name = "历史",       Color = "#A2845E", Enabled = true, Order = 70 },
                    new SubjectConfig { Key = "geography",Name = "地理",       Color = "#30B0C7", Enabled = true, Order = 80 },
                    new SubjectConfig { Key = "biology",  Name = "生物",       Color = "#8BC34A", Enabled = true, Order = 90 },
                    new SubjectConfig { Key = "other",    Name = "其他",       Color = "#8E8E93", Enabled = true, Order = 999 }
                },
                Templates = new List<TemplateConfig>
                {
                    new TemplateConfig { Label = "练习册",  Prefix = "练习册第 ",   Suffix = " 页",      Enabled = true },
                    new TemplateConfig { Label = "书",      Prefix = "书第 ",       Suffix = " 页",      Enabled = true },
                    new TemplateConfig { Label = "试卷",    Prefix = "试卷：",      Suffix = "",         Enabled = true },
                    new TemplateConfig { Label = "背诵",    Prefix = "背诵：",      Suffix = "",         Enabled = true },
                    new TemplateConfig { Label = "预习",    Prefix = "预习 ",       Suffix = "",         Enabled = true },
                    new TemplateConfig { Label = "订正",    Prefix = "订正 ",       Suffix = "",         Enabled = true },
                    new TemplateConfig { Label = "抄写",    Prefix = "抄写 ",       Suffix = " 遍",      Enabled = true },
                    new TemplateConfig { Label = "作文",    Prefix = "作文：",      Suffix = "",         Enabled = true }
                }
            };
            return s;
        }

        /// <summary>
        /// 构造空的作业数据容器。
        /// </summary>
        /// <returns>Dates 为空字典的 HomeworkStore</returns>
        /// <remarks>用于首次运行或文件损坏后的重建。</remarks>
        public static HomeworkStore CreateDefaultHomework()
        {
            return new HomeworkStore
            {
                Version = 1,
                Note = "本文件保存每天的作业内容。结构：dates -> 日期(yyyy-MM-dd) -> 科目键名 -> 作业条目数组。科目键名与 settings.json 中 subjects 的 key 一致。可用记事本直接编辑。",
                Dates = new Dictionary<string, Dictionary<string, List<HomeworkItem>>>(StringComparer.OrdinalIgnoreCase)
            };
        }

        /// <summary>
        /// 校验并修正配置对象，确保所有子对象与集合非 null，数值在合理范围内。
        /// </summary>
        /// <param name="s">待修正的配置对象（原地修改）</param>
        /// <remarks>
        /// 处理以下异常场景：
        /// - window 字段缺失或为 null -> 建默认；
        /// - subjects / templates 为 null -> 用默认列表；
        /// - opacityPercent 越界 -> 夹紧到 70—95；
        /// - 科目 key 为空 -> 自动补 subject_序号；
        /// - 科目 name 为空 -> 用 key 顶替；
        /// - 颜色格式非法 -> 替换为默认灰 #8E8E93。
        /// </remarks>
        public static void NormalizeSettings(AppSettings s)
        {
            if (s == null) return;

            if (s.Window == null)
            {
                s.Window = new WindowSettings();
            }

            // 数值夹紧：防止同学手改 JSON 时写成 0 或 1000 导致界面不可用
            s.Window.OpacityPercent = Clamp(s.Window.OpacityPercent, 70, 95);
            s.Window.FontSize = Clamp(s.Window.FontSize, 12, 48);
            s.Window.TitleFontSize = Clamp(s.Window.TitleFontSize, 12, 56);
            s.Window.ColumnWidth = Clamp(s.Window.ColumnWidth, 200, 800);
            // 上限 1400 而非 3000：运行时还会与显示器工作区高度取小值，
            // 写更大没有实际意义，反而让手工编辑容易填出无效值。
            s.Window.MaxHeight = Clamp(s.Window.MaxHeight, 300, 1400);
            s.Window.EdgeMargin = Clamp(s.Window.EdgeMargin, 0, 200);
            // 手动窗口尺寸：0 是「未设置过」的哨兵值，必须原样保留，
            // 否则会被夹到下限变成 280，导致默认宽度 380 永远失效。
            if (s.Window.ManualWidth != 0)
            {
                s.Window.ManualWidth = Clamp(s.Window.ManualWidth, 280, 1600);
            }
            if (s.Window.ManualHeight != 0)
            {
                s.Window.ManualHeight = Clamp(s.Window.ManualHeight, 180, 1400);
            }

            if (s.Subjects == null || s.Subjects.Count == 0)
            {
                s.Subjects = CreateDefaultSettings().Subjects;
            }
            else
            {
                int idx = 0;
                foreach (SubjectConfig sub in s.Subjects)
                {
                    if (sub == null) continue;
                    idx++;
                    if (string.IsNullOrWhiteSpace(sub.Key))
                    {
                        sub.Key = "subject_" + idx;
                    }
                    if (string.IsNullOrWhiteSpace(sub.Name))
                    {
                        sub.Name = sub.Key;
                    }
                    if (!IsValidHexColor(sub.Color))
                    {
                        sub.Color = "#8E8E93";
                    }
                }
                // 移除 null 项
                s.Subjects.RemoveAll(x => x == null);
            }

            if (s.Templates == null)
            {
                s.Templates = CreateDefaultSettings().Templates;
            }
            else
            {
                foreach (TemplateConfig t in s.Templates)
                {
                    if (t == null) continue;
                    if (string.IsNullOrWhiteSpace(t.Label))
                    {
                        t.Label = "模板";
                    }
                    if (t.Prefix == null) t.Prefix = string.Empty;
                    if (t.Suffix == null) t.Suffix = string.Empty;
                }
                s.Templates.RemoveAll(x => x == null);
            }
        }

        /// <summary>
        /// 校验并修正作业数据对象。
        /// </summary>
        /// <param name="h">待修正的数据对象（原地修改）</param>
        /// <remarks>
        /// 处理：Dates 为 null -> 建空字典；科目值为 null -> 建空列表；
        /// 条目为 null 或 id 缺失 -> 补全；Content 为 null -> 转空串。
        /// </remarks>
        public static void NormalizeHomework(HomeworkStore h)
        {
            if (h == null) return;
            if (h.Dates == null)
            {
                h.Dates = new Dictionary<string, Dictionary<string, List<HomeworkItem>>>(StringComparer.OrdinalIgnoreCase);
            }

            // 收集需要清理的键，避免遍历时修改集合
            var badDateKeys = new List<string>();
            foreach (var datePair in h.Dates)
            {
                if (string.IsNullOrWhiteSpace(datePair.Key) || datePair.Value == null)
                {
                    badDateKeys.Add(datePair.Key);
                    continue;
                }

                var badSubjectKeys = new List<string>();
                foreach (var subPair in datePair.Value)
                {
                    if (string.IsNullOrWhiteSpace(subPair.Key))
                    {
                        badSubjectKeys.Add(subPair.Key);
                        continue;
                    }
                    if (subPair.Value == null)
                    {
                        datePair.Value[subPair.Key] = new List<HomeworkItem>();
                        continue;
                    }

                    for (int i = subPair.Value.Count - 1; i >= 0; i--)
                    {
                        HomeworkItem item = subPair.Value[i];
                        if (item == null)
                        {
                            subPair.Value.RemoveAt(i);
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(item.Id))
                        {
                            item.Id = Guid.NewGuid().ToString("N");
                        }
                        if (item.Content == null)
                        {
                            item.Content = string.Empty;
                        }
                        if (item.CreatedAt == null)
                        {
                            item.CreatedAt = string.Empty;
                        }
                    }
                }

                foreach (string k in badSubjectKeys)
                {
                    datePair.Value.Remove(k);
                }
            }

            foreach (string k in badDateKeys)
            {
                h.Dates.Remove(k);
            }
        }

        /// <summary>
        /// 整数夹紧工具。
        /// </summary>
        /// <param name="value">原始值</param>
        /// <param name="min">允许的最小值</param>
        /// <param name="max">允许的最大值</param>
        /// <returns>夹紧后的值</returns>
        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>
        /// 判断字符串是否为合法的 #RRGGBB 十六进制颜色。
        /// </summary>
        /// <param name="color">待检测字符串，可能为 null</param>
        /// <returns>true = 格式合法</returns>
        /// <remarks>只接受 7 字符形式（# + 6 位十六进制），不接受 3 位简写与 8 位带透明度形式。</remarks>
        private static bool IsValidHexColor(string color)
        {
            if (string.IsNullOrWhiteSpace(color)) return false;
            if (color.Length != 7) return false;
            if (color[0] != '#') return false;
            for (int i = 1; i < 7; i++)
            {
                char c = color[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) return false;
            }
            return true;
        }
    }
}
