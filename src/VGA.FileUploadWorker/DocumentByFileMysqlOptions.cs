namespace VGA.FileUploadWorker;

public sealed class DocumentByFileMysqlOptions
{
    public const string SectionName = "DocumentByFile";

    /// <summary>Tabla destino (solo identificador seguro).</summary>
    public string TableName { get; set; } = "documentbyfile";

    public int LastUserUpdate { get; set; } = 111;

    public int IdLastUserUpdate { get; set; } = 111;

    public string IdValidation { get; set; } = "21";

    public int IdDocumentType { get; set; } = 1;

    public int IdCurrentStatus { get; set; } = 0;

    public int IdDocumentError { get; set; } = 0;

    public string ServerPath { get; set; } = "";

    public string IdDocumentContainer { get; set; } = "path por definir";

    /// <summary>Prefijo del campo Name, p. ej. Liquidacion_.</summary>
    public string NamePrefix { get; set; } = "Liquidacion_";
}
