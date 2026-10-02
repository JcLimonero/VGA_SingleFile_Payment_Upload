using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace VGA.FileUploadWorker;

public sealed class DocumentRelationMysqlConnectionProvider : IDocumentRelationMysqlConnectionProvider
{
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<DocumentRelationMysqlOptions> _mysqlOptions;

    public DocumentRelationMysqlConnectionProvider(
        IConfiguration configuration,
        IOptionsMonitor<DocumentRelationMysqlOptions> mysqlOptions)
    {
        _configuration = configuration;
        _mysqlOptions = mysqlOptions;
    }

    public string? GetConnectionString()
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
