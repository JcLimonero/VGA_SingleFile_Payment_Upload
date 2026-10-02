namespace VGA.FileUploadWorker;

public interface IBackblazeUploadClient
{
    /// <summary>POST multipart: file, idSingleFile, idDocumentFile.</summary>
    /// <returns>Success y mensaje de error o cuerpo truncado si falla.</returns>
    Task<(bool Success, string? ErrorDetail)> UploadAsync(
        string filePath,
        long idSingleFile,
        long idDocumentFile,
        CancellationToken cancellationToken);
}
