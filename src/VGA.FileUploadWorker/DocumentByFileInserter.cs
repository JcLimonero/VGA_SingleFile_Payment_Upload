using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace VGA.FileUploadWorker;

public sealed class DocumentByFileInserter : IDocumentByFileInserter
{
    private static readonly Regex SafeIdentifier = new("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<DocumentRelationMysqlOptions> _mysqlOptions;
    private readonly IOptionsMonitor<DocumentByFileMysqlOptions> _docOptions;
    private readonly ILogger<DocumentByFileInserter> _logger;

    public DocumentByFileInserter(
        IConfiguration configuration,
        IOptionsMonitor<DocumentRelationMysqlOptions> mysqlOptions,
        IOptionsMonitor<DocumentByFileMysqlOptions> docOptions,
        ILogger<DocumentByFileInserter> logger)
    {
        _configuration = configuration;
        _mysqlOptions = mysqlOptions;
        _docOptions = docOptions;
        _logger = logger;
    }

    public async Task<bool> TryInsertAsync(string originalFileName, long idFile, CancellationToken cancellationToken)
    {
        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
        {
            _logger.LogError("DocumentByFile: sin cadena MySQL; no se inserta documentbyfile.");
            return false;
        }

        var table = _docOptions.CurrentValue.TableName.Trim();
        if (!SafeIdentifier.IsMatch(table))
        {
            _logger.LogError("DocumentByFile:TableName no es un identificador válido: {Table}", table);
            return false;
        }

        var o = _docOptions.CurrentValue;
        var idCol = o.IdColumnName.Trim();
        if (o.GenerateIdUsingMaxPlusOne)
        {
            if (!SafeIdentifier.IsMatch(idCol))
            {
                _logger.LogError("DocumentByFile:IdColumnName no es un identificador válido: {Column}", idCol);
                return false;
            }
        }

        var name = $"{o.NamePrefix}{originalFileName}";
        var now = DateTime.Now;

        var insertColumns = o.GenerateIdUsingMaxPlusOne
            ? $"`{idCol}`, Name, Comment, ExperationDate, PathDocument, Enabled, RegistrationDate, UpdateDate, LastUserUpdate, IdLastUserUpdate, IdFile, IdValidation, IdDocumentType, IdCurrentStatus, IdDocumentError, ServerPath, IdDocumentContainer"
            : "Name, Comment, ExperationDate, PathDocument, Enabled, RegistrationDate, UpdateDate, LastUserUpdate, IdLastUserUpdate, IdFile, IdValidation, IdDocumentType, IdCurrentStatus, IdDocumentError, ServerPath, IdDocumentContainer";

        var insertValues = o.GenerateIdUsingMaxPlusOne
            ? "@idPk, @name, @comment, @expiration, @pathDoc, 1, @reg, @upd, @lastUser, @idLastUser, @idFile, @idValidation, @idDocType, @idStatus, @idErr, @serverPath, @idContainer"
            : "@name, @comment, @expiration, @pathDoc, 1, @reg, @upd, @lastUser, @idLastUser, @idFile, @idValidation, @idDocType, @idStatus, @idErr, @serverPath, @idContainer";

        var sql = $"""
            INSERT INTO `{table}` (
                {insertColumns})
            VALUES (
                {insertValues});
            """;

        var nextIdSql = $"""
            SELECT COALESCE(MAX(`{idCol}`), 0) + 1
            FROM `{table}`;
            """;

        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            long? assignedId = null;
            if (o.GenerateIdUsingMaxPlusOne)
            {
                await using (var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                {
                    await using (var nextCmd = new MySqlCommand(nextIdSql, conn, tx))
                    {
                        var scalar = await nextCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                        assignedId = Convert.ToInt64(scalar);
                    }

                    await using (var cmd = new MySqlCommand(sql, conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@idPk", assignedId.Value);
                        AddInsertParameters(cmd, name, originalFileName, now, idFile, o);
                        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                return true;
            }

            await using (var cmd = new MySqlCommand(sql, conn))
            {
                AddInsertParameters(cmd, name, originalFileName, now, idFile, o);
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al insertar en documentbyfile para archivo {File}, IdFile={IdFile}", originalFileName, idFile);
            return false;
        }
    }

    /// <summary>0 y ausencia de valor suelen significar “sin error”; 0 no cumple FK en muchas bases.</summary>
    private static object ResolveIdDocumentErrorParameter(DocumentByFileMysqlOptions o)
    {
        if (!o.IdDocumentError.HasValue || o.IdDocumentError.Value == 0)
            return DBNull.Value;
        return o.IdDocumentError.Value;
    }

    private static void AddInsertParameters(MySqlCommand cmd, string name, string originalFileName, DateTime now, long idFile, DocumentByFileMysqlOptions o)
    {
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@comment", "");
        cmd.Parameters.AddWithValue("@expiration", DBNull.Value);
        cmd.Parameters.AddWithValue("@pathDoc", originalFileName);
        cmd.Parameters.Add("@reg", MySqlDbType.DateTime).Value = now;
        cmd.Parameters.Add("@upd", MySqlDbType.DateTime).Value = now;
        cmd.Parameters.AddWithValue("@lastUser", o.LastUserUpdate);
        cmd.Parameters.AddWithValue("@idLastUser", o.IdLastUserUpdate);
        cmd.Parameters.AddWithValue("@idFile", idFile);
        cmd.Parameters.AddWithValue("@idValidation", o.IdValidation);
        cmd.Parameters.AddWithValue("@idDocType", o.IdDocumentType);
        cmd.Parameters.AddWithValue("@idStatus", o.IdCurrentStatus);
        cmd.Parameters.AddWithValue("@idErr", ResolveIdDocumentErrorParameter(o));
        cmd.Parameters.AddWithValue("@serverPath", o.ServerPath);
        cmd.Parameters.AddWithValue("@idContainer", o.IdDocumentContainer);
    }

    private string? ResolveConnectionString()
    {
        var full = _configuration.GetConnectionString("DocumentRelationMysql");
        if (!string.IsNullOrWhiteSpace(full))
            return full.Trim();

        var o = _mysqlOptions.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.Server) || string.IsNullOrWhiteSpace(o.Database)
                                               || string.IsNullOrWhiteSpace(o.UserId)
                                               || string.IsNullOrWhiteSpace(o.Password))
            return null;

        var b = new MySqlConnectionStringBuilder
        {
            Server = o.Server,
            Port = o.Port,
            Database = o.Database,
            UserID = o.UserId,
            Password = o.Password,
        };
        return b.ConnectionString;
    }
}
