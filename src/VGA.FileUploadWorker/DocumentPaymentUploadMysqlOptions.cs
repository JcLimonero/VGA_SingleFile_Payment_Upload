namespace VGA.FileUploadWorker;

public sealed class DocumentPaymentUploadMysqlOptions
{
    public const string SectionName = "DocumentPaymentUpload";

    /// <summary>Si es false, no se escribe en MySQL (el pipeline sigue igual).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Nombre de tabla (solo identificador seguro).</summary>
    public string TableName { get; set; } = "DocumentPaymentUpload";
}
