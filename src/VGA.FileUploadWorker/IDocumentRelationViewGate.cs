namespace VGA.FileUploadWorker;

public enum DocumentRelationGateVerdict
{
    RelatedRowFound,
    NoRelatedRow,
    MissingConfiguration,
    QueryError,
}

public sealed record DocumentRelationLookupResult(DocumentRelationGateVerdict Verdict, long? IdFile);

public interface IDocumentRelationViewGate
{
    Task<DocumentRelationLookupResult> LookupRelatedRowAsync(
        string abbreviation,
        string orderDms,
        CancellationToken cancellationToken);
}
