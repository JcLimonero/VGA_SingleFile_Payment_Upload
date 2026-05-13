using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace VGA.FileUploadWorker;

public sealed class SqlitePendingImportRetryStore : IPendingImportRetryStore
{
    private readonly ISqliteConnectionProvider _sqlite;
    private readonly IOptionsMonitor<UploadOptions> _uploadOptions;
    private readonly ILogger<SqlitePendingImportRetryStore> _logger;

    public SqlitePendingImportRetryStore(
        ISqliteConnectionProvider sqlite,
        IOptionsMonitor<UploadOptions> uploadOptions,
        ILogger<SqlitePendingImportRetryStore> logger)
    {
        _sqlite = sqlite;
        _uploadOptions = uploadOptions;
        _logger = logger;
    }

    public async Task<bool> ShouldDeferAsync(string sourceRelativePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new SqliteConnection(_sqlite.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT next_retry_utc FROM stalled_import_path WHERE source_relative_path = @p LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("@p", sourceRelativePath);
            var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is null || scalar is DBNull)
                return false;
            if (!DateTimeOffset.TryParse((string)scalar, null, System.Globalization.DateTimeStyles.RoundtripKind, out var next))
                return false;
            return DateTimeOffset.UtcNow < next;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer stalled_import_path; se intenta procesar: {Path}", sourceRelativePath);
            return false;
        }
    }

    public async Task ScheduleRetryAsync(string sourceRelativePath, string lastOutcome, CancellationToken cancellationToken)
    {
        var minutes = Math.Clamp(_uploadOptions.CurrentValue.RetryPendingIntervalMinutes, 1, 7 * 24 * 60);
        var next = DateTimeOffset.UtcNow.AddMinutes(minutes);
        try
        {
            await using var conn = new SqliteConnection(_sqlite.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO stalled_import_path (source_relative_path, next_retry_utc, last_outcome)
                VALUES (@p, @next, @outcome)
                ON CONFLICT(source_relative_path) DO UPDATE SET
                    next_retry_utc = excluded.next_retry_utc,
                    last_outcome = excluded.last_outcome;
                """;
            cmd.Parameters.AddWithValue("@p", sourceRelativePath);
            cmd.Parameters.AddWithValue("@next", next.ToString("O"));
            cmd.Parameters.AddWithValue("@outcome", lastOutcome);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar reintento programado para {Path}", sourceRelativePath);
        }
    }

    public async Task ClearAsync(string sourceRelativePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = new SqliteConnection(_sqlite.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM stalled_import_path WHERE source_relative_path = @p;";
            cmd.Parameters.AddWithValue("@p", sourceRelativePath);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo limpiar stalled_import_path para {Path}", sourceRelativePath);
        }
    }
}
