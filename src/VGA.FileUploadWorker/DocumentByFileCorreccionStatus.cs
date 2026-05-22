namespace VGA.FileUploadWorker;

public enum DocumentByFileCorreccionStatus
{
    Disabled,
    AlreadyDisabled,
    NotFound,
    Failed,
}

public readonly record struct DocumentByFileCorreccionResult(
    DocumentByFileCorreccionStatus Status,
    int RowsAffected,
    string? ErrorDetail);
