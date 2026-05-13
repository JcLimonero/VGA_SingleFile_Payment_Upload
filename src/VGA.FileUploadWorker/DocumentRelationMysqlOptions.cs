namespace VGA.FileUploadWorker;

public sealed class DocumentRelationMysqlOptions
{
    public const string SectionName = "DocumentRelationMysql";

    public string Server { get; set; } = "";

    public uint Port { get; set; } = 3306;

    public string Database { get; set; } = "";

    public string UserId { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>Columna en la vista que aporta el IdFile para documentbyfile (debe existir en el SELECT).</summary>
    public string IdFileColumnName { get; set; } = "IdFile";

    /// <summary>Vista a consultar (solo identificador seguro).</summary>
    public string ViewName { get; set; } = "view_upload_documents_relation";
}
