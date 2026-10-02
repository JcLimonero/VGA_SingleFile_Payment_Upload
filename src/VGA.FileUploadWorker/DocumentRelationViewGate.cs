using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace VGA.FileUploadWorker;

public sealed class DocumentRelationViewGate : IDocumentRelationViewGate
{
    private static readonly Regex SafeIdentifier = new("^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<DocumentRelationMysqlOptions> _options;
    private readonly ILogger<DocumentRelationViewGate> _logger;
    private int _missingConfigLogged;

    public DocumentRelationViewGate(
        IConfiguration configuration,
        IOptionsMonitor<DocumentRelationMysqlOptions> options,
        ILogger<DocumentRelationViewGate> logger)
    {
        _configuration = configuration;
        _options = options;
        _logger = logger;
    }

    public async Task<DocumentRelationLookupResult> LookupRelatedRowAsync(
        string abbreviation,
        string orderDms,
        CancellationToken cancellationToken)
    {
        var cs = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Interlocked.Exchange(ref _missingConfigLogged, 1) == 0)
                _logger.LogWarning("DocumentRelationMysql: sin cadena de conexión; no se moverá ningún archivo a PROCESADOS.");
            return new DocumentRelationLookupResult(DocumentRelationGateVerdict.MissingConfiguration, null);
        }

        var view = _options.CurrentValue.ViewName.Trim();
        var idCol = _options.CurrentValue.IdFileColumnName.Trim();
        if (!SafeIdentifier.IsMatch(view) || !SafeIdentifier.IsMatch(idCol))
        {
            _logger.LogError("DocumentRelationMysql: ViewName o IdFileColumnName no válidos: {View}, {Col}", view, idCol);
            return new DocumentRelationLookupResult(DocumentRelationGateVerdict.QueryError, null);
        }

        var abbr = abbreviation.Trim();
        var order = orderDms.Trim();
        if (abbr.Length == 0 || order.Length == 0)
            return new DocumentRelationLookupResult(DocumentRelationGateVerdict.NoRelatedRow, null);

        var sql = $"""
            SELECT `{idCol}`
            FROM `{view}`
            WHERE UPPER(TRIM(abbreviation)) = UPPER(TRIM(@abbr))
              AND TRIM(CAST(order_dms AS CHAR)) = TRIM(@order)
            LIMIT 1;
            """;

        try
        {
            await using var conn = new MySqlConnection(cs);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@abbr", abbr);
            cmd.Parameters.AddWithValue("@order", order);
            var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is null || scalar is DBNull)
                return new DocumentRelationLookupResult(DocumentRelationGateVerdict.NoRelatedRow, null);

            long idFile;
            try
            {
                idFile = Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "La columna {Col} no devolvió un IdFile numérico: {Value}", idCol, scalar);
                return new DocumentRelationLookupResult(DocumentRelationGateVerdict.QueryError, null);
            }

            return new DocumentRelationLookupResult(DocumentRelationGateVerdict.RelatedRowFound, idFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar la vista {View} para agencia={Agency} pedido={Order}", view, abbr, order);
            return new DocumentRelationLookupResult(DocumentRelationGateVerdict.QueryError, null);
        }
    }

    private string? ResolveConnectionString()
    {
        var full = _configuration.GetConnectionString("DocumentRelationMysql");
        if (!string.IsNullOrWhiteSpace(full))
            return full.Trim();

        var o = _options.CurrentValue;
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
