namespace VGA.FileUploadWorker;

public interface IDocumentPaymentUploadTracker
{
    Task<long?> InsertRowAfterSqliteAsync(
        string sourceRelativePath,
        string channel,
        string originalFileName,
        string? agencyAbbreviation,
        string? orderNumber,
        long idFile,
        long paymentUploadId,
        CancellationToken cancellationToken);

    Task MarkDocumentByFileAsync(long trackingId, long documentByFileId, CancellationToken cancellationToken);

    Task MarkProcessedAsync(long trackingId, string processedFullPath, string finalFileName, CancellationToken cancellationToken);

    Task MarkProcessFailedAsync(long trackingId, string? detail, CancellationToken cancellationToken);

    Task NotifyCloudAttemptStartingAsync(long trackingId, CancellationToken cancellationToken);

    Task MarkCloudOutcomeAsync(long trackingId, bool success, string? errorDetail, CancellationToken cancellationToken);
}
