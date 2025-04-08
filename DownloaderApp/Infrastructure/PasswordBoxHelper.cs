using System.Windows;
using System.Windows.Controls; // Для PasswordBox

namespace FileDownloader.Infrastructure;

// Вспомогательный класс для привязки PasswordBox.Password
public static class PasswordBoxHelper
{
    // Используем Attached Property для хранения флага, что мы уже обрабатываем изменение
    private static readonly DependencyProperty UpdatingPasswordProperty = 
        DependencyProperty.RegisterAttached("UpdatingPassword", typeof(bool), typeof(PasswordBoxHelper), new PropertyMetadata(false));

    // Основное Attached Property, к которому будем привязываться из ViewModel
    public static readonly DependencyProperty BoundPasswordProperty = 
        DependencyProperty.RegisterAttached("BoundPassword", typeof(string), typeof(PasswordBoxHelper), 
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static string GetBoundPassword(DependencyObject d)
    {
        return (string)d.GetValue(BoundPasswordProperty);
    }

    public static void SetBoundPassword(DependencyObject d, string value)
    {
        d.SetValue(BoundPasswordProperty, value);
    }

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordBox passwordBox)
        {
            // Отписываемся от события, чтобы избежать рекурсии
            passwordBox.PasswordChanged -= HandlePasswordChanged;

            string newPassword = (string)e.NewValue;

            // Обновляем пароль в PasswordBox, только если он действительно изменился
            // и если изменение пришло не из самого PasswordBox (проверяем флаг UpdatingPassword)
            if (!(bool)passwordBox.GetValue(UpdatingPasswordProperty) && passwordBox.Password != newPassword)
            {
                passwordBox.Password = newPassword;
            }
            
            // Подписываемся обратно
            passwordBox.PasswordChanged += HandlePasswordChanged;
        }
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            // Устанавливаем флаг, что изменение идет из PasswordBox
            passwordBox.SetValue(UpdatingPasswordProperty, true);
            // Обновляем наше Attached Property
            SetBoundPassword(passwordBox, passwordBox.Password);
            // Снимаем флаг
            passwordBox.SetValue(UpdatingPasswordProperty, false);
        }
    }

    // Метод для инициализации подписки (вызывать из XAML или code-behind)
     public static readonly DependencyProperty BindPasswordProperty = DependencyProperty.RegisterAttached(
        "BindPassword", typeof(bool), typeof(PasswordBoxHelper), new PropertyMetadata(false, OnBindPasswordChanged));

    public static bool GetBindPassword(DependencyObject dp)
    {
        return (bool)dp.GetValue(BindPasswordProperty);
    }

    public static void SetBindPassword(DependencyObject dp, bool value)
    {
        dp.SetValue(BindPasswordProperty, value);
    }

    private static void OnBindPasswordChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
    {
        if (dp is PasswordBox passwordBox)
        {
            bool wasBound = (bool)(e.OldValue);
            bool needToBind = (bool)(e.NewValue);

            if (wasBound)
            {
                passwordBox.PasswordChanged -= HandlePasswordChanged;
            }

            if (needToBind)
            {   
                // Устанавливаем начальное значение
                SetBoundPassword(passwordBox, passwordBox.Password);
                passwordBox.PasswordChanged += HandlePasswordChanged;
            }
        }
    }
} 