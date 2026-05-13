namespace VGA.FileUploadWorker;

public sealed class SqliteConnectionProvider : ISqliteConnectionProvider
{
    public string ConnectionString { get; }

    public SqliteConnectionProvider(IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("PaymentsDb");
        if (string.IsNullOrWhiteSpace(raw))
            raw = Path.Combine("Data", "payments.db");

        raw = raw.Trim();
        if (raw.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase))
        {
            EnsureDirectoryForDataSource(raw);
            ConnectionString = raw;
            return;
        }

        var fullPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, raw.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        ConnectionString = $"Data Source={fullPath};Mode=ReadWriteCreate;Cache=Shared";
    }

    private static void EnsureDirectoryForDataSource(string connectionString)
    {
        var marker = "Data Source=";
        var idx = connectionString.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return;
        var start = idx + marker.Length;
        var end = connectionString.IndexOf(';', start);
        var path = end < 0 ? connectionString[start..] : connectionString[start..end];
        path = path.Trim().Trim('"');
        if (path.Length == 0 || path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            return;
        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var d = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(d))
            Directory.CreateDirectory(d);
    }
}
