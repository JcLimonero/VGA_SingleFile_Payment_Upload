namespace VGA.FileUploadWorker;

public static class ObtainOutcomes
{
    public const string Imported = "Imported";
    public const string Failed = "Failed";
    public const string RejectedInvalidName = "RejectedInvalidName";
    public const string ReadRetriesExhausted = "ReadRetriesExhausted";
}

public sealed record FileObtainedLogEntry(
    string OriginalFileName,
    string SourceRelativePath,
    string Channel,
    string? AgencyAbbreviation,
    string? OrderNumber,
    string Outcome,
    string? Detail,
    long? PaymentUploadId);

public interface IFileObtainLogWriter
{
    Task WriteAsync(FileObtainedLogEntry entry, CancellationToken cancellationToken);
}
