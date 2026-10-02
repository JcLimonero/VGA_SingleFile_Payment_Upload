namespace VGA.FileUploadWorker;

public interface IDocumentByFileInserter
{
    /// <summary>Inserta en documentbyfile.</summary>
    /// <param name="originalFileName">Nombre al recibir (p. ej. Acu_5672_23.pdf); se usa para el campo Name con prefijo.</param>
    /// <param name="pathDocumentFileName">Valor de PathDocument (p. ej. nombre renombrado en PROCESADOS).</param>
    Task<DocumentByFileInsertResult> TryInsertAsync(
        string originalFileName,
        string pathDocumentFileName,
        long idFile,
        CancellationToken cancellationToken);

    /// <summary>Elimina una fila insertada si falla un paso posterior (p. ej. Backblaze).</summary>
    Task TryDeleteByIdAsync(long documentByFileId, CancellationToken cancellationToken);
}
