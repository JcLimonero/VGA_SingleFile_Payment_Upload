namespace VGA.FileUploadWorker;

public interface ISqliteConnectionProvider
{
    /// <summary>Cadena de conexión SQLite completa (incluye Data Source=...).</summary>
    string ConnectionString { get; }
}
