namespace VGA.FileUploadWorker;

public sealed class ImportRollbackService
{
    private readonly IFileUploadRepository _repository;
    private readonly IDocumentByFileInserter _documentByFile;
    private readonly IDocumentPaymentUploadTracker _documentPaymentUpload;

    public ImportRollbackService(
        IFileUploadRepository repository,
        IDocumentByFileInserter documentByFile,
        IDocumentPaymentUploadTracker documentPaymentUpload)
    {
        _repository = repository;
        _documentByFile = documentByFile;
        _documentPaymentUpload = documentPaymentUpload;
    }

    /// <summary>
    /// Revierte SQLite y filas documentbyfile. No usar tras Backblaze OK (el archivo ya está en la nube).
    /// </summary>
    public async Task RollbackBeforeCloudAsync(
        long uploadId,
        IReadOnlyList<InsertedOrderImport> inserted,
        string reason,
        CancellationToken cancellationToken)
    {
        foreach (var row in inserted)
        {
            if (row.TrackingId.HasValue)
            {
                await _documentPaymentUpload.MarkProcessFailedAsync(
                    row.TrackingId.Value,
                    reason,
                    cancellationToken).ConfigureAwait(false);
            }

            await _documentByFile.TryDeleteByIdAsync(row.DocumentByFileId, cancellationToken).ConfigureAwait(false);
        }

        await _repository.DeleteUploadByIdAsync(uploadId, cancellationToken).ConfigureAwait(false);
    }
}

public readonly record struct InsertedOrderImport(
    OrderLookupSuccess Lookup,
    long? TrackingId,
    long DocumentByFileId);

public readonly record struct OrderLookupSuccess(string OrderNumber, long IdFile);
