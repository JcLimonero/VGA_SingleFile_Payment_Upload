namespace VGA.FileUploadWorker;

/// <summary>Resuelve la cadena MySQL usada por vista documentbyfile, documentbyfile y DocumentPaymentUpload.</summary>
public interface IDocumentRelationMysqlConnectionProvider
{
    string? GetConnectionString();
}
