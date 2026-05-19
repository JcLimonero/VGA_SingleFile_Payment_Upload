using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace VGA.FileUploadWorker;

public sealed class DocumentPaymentUploadTracker : IDocumentPaymentUploadTracker
{
    private static readonly Regex SafeIdentifier = new("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    private readonly IDocumentRelationMysqlConnectionProvider _mysqlConnection;
    private readonly IOptionsMonitor<DocumentPaymentUploadMysqlOptions> _options;
    private readonly ILogger<DocumentPaymentUploadTracker> _logger;

    public DocumentPaymentUploadTracker(
        IDocumentRelationMysqlConnectionProvider mysqlConnection,
        IOptionsMonitor<DocumentPaymentUploadMysqlOptions> options,
        ILogger<DocumentPaymentUploadTracker> logger)
    {
        _mysqlConnection = mysqlConnection;
        _options = options;
        _logger = logger;
    }

    public async Task<long?> InsertRowAfterSqliteAsync(
        string sourceRelativePath,
        string channel,
        string originalFileName,
        string? agencyAbbreviation,
        string? orderNumber,
        long idFile,
        long paymentUploadId,
        CancellationToken cancellationToken)
    {
        var o = _options.CurrentValue;
        if (!o.Enabled)
            return null;

        var table = o.TableName.Trim();
        if (!SafeIdentifier.IsMatch(table))
        {
            _logger.LogError("DocumentPaymentUpload:TableName no es un identificador válido: {Table}", table);
            return null;
        }

        var cs = _mysqlConnection.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
        {
            _logger.LogWarning("DocumentPaymentUpload: sin cadena MySQL; no se inserta fila de seguimiento.");
            return null;
        }

        var now = DateTime.UtcNow;
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var insert = $"""
                INSERT INTO `{table}` (
                    DiscoveredUtc, SourceRelativePath, Channel, OriginalFileName, AgencyAbbreviation, OrderNumber,
                    IdFile, PaymentUploadId, DocumentByFileId, ProcessSucceeded, ProcessedUtc, ProcessedFullPath, FinalFileName,
                    CloudUploadAttemptCount, CloudLastAttemptUtc, CloudUploadSucceeded, CloudUploadedUtc, CloudLastError, UpdatedUtc)
                VALUES (
                    @discovered, @rel, @channel, @name, @agency, @order,
                    @idFile, @paymentId, NULL, 0, NULL, NULL, NULL,
                    0, NULL, 0, NULL, NULL, @updated);
                """;

            await using (var cmdInsert = new MySqlCommand(insert, conn))
            {
                cmdInsert.Parameters.Add("@discovered", MySqlDbType.DateTime).Value = now;
                cmdInsert.Parameters.AddWithValue("@rel", Truncate(sourceRelativePath, 1024));
                cmdInsert.Parameters.AddWithValue("@channel", Truncate(channel, 128));
                cmdInsert.Parameters.AddWithValue("@name", Truncate(originalFileName, 512));
                cmdInsert.Parameters.AddWithValue("@agency", string.IsNullOrEmpty(agencyAbbreviation) ? DBNull.Value : Truncate(agencyAbbreviation, 64));
                cmdInsert.Parameters.AddWithValue("@order", string.IsNullOrEmpty(orderNumber) ? DBNull.Value : Truncate(orderNumber, 128));
                cmdInsert.Parameters.AddWithValue("@idFile", idFile);
                cmdInsert.Parameters.AddWithValue("@paymentId", paymentUploadId);
                cmdInsert.Parameters.Add("@updated", MySqlDbType.DateTime).Value = now;
                await cmdInsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var cmdLid = new MySqlCommand("SELECT LAST_INSERT_ID();", conn))
            {
                var scalar = await cmdLid.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (scalar is null or DBNull)
                    return null;
                return scalar is ulong u ? unchecked((long)u) : Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DocumentPaymentUpload: error al insertar fila de seguimiento para {Path}", sourceRelativePath);
            return null;
        }
    }

    public Task MarkDocumentByFileAsync(long trackingId, long documentByFileId, CancellationToken cancellationToken) =>
        ExecuteUpdateAsync(
            trackingId,
            """
            UPDATE `{0}` SET DocumentByFileId = @docId, UpdatedUtc = @u WHERE Id = @id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@docId", documentByFileId);
                cmd.Parameters.Add("@u", MySqlDbType.DateTime).Value = DateTime.UtcNow;
                cmd.Parameters.AddWithValue("@id", trackingId);
            },
            cancellationToken);

    public Task MarkProcessedAsync(long trackingId, string processedFullPath, string finalFileName, CancellationToken cancellationToken) =>
        ExecuteUpdateAsync(
            trackingId,
            """
            UPDATE `{0}` SET ProcessSucceeded = 1, ProcessedUtc = @p, ProcessedFullPath = @path, FinalFileName = @fn, UpdatedUtc = @u WHERE Id = @id;
            """,
            cmd =>
            {
                cmd.Parameters.Add("@p", MySqlDbType.DateTime).Value = DateTime.UtcNow;
                cmd.Parameters.AddWithValue("@path", Truncate(processedFullPath, 2048));
                cmd.Parameters.AddWithValue("@fn", Truncate(finalFileName, 512));
                cmd.Parameters.Add("@u", MySqlDbType.DateTime).Value = DateTime.UtcNow;
                cmd.Parameters.AddWithValue("@id", trackingId);
            },
            cancellationToken);

    public Task MarkProcessFailedAsync(long trackingId, string? detail, CancellationToken cancellationToken) =>
        ExecuteUpdateAsync(
            trackingId,
            """
            UPDATE `{0}` SET ProcessSucceeded = 0, CloudLastError = @err, UpdatedUtc = @u WHERE Id = @id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@err", string.IsNullOrEmpty(detail) ? DBNull.Value : Truncate(detail!, 2000));
                cmd.Parameters.Add("@u", MySqlDbType.DateTime).Value = DateTime.UtcNow;
                cmd.Parameters.AddWithValue("@id", trackingId);
            },
            cancellationToken);

    public Task NotifyCloudAttemptStartingAsync(long trackingId, CancellationToken cancellationToken) =>
        ExecuteUpdateAsync(
            trackingId,
            """
            UPDATE `{0}` SET CloudUploadAttemptCount = CloudUploadAttemptCount + 1, CloudLastAttemptUtc = @a, UpdatedUtc = @u WHERE Id = @id;
            """,
            cmd =>
            {
                cmd.Parameters.Add("@a", MySqlDbType.DateTime).Value = DateTime.UtcNow;
                cmd.Parameters.Add("@u", MySqlDbType.DateTime).Value = DateTime.UtcNow;
                cmd.Parameters.AddWithValue("@id", trackingId);
            },
            cancellationToken);

    public Task MarkCloudOutcomeAsync(long trackingId, bool success, string? errorDetail, CancellationToken cancellationToken) =>
        success
            ? ExecuteUpdateAsync(
                trackingId,
                """
                UPDATE `{0}` SET CloudUploadSucceeded = 1, CloudUploadedUtc = @up, CloudLastError = NULL, UpdatedUtc = @u WHERE Id = @id;
                """,
                cmd =>
                {
                    var t = DateTime.UtcNow;
                    cmd.Parameters.Add("@up", MySqlDbType.DateTime).Value = t;
                    cmd.Parameters.Add("@u", MySqlDbType.DateTime).Value = t;
                    cmd.Parameters.AddWithValue("@id", trackingId);
                },
                cancellationToken)
            : ExecuteUpdateAsync(
                trackingId,
                """
                UPDATE `{0}` SET CloudUploadSucceeded = 0, CloudLastError = @err, UpdatedUtc = @u WHERE Id = @id;
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@err", string.IsNullOrEmpty(errorDetail) ? DBNull.Value : Truncate(errorDetail!, 2000));
                    cmd.Parameters.Add("@u", MySqlDbType.DateTime).Value = DateTime.UtcNow;
                    cmd.Parameters.AddWithValue("@id", trackingId);
                },
                cancellationToken);

    private async Task ExecuteUpdateAsync(
        long trackingId,
        string sqlTemplate,
        Action<MySqlCommand> bind,
        CancellationToken cancellationToken)
    {
        var o = _options.CurrentValue;
        if (!o.Enabled)
            return;

        var table = o.TableName.Trim();
        if (!SafeIdentifier.IsMatch(table))
            return;

        var cs = _mysqlConnection.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return;

        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, sqlTemplate, table);
        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new MySqlCommand(sql, conn);
            bind(cmd);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DocumentPaymentUpload: error al actualizar Id={Id}", trackingId);
        }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s!.Length <= max ? s : s[..max];
    }
}
