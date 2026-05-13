namespace VGA.FileUploadWorker;

public interface IDocumentByFileInserter
{
    /// <summary>Inserta en documentbyfile. Devuelve false si no se insertó (error de MySQL).</summary>
    /// <param name="originalFileName">Nombre al recibir (p. ej. Acu_5672_23.pdf); se usa para el campo Name con prefijo.</param>
    /// <param name="pathDocumentFileName">Valor de PathDocument (p. ej. nombre renombrado en PROCESADOS).</param>
    Task<bool> TryInsertAsync(string originalFileName, string pathDocumentFileName, long idFile, CancellationToken cancellationToken);
}
