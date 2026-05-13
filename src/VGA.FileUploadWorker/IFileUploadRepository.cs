namespace VGA.FileUploadWorker;

public interface IFileUploadRepository
{
    Task<long> InsertUploadAsync(
        string originalFileName,
        string sourceRelativePath,
        string channel,
        string? agencyAbbreviation,
        string? orderNumber,
        long fileSizeBytes,
        byte[]? fileContent,
        byte[]? contentSha256,
        CancellationToken cancellationToken);

    /// <summary>Elimina un registro recién insertado si falla un paso posterior (p. ej. MySQL).</summary>
    Task DeleteUploadByIdAsync(long id, CancellationToken cancellationToken);
}
