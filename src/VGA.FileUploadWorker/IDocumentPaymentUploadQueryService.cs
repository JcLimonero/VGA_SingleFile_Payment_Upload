namespace VGA.FileUploadWorker;

public sealed record FailedUploadTrackingRow(
    long TrackingId,
    long? PaymentUploadId,
    string SourceRelativePath,
    string Channel,
    string OriginalFileName,
    bool ProcessSucceeded,
    string? ProcessedFullPath,
    string? FinalFileName,
    long DocumentByFileId,
    string CloudLastError);

public interface IDocumentPaymentUploadQueryService
{
    Task<IReadOnlyList<FailedUploadTrackingRow>> GetFailedUploadRowsAsync(
        string? channelFilter,
        string? errorContainsFilter,
        CancellationToken cancellationToken);

    Task MarkAttemptRecoveredAsync(IReadOnlyList<long> trackingIds, CancellationToken cancellationToken);
}
