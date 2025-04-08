using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FileDownloader.Converters
{
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = false;
            if (value is bool b)
            {
                flag = b;
            }

            // Логика инвертирования, если параметр "Invert" передан
            if (parameter != null && parameter.ToString().Equals("Invert", StringComparison.OrdinalIgnoreCase))
            {
                flag = !flag;
            }

            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Обратное преобразование обычно не нужно для этого конвертера
            if (value is Visibility visibility)
            {
                bool flag = visibility == Visibility.Visible;
                if (parameter != null && parameter.ToString().Equals("Invert", StringComparison.OrdinalIgnoreCase))
                {
                    flag = !flag;
                }
                return flag;
            }
            return false;
        }
    }
} 