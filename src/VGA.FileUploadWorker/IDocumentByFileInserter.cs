namespace VGA.FileUploadWorker;

public interface IDocumentByFileInserter
{
    /// <summary>Inserta en documentbyfile. Devuelve false si no se insertó (error de MySQL).</summary>
    Task<bool> TryInsertAsync(string originalFileName, long idFile, CancellationToken cancellationToken);
}
