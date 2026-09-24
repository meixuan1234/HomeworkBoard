using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HomeworkBoard.Converters
{
    /// <summary>
    /// bool -> Visibility 转换器。
    /// 
    /// 用途：绑定可见性。true 显示、false 折叠（Collapsed，不占布局空间）。
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        /// <summary>
        /// bool -> Visibility。
        /// </summary>
        /// <param name="value">布尔值；可能为 null 或非 bool 类型</param>
        /// <param name="targetType">目标类型（未使用）</param>
        /// <param name="parameter">若传字符串 "invert" 则结果取反</param>
        /// <param name="culture">区域信息（未使用）</param>
        /// <returns>Visibility.Visible 或 Visibility.Collapsed</returns>
        /// <remarks>非法输入一律返回 Collapsed，避免界面出现莫名元素。</remarks>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool && (bool)value;
            string p = parameter as string;
            if (!string.IsNullOrEmpty(p) && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
            {
                b = !b;
            }
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Visibility -> bool。本程序不需要反向转换。
        /// </summary>
        /// <param name="value">源值</param>
        /// <param name="targetType">目标类型</param>
        /// <param name="parameter">参数</param>
        /// <param name="culture">区域</param>
        /// <returns>是否为 Visible</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility && (Visibility)value == Visibility.Visible;
        }
    }
}
