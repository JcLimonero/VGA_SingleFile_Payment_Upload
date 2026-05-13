using Microsoft.Data.Sqlite;

namespace VGA.FileUploadWorker;

public static class SqliteSchema
{
    public static async Task EnsureCreatedAsync(ISqliteConnectionProvider provider, CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection(provider.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS PaymentFileUploads (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                OriginalFileName TEXT NOT NULL,
                SourceRelativePath TEXT NOT NULL,
                Channel TEXT NOT NULL,
                AgencyAbbreviation TEXT NULL,
                OrderNumber TEXT NULL,
                FileSizeBytes INTEGER NOT NULL,
                FileContent BLOB NULL,
                ContentSha256 BLOB NULL,
                ImportedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS FileObtainedLog (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                LogDate TEXT NOT NULL,
                OccurredUtc TEXT NOT NULL,
                OriginalFileName TEXT NOT NULL,
                SourceRelativePath TEXT NOT NULL,
                Channel TEXT NOT NULL,
                AgencyAbbreviation TEXT NULL,
                OrderNumber TEXT NULL,
                Outcome TEXT NOT NULL,
                Detail TEXT NULL,
                PaymentUploadId INTEGER NULL
            );

            CREATE INDEX IF NOT EXISTS IX_FileObtainedLog_LogDate ON FileObtainedLog (LogDate);
            CREATE INDEX IF NOT EXISTS IX_FileObtainedLog_OccurredUtc ON FileObtainedLog (OccurredUtc);
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
