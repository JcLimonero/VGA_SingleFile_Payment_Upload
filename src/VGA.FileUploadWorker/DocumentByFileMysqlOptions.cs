namespace VGA.FileUploadWorker;

public sealed class DocumentByFileMysqlOptions
{
    public const string SectionName = "DocumentByFile";

    /// <summary>Tabla destino (solo identificador seguro).</summary>
    public string TableName { get; set; } = "documentbyfile";

    /// <summary>Columna PK numérica cuando la tabla no usa AUTO_INCREMENT.</summary>
    public string IdColumnName { get; set; } = "Id";

    /// <summary>
    /// Si es true, asigna <see cref="IdColumnName"/> con COALESCE(MAX(Id),0)+1 en una transacción antes del INSERT.
    /// Desactívelo si la columna Id es AUTO_INCREMENT y no debe enviarse en el INSERT.
    /// </summary>
    public bool GenerateIdUsingMaxPlusOne { get; set; } = true;

    public int LastUserUpdate { get; set; } = 111;

    public int IdLastUserUpdate { get; set; } = 111;

    public string IdValidation { get; set; } = "21";

    public int IdDocumentType { get; set; } = 1;

    public int IdCurrentStatus { get; set; } = 0;

    /// <summary>
    /// FK a documentfile_error.Id. Use null o 0 en configuración para insertar SQL NULL (sin error), si la columna lo permite.
    /// Si la columna es NOT NULL, asigne aquí un Id válido existente en documentfile_error.
    /// </summary>
    public int? IdDocumentError { get; set; }

    public string ServerPath { get; set; } = "";

    public string IdDocumentContainer { get; set; } = "path por definir";

    /// <summary>Prefijo del campo Name, p. ej. Liquidacion_.</summary>
    public string NamePrefix { get; set; } = "Liquidacion_";
}
