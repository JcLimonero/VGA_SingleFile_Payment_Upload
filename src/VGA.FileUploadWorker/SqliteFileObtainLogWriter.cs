using Microsoft.Data.Sqlite;

namespace VGA.FileUploadWorker;

public sealed class SqliteFileObtainLogWriter : IFileObtainLogWriter
{
    private readonly ISqliteConnectionProvider _provider;
    private readonly ILogger<SqliteFileObtainLogWriter> _logger;

    public SqliteFileObtainLogWriter(ISqliteConnectionProvider provider, ILogger<SqliteFileObtainLogWriter> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public async Task WriteAsync(FileObtainedLogEntry entry, CancellationToken cancellationToken)
    {
        var logDate = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var occurred = DateTimeOffset.UtcNow.ToString("O");

        try
        {
            await using var conn = new SqliteConnection(_provider.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO FileObtainedLog (
                    LogDate, OccurredUtc, OriginalFileName, SourceRelativePath, Channel,
                    AgencyAbbreviation, OrderNumber, Outcome, Detail, PaymentUploadId)
                VALUES (@logDate, @occurred, @name, @rel, @channel, @agency, @order, @outcome, @detail, @uploadId);
                """;
            cmd.Parameters.AddWithValue("@logDate", logDate);
            cmd.Parameters.AddWithValue("@occurred", occurred);
            cmd.Parameters.AddWithValue("@name", entry.OriginalFileName);
            cmd.Parameters.AddWithValue("@rel", entry.SourceRelativePath);
            cmd.Parameters.AddWithValue("@channel", entry.Channel);
            cmd.Parameters.AddWithValue("@agency", entry.AgencyAbbreviation ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@order", entry.OrderNumber ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@outcome", entry.Outcome);
            cmd.Parameters.AddWithValue("@detail", Truncate(entry.Detail, 2000) ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@uploadId", entry.PaymentUploadId ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo escribir FileObtainedLog para {File}", entry.SourceRelativePath);
        }

        _logger.LogInformation(
            "Archivo obtenido [{Outcome}] {Path} | Agencia={Agency} | Pedido={Order} | IdCarga={UploadId} | {Detail}",
            entry.Outcome,
            entry.SourceRelativePath,
            entry.AgencyAbbreviation ?? "-",
            entry.OrderNumber ?? "-",
            entry.PaymentUploadId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            entry.Detail ?? "");
    }

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        return s.Length <= max ? s : s[..max];
    }
}
