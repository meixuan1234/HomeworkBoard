using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HomeworkBoard.Models
{
    /// <summary>
    /// 作业条目：一条具体的作业记录。
    /// 对应 homework.json 中 dates -> [日期] -> subjects -> [科目键名] 数组内的一个元素。
    /// </summary>
    public class HomeworkItem
    {
        /// <summary>
        /// 条目唯一标识（GUID 字符串）。
        /// 作用：编辑/删除时精确定位到某一条，避免同科目下多条内容重复导致误删。
        /// 若 JSON 中缺失该字段，程序会自动补一个新的，不影响使用。
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>
        /// 作业内容文本，例如「练习册第 12 页 1-8 题」。
        /// 允许为空字符串，界面会显示为「(空)」并提示但不崩溃。
        /// </summary>
        [JsonPropertyName("content")]
        public string Content { get; set; }

        /// <summary>
        /// 是否已完成（勾选状态）。学生在界面上点勾即可标记。
        /// </summary>
        [JsonPropertyName("done")]
        public bool Done { get; set; }

        /// <summary>
        /// 创建时间（本地时间字符串，形如 2026-09-18 20:30:00）。
        /// 仅用于排序与手工排查，不影响功能。
        /// </summary>
        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; }

        /// <summary>
        /// 创建一个新的作业条目。
        /// </summary>
        /// <param name="content">作业内容文本，可为 null（内部会转为空字符串）</param>
        /// <returns>已初始化 id 与 createdAt 的 HomeworkItem 实例</returns>
        /// <remarks>不依赖任何外部状态，纯构造函数，线程安全。</remarks>
        public static HomeworkItem Create(string content)
        {
            return new HomeworkItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Content = content ?? string.Empty,
                Done = false,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }
    }

    /// <summary>
    /// 单个科目的配置：科目名 / 键名 / 是否启用 / 显示顺序。
    /// 对应 settings.json 中 "subjects" 数组内的一个元素。
    /// </summary>
    public class SubjectConfig
    {
        /// <summary>
        /// 科目键名（英文/拼音，作为 homework.json 里 subjects 字典的 key）。
        /// 必须唯一，建议只用字母数字下划线，改用中文也可以但手工编辑时容易出错。
        /// </summary>
        [JsonPropertyName("key")]
        public string Key { get; set; }

        /// <summary>
        /// 科目显示名称，直接显示在悬浮窗卡片标题上，例如「语文」「道德与法治」。
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// 主题色（十六进制，形如 "#FF6B81"）。
        /// 用于卡片左侧色条与标题颜色，方便一眼区分科目。
        /// 格式非法时程序会回退到默认灰色，不会崩溃。
        /// </summary>
        [JsonPropertyName("color")]
        public string Color { get; set; }

        /// <summary>
        /// 是否启用。false 时该科目不显示，也不再能添加作业。
        /// 同学若不想看到某个科目，把它改成 false 即可，无需删除配置。
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// 显示顺序，数字越小越靠前。
        /// 相同数字时按数组出现顺序排。
        /// </summary>
        [JsonPropertyName("order")]
        public int Order { get; set; }
    }

    /// <summary>
    /// 作业内容模板（预设短语），点击后快速生成一条作业。
    /// 对应 settings.json 中 "templates" 数组内的一个元素。
    /// </summary>
    public class TemplateConfig
    {
        /// <summary>
        /// 模板显示名，例如「练习册」。
        /// </summary>
        [JsonPropertyName("label")]
        public string Label { get; set; }

        /// <summary>
        /// 模板起始文本，选中后填入输入框。例如「练习册第 」。
        /// 允许为空字符串，表示只插入一个空内容待用户手填。
        /// </summary>
        [JsonPropertyName("prefix")]
        public string Prefix { get; set; }

        /// <summary>
        /// 模板后缀，例如「 页」。
        /// 程序会把光标自动放在 prefix 与 suffix 之间，方便直接打数字。
        /// 允许为空字符串。
        /// </summary>
        [JsonPropertyName("suffix")]
        public string Suffix { get; set; }

        /// <summary>
        /// 是否启用该模板。false 时界面上不显示。
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// 窗口外观与行为设置。
    /// 对应 settings.json 中 "window" 对象。
    /// </summary>
    public class WindowSettings
    {
        /// <summary>
        /// 悬浮窗整体不透明度（百分比整数）。
        /// 取值范围被强制收敛到 70—95，超出会被程序自动夹紧。
        /// </summary>
        [JsonPropertyName("opacityPercent")]
        public int OpacityPercent { get; set; } = 92;

        /// <summary>
        /// 是否窗口置顶。
        /// true = 始终显示在其它窗口之上；false = 普通窗口层级。
        /// </summary>
        [JsonPropertyName("topmost")]
        public bool Topmost { get; set; } = true;

        /// <summary>
        /// 正文字号（像素）。用于作业条目文字，教室大屏建议 20—30。
        /// 范围被夹紧到 12—48。
        /// </summary>
        [JsonPropertyName("fontSize")]
        public int FontSize { get; set; } = 20;

        /// <summary>
        /// 标题字号（像素）。用于科目卡片标题，范围被夹紧到 12—56。
        /// </summary>
        [JsonPropertyName("titleFontSize")]
        public int TitleFontSize { get; set; } = 22;

        /// <summary>
        /// 单列宽度（像素）。作业卡片列宽，影响一屏能排几列。
        /// 范围被夹紧到 200—800。
        /// </summary>
        [JsonPropertyName("columnWidth")]
        public int ColumnWidth { get; set; } = 320;

        /// <summary>
        /// 窗口最大高度（像素）。超出后内容区滚动，避免超出屏幕。
        /// 范围被夹紧到 300—1400。
        /// 
        /// 默认 720：约等于 1080p 屏（1080 - 任务栏 48 - 上下留白 40 - 标题栏/状态栏/工具条 ~110）
        /// 的舒适值，不会再压到任务栏。程序还会在运行时与显示器工作区高度取小值，
        /// 因此在 768p 等小屏上也不会溢出（见 MainViewModel.MaxWindowHeight）。
        /// </summary>
        [JsonPropertyName("maxHeight")]
        public int MaxHeight { get; set; } = 720;

        /// <summary>
        /// 距屏幕边缘的间距（像素），用于计算默认位置。
        /// 范围被夹紧到 0—200。
        /// </summary>
        [JsonPropertyName("edgeMargin")]
        public int EdgeMargin { get; set; } = 20;

        /// <summary>
        /// 用户手动调整后的窗口宽度（像素）。
        /// 0 = 尚未手动调整过，使用默认宽度 380。
        /// 
        /// 为什么要保存它：窗口位置按需求每次启动回默认位（靠右上角），
        /// 但「大小」是用户的阅读偏好（老师可能喜欢更宽以一行多放几个科目），
        /// 每次都重置会让人反复拖动，属于纯粹的骚扰。
        /// 范围被夹紧到 280—1600，与 MainWindow 的 MinWidth/MaxWidth 保持一致。
        /// </summary>
        [JsonPropertyName("manualWidth")]
        public int ManualWidth { get; set; } = 0;

        /// <summary>
        /// 用户手动调整后的窗口高度（像素）。
        /// 0 = 尚未手动调整过，使用默认高度 720。
        /// 运行时仍会与显示器工作区高度取小值，避免换到小屏后溢出屏幕。
        /// 范围被夹紧到 180—1400。
        /// </summary>
        [JsonPropertyName("manualHeight")]
        public int ManualHeight { get; set; } = 0;
    }

    /// <summary>
    /// settings.json 的根对象。
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// 配置格式版本号。仅用于将来兼容判断，当前恒为 1。
        /// </summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        /// <summary>
        /// 说明字段（以 _ 开头，程序读取时忽略，仅供同学手工编辑时参考）。
        /// </summary>
        [JsonPropertyName("_说明")]
        public string Note { get; set; }

        /// <summary>
        /// 窗口设置。
        /// </summary>
        [JsonPropertyName("window")]
        public WindowSettings Window { get; set; }

        /// <summary>
        /// 科目列表。
        /// </summary>
        [JsonPropertyName("subjects")]
        public List<SubjectConfig> Subjects { get; set; }

        /// <summary>
        /// 作业内容模板列表。
        /// </summary>
        [JsonPropertyName("templates")]
        public List<TemplateConfig> Templates { get; set; }
    }

    /// <summary>
    /// homework.json 的根对象。
    /// </summary>
    public class HomeworkStore
    {
        /// <summary>
        /// 配置格式版本号，当前恒为 1。
        /// </summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        /// <summary>
        /// 说明字段（程序忽略，仅供手工编辑参考）。
        /// </summary>
        [JsonPropertyName("_说明")]
        public string Note { get; set; }

        /// <summary>
        /// 按日期归档的作业数据。
        /// 结构：{ "2026-09-18": { "chinese": [ ...作业条目... ], "math": [...] }, ... }
        /// 日期格式必须是 yyyy-MM-dd，否则程序会忽略该键并在界面上提示。
        /// </summary>
        [JsonPropertyName("dates")]
        public Dictionary<string, Dictionary<string, List<HomeworkItem>>> Dates { get; set; }
    }
}
