namespace VGA.FileUploadWorker;

/// <summary>Resultado del INSERT en documentbyfile.</summary>
public readonly record struct DocumentByFileInsertResult(bool Ok, long? DocumentByFileId);
