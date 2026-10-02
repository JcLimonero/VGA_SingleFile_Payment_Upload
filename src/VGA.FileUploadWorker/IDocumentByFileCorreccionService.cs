namespace VGA.FileUploadWorker;

public interface IDocumentByFileCorreccionService
{
    /// <summary>Desactiva filas en documentbyfile donde PathDocument coincide (Enabled → 0).</summary>
    Task<DocumentByFileCorreccionResult> TryDisableByPathDocumentAsync(
        string pathDocumentFileName,
        CancellationToken cancellationToken);

    /// <summary>Desactiva una fila en documentbyfile por PK (Enabled → 0).</summary>
    Task<DocumentByFileCorreccionResult> TryDisableByIdAsync(
        long documentByFileId,
        CancellationToken cancellationToken);
}
