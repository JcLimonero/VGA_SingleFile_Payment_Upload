using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace VGA.FileUploadWorker;

public sealed class DocumentPaymentUploadQueryService : IDocumentPaymentUploadQueryService
{
    private static readonly Regex SafeIdentifier = new("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    private readonly IDocumentRelationMysqlConnectionProvider _mysqlConnection;
    private readonly IOptionsMonitor<DocumentPaymentUploadMysqlOptions> _options;
    private readonly ILogger<DocumentPaymentUploadQueryService> _logger;

    public DocumentPaymentUploadQueryService(
        IDocumentRelationMysqlConnectionProvider mysqlConnection,
        IOptionsMonitor<DocumentPaymentUploadMysqlOptions> options,
        ILogger<DocumentPaymentUploadQueryService> logger)
    {
        _mysqlConnection = mysqlConnection;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FailedUploadTrackingRow>> GetFailedUploadRowsAsync(
        string? channelFilter,
        string? errorContainsFilter,
        CancellationToken cancellationToken)
    {
        var o = _options.CurrentValue;
        if (!o.Enabled)
        {
            _logger.LogWarning("DocumentPaymentUpload:Enabled=false; no hay filas que consultar.");
            return Array.Empty<FailedUploadTrackingRow>();
        }

        var table = o.TableName.Trim();
        if (!SafeIdentifier.IsMatch(table))
        {
            _logger.LogError("DocumentPaymentUpload:TableName no es un identificador válido: {Table}", table);
            return Array.Empty<FailedUploadTrackingRow>();
        }

        var cs = _mysqlConnection.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
        {
            _logger.LogError("DocumentPaymentUpload: sin cadena MySQL.");
            return Array.Empty<FailedUploadTrackingRow>();
        }

        var sql = $"""
            SELECT Id, PaymentUploadId, SourceRelativePath, Channel, OriginalFileName,
                   ProcessSucceeded, ProcessedFullPath, FinalFileName, DocumentByFileId, CloudLastError
            FROM `{table}`
            WHERE CloudLastError IS NOT NULL
              AND DocumentByFileId IS NOT NULL
              AND CloudLastError NOT LIKE '%(recovered %'
            ORDER BY PaymentUploadId, Id;
            """;

        var rows = new List<FailedUploadTrackingRow>();
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new MySqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var channel = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var cloudError = reader.IsDBNull(9) ? "" : reader.GetString(9);

                if (!string.IsNullOrWhiteSpace(channelFilter)
                    && !string.Equals(channel, channelFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(errorContainsFilter)
                    && cloudError.IndexOf(errorContainsFilter.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                rows.Add(new FailedUploadTrackingRow(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    channel,
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    !reader.IsDBNull(5) && reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8),
                    cloudError));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DocumentPaymentUpload: error al consultar filas con CloudLastError.");
        }

        return rows;
    }

    public async Task MarkAttemptRecoveredAsync(IReadOnlyList<long> trackingIds, CancellationToken cancellationToken)
    {
        if (trackingIds.Count == 0)
            return;

        var o = _options.CurrentValue;
        if (!o.Enabled)
            return;

        var table = o.TableName.Trim();
        if (!SafeIdentifier.IsMatch(table))
            return;

        var cs = _mysqlConnection.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return;

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = $" (recovered {stamp})";

        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var id in trackingIds)
            {
                var sql = $"""
                    UPDATE `{table}`
                    SET ProcessSucceeded = 0,
                        ProcessedUtc = NULL,
                        ProcessedFullPath = NULL,
                        FinalFileName = NULL,
                        CloudLastError = CONCAT(COALESCE(CloudLastError, ''), @suffix),
                        UpdatedUtc = @u
                    WHERE Id = @id;
                    """;

                await using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@suffix", suffix);
                cmd.Parameters.Add("@u", MySqlDbType.DateTime).Value = DateTime.UtcNow;
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DocumentPaymentUpload: error al marcar intentos como recuperados.");
            throw;
        }
    }
}
