namespace VGA.FileUploadWorker;

public interface IDocumentByFileCorreccionService
{
    /// <summary>Desactiva filas en documentbyfile donde PathDocument coincide (Enabled → 0).</summary>
    Task<DocumentByFileCorreccionResult> TryDisableByPathDocumentAsync(
        string pathDocumentFileName,
        CancellationToken cancellationToken);
}
