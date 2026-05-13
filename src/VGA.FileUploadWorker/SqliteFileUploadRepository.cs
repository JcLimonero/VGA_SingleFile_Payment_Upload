using Microsoft.Data.Sqlite;

namespace VGA.FileUploadWorker;

public sealed class SqliteFileUploadRepository : IFileUploadRepository
{
    private readonly ISqliteConnectionProvider _provider;

    public SqliteFileUploadRepository(ISqliteConnectionProvider provider)
    {
        _provider = provider;
    }

    public async Task<long> InsertUploadAsync(
        string originalFileName,
        string sourceRelativePath,
        string channel,
        string? agencyAbbreviation,
        string? orderNumber,
        long fileSizeBytes,
        byte[]? fileContent,
        byte[]? contentSha256,
        CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection(_provider.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PaymentFileUploads (
                OriginalFileName, SourceRelativePath, Channel, AgencyAbbreviation, OrderNumber,
                FileSizeBytes, FileContent, ContentSha256, ImportedUtc)
            VALUES (@name, @rel, @channel, @agency, @order, @size, @content, @hash, @importedUtc)
            RETURNING Id;
            """;
        cmd.Parameters.AddWithValue("@name", originalFileName);
        cmd.Parameters.AddWithValue("@rel", sourceRelativePath);
        cmd.Parameters.AddWithValue("@channel", channel);
        cmd.Parameters.AddWithValue("@agency", agencyAbbreviation ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@order", orderNumber ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@size", fileSizeBytes);
        cmd.Parameters.AddWithValue("@content", fileContent ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", contentSha256 ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@importedUtc", DateTimeOffset.UtcNow.ToString("O"));

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long id ? id : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
