namespace VGA.FileUploadWorker;

public static class ObtainOutcomes
{
    public const string Imported = "Imported";
    public const string Failed = "Failed";
    public const string RejectedInvalidName = "RejectedInvalidName";
    public const string ReadRetriesExhausted = "ReadRetriesExhausted";
    /// <summary>No hay agencia/pedido en el nombre; no se consulta la vista.</summary>
    public const string SkippedUnparsedName = "SkippedUnparsedName";
    /// <summary>La vista no devolvió fila para agencia/pedido; archivo permanece en el canal.</summary>
    public const string PendingNoRelatedRow = "PendingNoRelatedRow";
    /// <summary>Falta configuración de MySQL para la vista.</summary>
    public const string BlockedMissingMysqlConfig = "BlockedMissingMysqlConfig";
    /// <summary>Error al consultar MySQL; archivo no movido.</summary>
    public const string BlockedMysqlError = "BlockedMysqlError";
    /// <summary>Error al insertar en documentbyfile tras encontrar fila en la vista.</summary>
    public const string BlockedDocumentByFileInsert = "BlockedDocumentByFileInsert";
}

public sealed record FileObtainedLogEntry(
    string OriginalFileName,
    string SourceRelativePath,
    string Channel,
    string? AgencyAbbreviation,
    string? OrderNumber,
    string Outcome,
    string? Detail,
    long? PaymentUploadId);

public interface IFileObtainLogWriter
{
    Task WriteAsync(FileObtainedLogEntry entry, CancellationToken cancellationToken);
}
