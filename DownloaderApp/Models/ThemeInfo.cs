namespace FileDownloader.Models;

/// <summary>
/// Представляет информацию о доступной теме.
/// </summary>
public class ThemeInfo
{
    public int Id { get; set; } // Идентификатор темы
    public string Name { get; set; } // Название темы

    // Переопределяем ToString для удобства отображения
    public override string ToString() => $"{Name} (ID: {Id})";
} 