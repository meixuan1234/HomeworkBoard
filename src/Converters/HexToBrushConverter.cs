using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HomeworkBoard.Converters
{
    /// <summary>
    /// 十六进制颜色字符串 -> WPF 画刷 的转换器。
    /// 
    /// 用途：settings.json 里科目颜色写成 "#FF6B81" 这类字符串，
    /// 绑定到界面时需要转成 Brush。格式非法时返回默认灰色，绝不抛异常。
    /// </summary>
    public class HexToBrushConverter : IValueConverter
    {
        /// <summary>格式非法时使用的兜底画刷（Apple 系统灰）。</summary>
        private static readonly SolidColorBrush FallbackBrush = CreateFrozen("#8E8E93");

        /// <summary>
        /// 字符串 -> 画刷。
        /// </summary>
        /// <param name="value">十六进制颜色字符串，形如 "#FF6B81"；可能为 null</param>
        /// <param name="targetType">目标类型（未使用）</param>
        /// <param name="parameter">转换参数（未使用）</param>
        /// <param name="culture">区域信息（未使用）</param>
        /// <returns>冻结的 SolidColorBrush；解析失败时返回灰刷</returns>
        /// <remarks>转换结果被 Freeze，可跨线程安全使用且渲染更省资源。</remarks>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string hex = value as string;
            if (string.IsNullOrWhiteSpace(hex)) return FallbackBrush;
            if (hex.Length != 7 || hex[0] != '#') return FallbackBrush;

            try
            {
                byte r = byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return CreateFrozen(r, g, b);
            }
            catch
            {
                return FallbackBrush;
            }
        }

        /// <summary>
        /// 画刷 -> 字符串。本程序不需要反向转换。
        /// </summary>
        /// <param name="value">源值</param>
        /// <param name="targetType">目标类型</param>
        /// <param name="parameter">参数</param>
        /// <param name="culture">区域</param>
        /// <returns>固定返回空字符串</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return string.Empty;
        }

        /// <summary>
        /// 创建并冻结一个画刷。
        /// </summary>
        /// <param name="hex">十六进制颜色串，形如 "#FF6B81"</param>
        /// <returns>冻结的 SolidColorBrush</returns>
        /// <remarks>内部使用 ColorConverter，失败时返回灰刷。</remarks>
        private static SolidColorBrush CreateFrozen(string hex)
        {
            try
            {
                object c = ColorConverter.ConvertFromString(hex);
                return CreateFrozen(((Color)c).R, ((Color)c).G, ((Color)c).B);
            }
            catch
            {
                return new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
            }
        }

        /// <summary>
        /// 由 RGB 分量创建并冻结画刷。
        /// </summary>
        /// <param name="r">红分量 0-255</param>
        /// <param name="g">绿分量 0-255</param>
        /// <param name="b">蓝分量 0-255</param>
        /// <returns>已冻结的 SolidColorBrush（可安全跨线程共享）</returns>
        private static SolidColorBrush CreateFrozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// 十六进制颜色字符串 -> Color 的转换器。
    /// 
    /// 用途：需要把科目色变成渐变（如色条）时使用。
    /// 与 HexToBrushConverter 配套，保证解析逻辑一致。
    /// </summary>
    public class HexToColorConverter : IValueConverter
    {
        /// <summary>
        /// 字符串 -> Color。
        /// </summary>
        /// <param name="value">十六进制颜色字符串；可能为 null</param>
        /// <param name="targetType">目标类型（未使用）</param>
        /// <param name="parameter">转换参数（未使用）</param>
        /// <param name="culture">区域信息（未使用）</param>
        /// <returns>解析出的 Color；失败时返回灰 Color</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string hex = value as string;
            if (string.IsNullOrWhiteSpace(hex) || hex.Length != 7 || hex[0] != '#')
            {
                return Color.FromRgb(0x8E, 0x8E, 0x93);
            }
            try
            {
                byte r = byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return Color.FromRgb(r, g, b);
            }
            catch
            {
                return Color.FromRgb(0x8E, 0x8E, 0x93);
            }
        }

        /// <summary>
        /// Color -> 字符串。本程序不需要反向转换。
        /// </summary>
        /// <param name="value">源值</param>
        /// <param name="targetType">目标类型</param>
        /// <param name="parameter">参数</param>
        /// <param name="culture">区域</param>
        /// <returns>固定返回空字符串</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return string.Empty;
        }
    }
}
