using System;
using System.Globalization;
using System.Windows.Data;

namespace FileDownloader.Converters
{
    public class IsNullOrEmptyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Возвращает true, если строка null или пустая, иначе false
            return string.IsNullOrEmpty(value as string);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Конвертация обратно не поддерживается
            throw new NotImplementedException();
        }
    }
} 