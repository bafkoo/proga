namespace DownloaderApp.Models;

/// <summary>
/// Представляет информацию о доступной базе данных.
/// </summary>
public class DatabaseInfo
{
    // В простом случае может быть достаточно только имени
    public string Name { get; set; }

    // Можно добавить другие свойства, если они нужны (например, сервер)

    // Переопределяем ToString для удобства отображения, если не используется DisplayMemberPath
    public override string ToString() => Name;
} 