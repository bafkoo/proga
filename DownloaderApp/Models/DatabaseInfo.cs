namespace DownloaderApp.Models;

/// <summary>
/// Представляет информацию о доступной базе данных.
/// </summary>
public class DatabaseInfo
{
    // В простом случае может быть достаточно только имени
    public string Name { get; set; }

    // Добавляем отображаемое имя
    public string DisplayName { get; set; }

    // Можно добавить другие свойства, если они нужны (например, сервер)

    // Переопределяем ToString для удобства отображения, если не используется DisplayMemberPath
    // Теперь ToString будет возвращать DisplayName, если оно задано
    public override string ToString() => DisplayName ?? Name;
} 