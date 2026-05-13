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
        var name = $"{o.NamePrefix}{originalFileName}";
        var now = DateTime.Now;

        var sql = $"""
            INSERT INTO `{table}` (
                Name, Comment, ExperationDate, PathDocument, Enabled,
                RegistrationDate, UpdateDate, LastUserUpdate, IdLastUserUpdate,
                IdFile, IdValidation, IdDocumentType, IdCurrentStatus, IdDocumentError,
                ServerPath, IdDocumentContainer)
            VALUES (
                @name, @comment, @expiration, @pathDoc, 1,
                @reg, @upd, @lastUser, @idLastUser,
                @idFile, @idValidation, @idDocType, @idStatus, @idErr,
                @serverPath, @idContainer);
            """;

        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@comment", "");
            cmd.Parameters.AddWithValue("@expiration", "");
            cmd.Parameters.AddWithValue("@pathDoc", originalFileName);
            cmd.Parameters.Add("@reg", MySqlDbType.DateTime).Value = now;
            cmd.Parameters.Add("@upd", MySqlDbType.DateTime).Value = now;
            cmd.Parameters.AddWithValue("@lastUser", o.LastUserUpdate);
            cmd.Parameters.AddWithValue("@idLastUser", o.IdLastUserUpdate);
            cmd.Parameters.AddWithValue("@idFile", idFile);
            cmd.Parameters.AddWithValue("@idValidation", o.IdValidation);
            cmd.Parameters.AddWithValue("@idDocType", o.IdDocumentType);
            cmd.Parameters.AddWithValue("@idStatus", o.IdCurrentStatus);
            cmd.Parameters.AddWithValue("@idErr", o.IdDocumentError);
            cmd.Parameters.AddWithValue("@serverPath", o.ServerPath);
            cmd.Parameters.AddWithValue("@idContainer", o.IdDocumentContainer);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al insertar en documentbyfile para archivo {File}, IdFile={IdFile}", originalFileName, idFile);
            return false;
        }
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
