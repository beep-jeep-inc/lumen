namespace Pult.Core.Cleanup;

/// Файлы «только в облаке» (OneDrive Files On-Demand и подобные): Length — полный размер, а на диске файл не занимает ничего.
/// Чтение такого файла (для хеша, предпросмотра) скачивает его целиком, а удаление в Корзину стирает его из облака.
public static class CloudFiles
{
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;   // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;         // FILE_ATTRIBUTE_RECALL_ON_OPEN
    public const FileAttributes OfflineAttr = (FileAttributes)0x00001000;          // FILE_ATTRIBUTE_OFFLINE

    public const string Reason = "файл только в облаке";

    public static bool IsCloudOnly(FileAttributes a) => (a & (RecallOnDataAccess | RecallOnOpen | OfflineAttr)) != 0;

    /// Атрибуты файла; attrOf — подставной читатель для тестов (по умолчанию настоящие).
    public static bool IsCloudOnly(FileSystemInfo i, Func<FileSystemInfo, FileAttributes>? attrOf = null)
    {
        try { return IsCloudOnly(attrOf?.Invoke(i) ?? i.Attributes); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
