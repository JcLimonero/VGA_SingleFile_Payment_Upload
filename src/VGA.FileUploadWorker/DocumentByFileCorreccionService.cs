using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace VGA.FileUploadWorker;

public sealed class DocumentByFileCorreccionService : IDocumentByFileCorreccionService
{
    private static readonly Regex SafeIdentifier = new("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    private readonly IDocumentRelationMysqlConnectionProvider _mysqlConnection;
    private readonly IOptionsMonitor<DocumentByFileMysqlOptions> _options;
    private readonly ILogger<DocumentByFileCorreccionService> _logger;

    public DocumentByFileCorreccionService(
        IDocumentRelationMysqlConnectionProvider mysqlConnection,
        IOptionsMonitor<DocumentByFileMysqlOptions> options,
        ILogger<DocumentByFileCorreccionService> logger)
    {
        _mysqlConnection = mysqlConnection;
        _options = options;
        _logger = logger;
    }

    public async Task<DocumentByFileCorreccionResult> TryDisableByPathDocumentAsync(
        string pathDocumentFileName,
        CancellationToken cancellationToken)
    {
        var o = _options.CurrentValue;
        if (!o.DisableOnCorreccionScan)
            return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.Failed, 0, "DocumentByFile:DisableOnCorreccionScan está deshabilitado.");

        var table = o.TableName.Trim();
        if (!SafeIdentifier.IsMatch(table))
        {
            _logger.LogError("DocumentByFile:TableName no es un identificador válido: {Table}", table);
            return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.Failed, 0, "TableName inválido.");
        }

        var cs = _mysqlConnection.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.Failed, 0, "Sin cadena MySQL.");

        var pathDoc = pathDocumentFileName.Trim();
        if (string.IsNullOrEmpty(pathDoc))
            return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.Failed, 0, "PathDocument vacío.");

        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTime.Now;
            var updateSql = $"""
                UPDATE `{table}`
                SET Enabled = 0, UpdateDate = @upd, LastUserUpdate = @lastUser, IdLastUserUpdate = @idLastUser
                WHERE PathDocument = @pathDoc AND Enabled <> 0;
                """;

            int rowsAffected;
            await using (var cmd = new MySqlCommand(updateSql, conn))
            {
                cmd.Parameters.Add("@upd", MySqlDbType.DateTime).Value = now;
                cmd.Parameters.AddWithValue("@lastUser", o.LastUserUpdate);
                cmd.Parameters.AddWithValue("@idLastUser", o.IdLastUserUpdate);
                cmd.Parameters.AddWithValue("@pathDoc", pathDoc);
                rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (rowsAffected > 0)
                return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.Disabled, rowsAffected, null);

            var existsSql = $"""
                SELECT 1 FROM `{table}` WHERE PathDocument = @pathDoc AND Enabled = 0 LIMIT 1;
                """;
            await using (var existsCmd = new MySqlCommand(existsSql, conn))
            {
                existsCmd.Parameters.AddWithValue("@pathDoc", pathDoc);
                var exists = await existsCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (exists is not null and not DBNull)
                    return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.AlreadyDisabled, 0, null);
            }

            return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.NotFound, 0, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al desactivar documentbyfile para PathDocument={PathDocument}", pathDoc);
            return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.Failed, 0, ex.Message);
        }
    }

    public async Task<DocumentByFileCorreccionResult> TryDisableByIdAsync(
        long documentByFileId,
        CancellationToken cancellationToken)
    {
        var o = _options.CurrentValue;
        var table = o.TableName.Trim();
        var idCol = o.IdColumnName.Trim();
        if (!SafeIdentifier.IsMatch(table) || !SafeIdentifier.IsMatch(idCol))
            return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.Failed, 0, "TableName o IdColumnName inválido.");

        var cs = _mysqlConnection.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.Failed, 0, "Sin cadena MySQL.");

        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTime.Now;
            var updateSql = $"""
                UPDATE `{table}`
                SET Enabled = 0, UpdateDate = @upd, LastUserUpdate = @lastUser, IdLastUserUpdate = @idLastUser
                WHERE `{idCol}` = @id AND Enabled <> 0;
                """;

            int rowsAffected;
            await using (var cmd = new MySqlCommand(updateSql, conn))
            {
                cmd.Parameters.Add("@upd", MySqlDbType.DateTime).Value = now;
                cmd.Parameters.AddWithValue("@lastUser", o.LastUserUpdate);
                cmd.Parameters.AddWithValue("@idLastUser", o.IdLastUserUpdate);
                cmd.Parameters.AddWithValue("@id", documentByFileId);
                rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (rowsAffected > 0)
                return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.Disabled, rowsAffected, null);

            var existsSql = $"""
                SELECT 1 FROM `{table}` WHERE `{idCol}` = @id AND Enabled = 0 LIMIT 1;
                """;
            await using (var existsCmd = new MySqlCommand(existsSql, conn))
            {
                existsCmd.Parameters.AddWithValue("@id", documentByFileId);
                var exists = await existsCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (exists is not null and not DBNull)
                    return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.AlreadyDisabled, 0, null);
            }

            return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.NotFound, 0, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al desactivar documentbyfile Id={Id}", documentByFileId);
            return new DocumentByFileCorreccionResult(DocumentByFileCorreccionStatus.Failed, 0, ex.Message);
        }
    }
}
