namespace VGA.FileUploadWorker;

public enum RecoveryMoveAction
{
    Move,
    DisableOnly,
    Skip,
}

public sealed record RecoveryOptions
{
    public string? ChannelFilter { get; init; }
    public string? ErrorContainsFilter { get; init; }
}

public sealed class RecoveryPlanItem
{
    public required string GroupKey { get; init; }
    public long? PaymentUploadId { get; init; }
    public required string Channel { get; init; }
    public required string OriginalFileName { get; init; }
    public required string SourceRelativePath { get; init; }
    public string? ProcessedFullPath { get; init; }
    public required string DestinationPath { get; init; }
    public required IReadOnlyList<long> DocumentByFileIds { get; init; }
    public required IReadOnlyList<long> TrackingIds { get; init; }
    public required RecoveryMoveAction Action { get; init; }
    public required string ActionReason { get; init; }
    public required string CloudLastError { get; init; }
    public bool SourceFileExists { get; init; }
    public bool DestinationExists { get; init; }
}

public sealed class RecoveryPlan
{
    public required string RootPath { get; init; }
    public required IReadOnlyList<RecoveryPlanItem> Items { get; init; }
}

public sealed class RecoveryItemResult
{
    public required RecoveryPlanItem Item { get; init; }
    public bool Succeeded { get; init; }
    public string? ErrorDetail { get; init; }
}

public sealed class RecoveryResult
{
    public required IReadOnlyList<RecoveryItemResult> ItemResults { get; init; }

    public int SuccessCount => ItemResults.Count(r => r.Succeeded);
    public int FailureCount => ItemResults.Count(r => !r.Succeeded);
}

public static class FailedUploadRecoveryPaths
{
    public static string ResolveOriginalPath(string rootPath, string sourceRelativePath)
    {
        var root = NormalizeRoot(rootPath);
        var rel = sourceRelativePath.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, rel));
    }

    public static string BuildGroupKey(FailedUploadTrackingRow row)
    {
        if (row.PaymentUploadId.HasValue)
            return $"upload:{row.PaymentUploadId.Value}";

        var processed = row.ProcessedFullPath ?? "";
        return $"path:{row.SourceRelativePath}|{processed}";
    }

    public static RecoveryPlanItem BuildPlanItem(
        string groupKey,
        IReadOnlyList<FailedUploadTrackingRow> rows,
        string rootPath)
    {
        var first = rows[0];
        var destinationPath = ResolveOriginalPath(rootPath, first.SourceRelativePath);
        var processedPath = first.ProcessedFullPath;
        var sourceExists = !string.IsNullOrWhiteSpace(processedPath) && File.Exists(processedPath);
        var destinationExists = File.Exists(destinationPath);

        var action = ResolveAction(first.ProcessSucceeded, processedPath, sourceExists, destinationExists, out var reason);

        return new RecoveryPlanItem
        {
            GroupKey = groupKey,
            PaymentUploadId = first.PaymentUploadId,
            Channel = first.Channel,
            OriginalFileName = first.OriginalFileName,
            SourceRelativePath = first.SourceRelativePath,
            ProcessedFullPath = processedPath,
            DestinationPath = destinationPath,
            DocumentByFileIds = rows.Select(r => r.DocumentByFileId).Distinct().ToList(),
            TrackingIds = rows.Select(r => r.TrackingId).Distinct().ToList(),
            Action = action,
            ActionReason = reason,
            CloudLastError = TruncateError(first.CloudLastError, 200),
            SourceFileExists = sourceExists,
            DestinationExists = destinationExists,
        };
    }

    public static RecoveryMoveAction ResolveAction(
        bool processSucceeded,
        string? processedFullPath,
        bool sourceFileExists,
        bool destinationExists,
        out string reason)
    {
        if (processSucceeded && !string.IsNullOrWhiteSpace(processedFullPath))
        {
            if (!sourceFileExists)
            {
                reason = "Archivo no encontrado en PROCESADOS; solo deshabilitar documentbyfile.";
                return RecoveryMoveAction.DisableOnly;
            }

            if (destinationExists)
            {
                reason = "Destino ya existe en carpeta original; omitir move, deshabilitar documentbyfile.";
                return RecoveryMoveAction.DisableOnly;
            }

            reason = "Mover de PROCESADOS a carpeta original.";
            return RecoveryMoveAction.Move;
        }

        reason = "Archivo en canal o sin move a PROCESADOS; solo deshabilitar documentbyfile.";
        return RecoveryMoveAction.DisableOnly;
    }

    private static string TruncateError(string error, int maxLen) =>
        error.Length <= maxLen ? error : error[..maxLen] + "…";

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "";
        var t = root.Trim();
        if (t.Length >= 2 && t[^1] == Path.VolumeSeparatorChar)
            return t + Path.DirectorySeparatorChar;
        return t;
    }
}

public sealed class FailedUploadRecoveryService
{
    private readonly IDocumentPaymentUploadQueryService _query;
    private readonly IDocumentByFileCorreccionService _documentCorreccion;
    private readonly IPendingImportRetryStore _pendingRetry;
    private readonly UploadOptions _uploadOptions;
    private readonly ILogger<FailedUploadRecoveryService> _logger;

    public FailedUploadRecoveryService(
        IDocumentPaymentUploadQueryService query,
        IDocumentByFileCorreccionService documentCorreccion,
        IPendingImportRetryStore pendingRetry,
        Microsoft.Extensions.Options.IOptions<UploadOptions> uploadOptions,
        ILogger<FailedUploadRecoveryService> logger)
    {
        _query = query;
        _documentCorreccion = documentCorreccion;
        _pendingRetry = pendingRetry;
        _uploadOptions = uploadOptions.Value;
        _logger = logger;
    }

    public async Task<RecoveryPlan> BuildPlanAsync(RecoveryOptions options, CancellationToken cancellationToken)
    {
        var rows = await _query.GetFailedUploadRowsAsync(
            options.ChannelFilter,
            options.ErrorContainsFilter,
            cancellationToken).ConfigureAwait(false);

        var grouped = rows
            .GroupBy(FailedUploadRecoveryPaths.BuildGroupKey, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var items = new List<RecoveryPlanItem>();
        foreach (var group in grouped)
        {
            var list = group.ToList();
            items.Add(FailedUploadRecoveryPaths.BuildPlanItem(group.Key, list, _uploadOptions.RootPath));
        }

        return new RecoveryPlan
        {
            RootPath = _uploadOptions.RootPath,
            Items = items,
        };
    }

    public async Task<RecoveryResult> ExecuteAsync(RecoveryPlan plan, CancellationToken cancellationToken)
    {
        var results = new List<RecoveryItemResult>();

        foreach (var item in plan.Items)
        {
            try
            {
                foreach (var docId in item.DocumentByFileIds)
                {
                    var disable = await _documentCorreccion.TryDisableByIdAsync(docId, cancellationToken)
                        .ConfigureAwait(false);
                    if (disable.Status == DocumentByFileCorreccionStatus.Failed)
                    {
                        throw new InvalidOperationException(
                            $"No se pudo deshabilitar documentbyfile Id={docId}: {disable.ErrorDetail}");
                    }
                }

                if (item.Action == RecoveryMoveAction.Move)
                {
                    if (string.IsNullOrWhiteSpace(item.ProcessedFullPath))
                        throw new InvalidOperationException("ProcessedFullPath vacío.");

                    Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
                    File.Move(item.ProcessedFullPath, item.DestinationPath, overwrite: false);
                    _logger.LogInformation(
                        "Recuperación: movido {From} -> {To}",
                        item.ProcessedFullPath,
                        item.DestinationPath);
                }

                await _pendingRetry.ClearAsync(item.SourceRelativePath, cancellationToken).ConfigureAwait(false);
                await _query.MarkAttemptRecoveredAsync(item.TrackingIds, cancellationToken).ConfigureAwait(false);

                results.Add(new RecoveryItemResult { Item = item, Succeeded = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recuperación falló para grupo {GroupKey}", item.GroupKey);
                results.Add(new RecoveryItemResult
                {
                    Item = item,
                    Succeeded = false,
                    ErrorDetail = ex.Message,
                });
            }
        }

        return new RecoveryResult { ItemResults = results };
    }
}
