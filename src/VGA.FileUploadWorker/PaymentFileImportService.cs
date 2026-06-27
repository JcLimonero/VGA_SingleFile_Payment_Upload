using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace VGA.FileUploadWorker;

public sealed class PaymentFileImportService
{
    private readonly IPendingImportRetryStore _pendingRetry;
    private readonly IFileUploadRepository _repository;
    private readonly IFileObtainLogWriter _obtainLog;
    private readonly IDocumentRelationViewGate _documentGate;
    private readonly IDocumentByFileInserter _documentByFile;
    private readonly IBackblazeUploadClient _backblazeUpload;
    private readonly IOptionsMonitor<BackblazeUploadOptions> _backblazeOptions;
    private readonly IDocumentPaymentUploadTracker _documentPaymentUpload;
    private readonly ImportRollbackService _rollback;
    private readonly UploadOptions _options;
    private readonly ILogger<PaymentFileImportService> _logger;

    public PaymentFileImportService(
        IPendingImportRetryStore pendingRetry,
        IFileUploadRepository repository,
        IFileObtainLogWriter obtainLog,
        IDocumentRelationViewGate documentGate,
        IDocumentByFileInserter documentByFile,
        IBackblazeUploadClient backblazeUpload,
        IOptionsMonitor<BackblazeUploadOptions> backblazeOptions,
        IDocumentPaymentUploadTracker documentPaymentUpload,
        ImportRollbackService rollback,
        IOptions<UploadOptions> options,
        ILogger<PaymentFileImportService> logger)
    {
        _pendingRetry = pendingRetry;
        _repository = repository;
        _obtainLog = obtainLog;
        _documentGate = documentGate;
        _documentByFile = documentByFile;
        _backblazeUpload = backblazeUpload;
        _backblazeOptions = backblazeOptions;
        _documentPaymentUpload = documentPaymentUpload;
        _rollback = rollback;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessFileAsync(
        string fullPath,
        string sourceRelativePath,
        string channel,
        string successDir,
        string failureDir,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(fullPath);

        if (await TryResumePendingMoveAsync(fullPath, sourceRelativePath, channel, successDir, name, cancellationToken)
                .ConfigureAwait(false))
            return;

        var lastOutcome = await _pendingRetry.GetLastOutcomeAsync(sourceRelativePath, cancellationToken).ConfigureAwait(false);
        if (string.Equals(lastOutcome, ObtainOutcomes.PendingMove, StringComparison.Ordinal))
        {
            if (!await _pendingRetry.ShouldDeferAsync(sourceRelativePath, cancellationToken).ConfigureAwait(false))
            {
                await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.PendingMove, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (await _pendingRetry.ShouldDeferAsync(sourceRelativePath, cancellationToken).ConfigureAwait(false))
            return;

        if (!PaymentFileNameParser.TryParse(name, out var fileInfo) || fileInfo is null)
        {
            await HandleUnparsedNameAsync(fullPath, sourceRelativePath, channel, name, failureDir, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var agency = fileInfo.Value.AgencyAbbreviation;
        var allOrders = fileInfo.Value.OrderNumbers;
        var ordersJoined = string.Join(",", allOrders);

        var lookupResult = await ResolveLookupsAsync(
            name,
            sourceRelativePath,
            channel,
            agency,
            allOrders,
            ordersJoined,
            cancellationToken).ConfigureAwait(false);
        if (!lookupResult.Ok)
            return;

        var readResult = await TryReadFileWithRetriesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (!readResult.Ok)
        {
            await LogAndScheduleRetryAsync(
                name,
                sourceRelativePath,
                channel,
                agency,
                ordersJoined,
                ObtainOutcomes.ReadRetriesExhausted,
                "No se pudo abrir/leer el archivo tras reintentos.",
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        long uploadId;
        List<InsertedOrderImport> insertedOrders;
        string destPath;
        string destFileName;

        try
        {
            var size = new FileInfo(fullPath).Length;
            uploadId = await _repository.InsertUploadAsync(
                name,
                sourceRelativePath,
                channel,
                agency,
                ordersJoined,
                size,
                _options.StoreFileContent ? readResult.Bytes : null,
                readResult.Hash,
                cancellationToken).ConfigureAwait(false);

            (destPath, destFileName) = ProcessedFileNaming.ResolveProcessedDestination(fullPath, successDir, uploadId);
            var inserted = await InsertDocumentByFileRowsAsync(
                name,
                sourceRelativePath,
                channel,
                agency,
                lookupResult.SuccessfulLookups,
                uploadId,
                destFileName,
                cancellationToken).ConfigureAwait(false);
            if (inserted is null)
                return;
            insertedOrders = inserted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fallo al preparar importación para {File}; archivo permanece en el canal.", sourceRelativePath);
            await LogAndScheduleRetryAsync(
                name,
                sourceRelativePath,
                channel,
                agency,
                ordersJoined,
                ObtainOutcomes.Failed,
                ex.Message,
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await UploadBackblazeForAllOrdersAsync(
                fullPath,
                name,
                sourceRelativePath,
                channel,
                agency,
                uploadId,
                insertedOrders,
                cancellationToken).ConfigureAwait(false))
            return;

        foreach (var row in insertedOrders)
        {
            if (row.TrackingId.HasValue)
            {
                await _documentPaymentUpload.MarkAwaitingMoveAsync(row.TrackingId.Value, destFileName, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        try
        {
            File.Move(fullPath, destPath, overwrite: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Backblaze OK pero no se pudo mover {File} a PROCESADOS; se reintentará solo el move.",
                sourceRelativePath);
            await LogAndScheduleRetryAsync(
                name,
                sourceRelativePath,
                channel,
                agency,
                ordersJoined,
                ObtainOutcomes.PendingMove,
                ex.Message,
                uploadId,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await FinalizeSuccessfulImportAsync(
            name,
            sourceRelativePath,
            channel,
            agency,
            uploadId,
            destPath,
            destFileName,
            insertedOrders,
            cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct LookupResult(bool Ok, List<OrderLookupSuccess> SuccessfulLookups);

    private readonly record struct FileReadResult(bool Ok, byte[]? Bytes, byte[]? Hash);

    private async Task<bool> TryResumePendingMoveAsync(
        string fullPath,
        string sourceRelativePath,
        string channel,
        string successDir,
        string name,
        CancellationToken cancellationToken)
    {
        var lastOutcome = await _pendingRetry.GetLastOutcomeAsync(sourceRelativePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(lastOutcome, ObtainOutcomes.PendingMove, StringComparison.Ordinal))
            return false;

        var awaiting = await _documentPaymentUpload.GetAwaitingMoveRowsAsync(sourceRelativePath, cancellationToken)
            .ConfigureAwait(false);
        if (awaiting.Count == 0)
            return false;

        var destFileName = awaiting[0].FinalFileName;
        var destPath = Path.Combine(successDir, destFileName);
        if (!File.Exists(fullPath))
        {
            if (File.Exists(destPath))
            {
                await FinalizeSuccessfulImportAsync(
                    name,
                    sourceRelativePath,
                    channel,
                    awaiting[0].AgencyAbbreviation,
                    awaiting[0].PaymentUploadId,
                    destPath,
                    destFileName,
                    awaiting.Select(r => new InsertedOrderImport(
                        new OrderLookupSuccess(r.OrderNumber, 0),
                        r.TrackingId,
                        0)).ToList(),
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        try
        {
            File.Move(fullPath, destPath, overwrite: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reintento de move pendiente falló para {File}", sourceRelativePath);
            await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.PendingMove, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        await FinalizeSuccessfulImportAsync(
            name,
            sourceRelativePath,
            channel,
            awaiting[0].AgencyAbbreviation,
            awaiting[0].PaymentUploadId,
            destPath,
            destFileName,
            awaiting.Select(r => new InsertedOrderImport(
                new OrderLookupSuccess(r.OrderNumber, 0),
                r.TrackingId,
                0)).ToList(),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task HandleUnparsedNameAsync(
        string fullPath,
        string sourceRelativePath,
        string channel,
        string name,
        string failureDir,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Nombre sin patrón válido: {File}. Se espera p. ej. Acu_7399_23.pdf o Acu_(1,2,3)_data.pdf",
            sourceRelativePath);
        if (_options.RequireValidPaymentFileName)
        {
            try
            {
                ProcessedFileNaming.MoveWithUniqueName(fullPath, failureDir);
            }
            catch (Exception moveEx)
            {
                _logger.LogError(moveEx, "No se pudo mover archivo con nombre inválido: {File}", name);
            }

            await _obtainLog.WriteAsync(
                new FileObtainedLogEntry(
                    name,
                    sourceRelativePath,
                    channel,
                    null,
                    null,
                    ObtainOutcomes.RejectedInvalidName,
                    "Nombre sin patrón Agencia_pedido_ o Agencia_(ped1,ped2,...)_",
                    null),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await LogAndScheduleRetryAsync(
            name,
            sourceRelativePath,
            channel,
            null,
            null,
            ObtainOutcomes.SkippedUnparsedName,
            "Sin agencia y pedido en el nombre no se puede consultar view_upload_documents_relation.",
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LookupResult> ResolveLookupsAsync(
        string name,
        string sourceRelativePath,
        string channel,
        string agency,
        IReadOnlyList<string> allOrders,
        string ordersJoined,
        CancellationToken cancellationToken)
    {
        var successfulLookups = new List<OrderLookupSuccess>();
        var missingOrders = new List<string>();

        foreach (var order in allOrders)
        {
            var lookup = await _documentGate.LookupRelatedRowAsync(agency, order, cancellationToken).ConfigureAwait(false);
            switch (lookup.Verdict)
            {
                case DocumentRelationGateVerdict.MissingConfiguration:
                    await LogAndScheduleRetryAsync(
                        name,
                        sourceRelativePath,
                        channel,
                        agency,
                        ordersJoined,
                        ObtainOutcomes.BlockedMissingMysqlConfig,
                        "Configure ConnectionStrings:DocumentRelationMysql o DocumentRelationMysql:Password.",
                        null,
                        cancellationToken).ConfigureAwait(false);
                    return new LookupResult(false, successfulLookups);
                case DocumentRelationGateVerdict.QueryError:
                    await LogAndScheduleRetryAsync(
                        name,
                        sourceRelativePath,
                        channel,
                        agency,
                        ordersJoined,
                        ObtainOutcomes.BlockedMysqlError,
                        "Error al consultar la vista en MySQL.",
                        null,
                        cancellationToken).ConfigureAwait(false);
                    return new LookupResult(false, successfulLookups);
                case DocumentRelationGateVerdict.NoRelatedRow:
                    missingOrders.Add(order);
                    break;
                case DocumentRelationGateVerdict.RelatedRowFound:
                    if (!lookup.IdFile.HasValue)
                    {
                        await LogAndScheduleRetryAsync(
                            name,
                            sourceRelativePath,
                            channel,
                            agency,
                            ordersJoined,
                            ObtainOutcomes.BlockedMysqlError,
                            "La vista no devolvió IdFile.",
                            null,
                            cancellationToken).ConfigureAwait(false);
                        return new LookupResult(false, successfulLookups);
                    }

                    successfulLookups.Add(new OrderLookupSuccess(order, lookup.IdFile.Value));
                    break;
                default:
                    throw new InvalidOperationException($"Verdict no contemplado: {lookup.Verdict}");
            }
        }

        if (missingOrders.Count > 0 || successfulLookups.Count == 0)
        {
            var ordersToLog = missingOrders.Count > 0 ? missingOrders : allOrders;
            foreach (var order in ordersToLog)
            {
                _logger.LogInformation(
                    "Sin fila en vista para abbreviation={Agency} order_dms={Order}; archivo sin mover: {File}",
                    agency,
                    order,
                    sourceRelativePath);
                await _obtainLog.WriteAsync(
                    new FileObtainedLogEntry(
                        name,
                        sourceRelativePath,
                        channel,
                        agency,
                        order,
                        ObtainOutcomes.PendingNoRelatedRow,
                        missingOrders.Count > 0
                            ? "Faltan pedidos en view_upload_documents_relation; archivo no movido."
                            : "No existe registro en view_upload_documents_relation para esta agencia y pedido.",
                        null),
                    cancellationToken).ConfigureAwait(false);
            }

            await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.PendingNoRelatedRow, cancellationToken)
                .ConfigureAwait(false);
            return new LookupResult(false, successfulLookups);
        }

        return new LookupResult(true, successfulLookups);
    }

    private async Task<FileReadResult> TryReadFileWithRetriesAsync(string fullPath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await using var fs = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 65_536,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (_options.StoreFileContent)
                {
                    var bytes = await ReadAllAsync(fs, cancellationToken).ConfigureAwait(false);
                    return new FileReadResult(true, bytes, SHA256.HashData(bytes));
                }

                var hash = await ComputeSha256Async(fs, cancellationToken).ConfigureAwait(false);
                return new FileReadResult(true, null, hash);
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(250 * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                await Task.Delay(250 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogWarning("No se pudo leer el archivo tras varios intentos: {File}", fullPath);
        return new FileReadResult(false, null, null);
    }

    private async Task<List<InsertedOrderImport>?> InsertDocumentByFileRowsAsync(
        string name,
        string sourceRelativePath,
        string channel,
        string agency,
        IReadOnlyList<OrderLookupSuccess> successfulLookups,
        long uploadId,
        string destFileName,
        CancellationToken cancellationToken)
    {
        var insertedOrders = new List<InsertedOrderImport>();
        foreach (var lookup in successfulLookups)
        {
            var trackingId = await _documentPaymentUpload.InsertRowAfterSqliteAsync(
                sourceRelativePath,
                channel,
                name,
                agency,
                lookup.OrderNumber,
                lookup.IdFile,
                uploadId,
                cancellationToken).ConfigureAwait(false);

            var insertResult = await _documentByFile.TryInsertAsync(
                name,
                destFileName,
                lookup.IdFile,
                cancellationToken).ConfigureAwait(false);

            if (!insertResult.Ok || !insertResult.DocumentByFileId.HasValue)
            {
                var detail = !insertResult.Ok
                    ? "No se pudo insertar en documentbyfile (MySQL)."
                    : "Insert en documentbyfile sin Id devuelto.";
                if (trackingId.HasValue)
                {
                    await _documentPaymentUpload.MarkProcessFailedAsync(trackingId.Value, detail, cancellationToken)
                        .ConfigureAwait(false);
                }

                await _rollback.RollbackBeforeCloudAsync(uploadId, insertedOrders, detail, cancellationToken)
                    .ConfigureAwait(false);
                await _obtainLog.WriteAsync(
                    new FileObtainedLogEntry(
                        name,
                        sourceRelativePath,
                        channel,
                        agency,
                        lookup.OrderNumber,
                        ObtainOutcomes.BlockedDocumentByFileInsert,
                        detail,
                        null),
                    cancellationToken).ConfigureAwait(false);
                await _pendingRetry.ScheduleRetryAsync(
                    sourceRelativePath,
                    ObtainOutcomes.BlockedDocumentByFileInsert,
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            insertedOrders.Add(new InsertedOrderImport(lookup, trackingId, insertResult.DocumentByFileId.Value));
            if (trackingId.HasValue)
            {
                await _documentPaymentUpload.MarkDocumentByFileAsync(
                    trackingId.Value,
                    insertResult.DocumentByFileId.Value,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return insertedOrders;
    }

    private async Task<bool> UploadBackblazeForAllOrdersAsync(
        string fullPath,
        string name,
        string sourceRelativePath,
        string channel,
        string agency,
        long uploadId,
        List<InsertedOrderImport> insertedOrders,
        CancellationToken cancellationToken)
    {
        if (!_backblazeOptions.CurrentValue.Enabled)
        {
            _logger.LogInformation(
                "BackblazeUpload.Enabled=false: no se llama al API; archivo solo en disco tras mover: {File}",
                name);
            return true;
        }

        foreach (var row in insertedOrders)
        {
            var lookup = row.Lookup;
            _logger.LogInformation(
                "Enviando archivo por API Backblaze: ruta={Path}, idSingleFile={IdFile}, idDocumentFile={IdDoc}, pedido={Order}",
                fullPath,
                lookup.IdFile,
                row.DocumentByFileId,
                lookup.OrderNumber);
            if (row.TrackingId.HasValue)
            {
                await _documentPaymentUpload.NotifyCloudAttemptStartingAsync(row.TrackingId.Value, cancellationToken)
                    .ConfigureAwait(false);
            }

            var (uploadOk, uploadErr) = await _backblazeUpload
                .UploadAsync(fullPath, lookup.IdFile, row.DocumentByFileId, cancellationToken)
                .ConfigureAwait(false);

            if (row.TrackingId.HasValue)
            {
                await _documentPaymentUpload.MarkCloudOutcomeAsync(
                    row.TrackingId.Value,
                    uploadOk,
                    uploadErr,
                    cancellationToken).ConfigureAwait(false);
            }

            if (uploadOk)
                continue;

            _logger.LogError(
                "Fallo subida Backblaze para {File}, pedido={Order}: {Detail}; archivo no movido",
                name,
                lookup.OrderNumber,
                uploadErr);
            await _rollback.RollbackBeforeCloudAsync(uploadId, insertedOrders, uploadErr ?? "Fallo Backblaze", cancellationToken)
                .ConfigureAwait(false);
            await _obtainLog.WriteAsync(
                new FileObtainedLogEntry(
                    name,
                    sourceRelativePath,
                    channel,
                    agency,
                    lookup.OrderNumber,
                    ObtainOutcomes.FailedBackblazeUpload,
                    uploadErr,
                    null),
                cancellationToken).ConfigureAwait(false);
            await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.FailedBackblazeUpload, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async Task FinalizeSuccessfulImportAsync(
        string name,
        string sourceRelativePath,
        string channel,
        string? agency,
        long uploadId,
        string destPath,
        string destFileName,
        IReadOnlyList<InsertedOrderImport> insertedOrders,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Archivo guardado en PROCESADOS como {StoredName}", Path.GetFileName(destPath));
        await _pendingRetry.ClearAsync(sourceRelativePath, cancellationToken).ConfigureAwait(false);

        foreach (var row in insertedOrders)
        {
            if (row.TrackingId.HasValue)
            {
                await _documentPaymentUpload.MarkProcessedAsync(
                    row.TrackingId.Value,
                    destPath,
                    destFileName,
                    cancellationToken).ConfigureAwait(false);
            }

            var importDetail = $"Guardado como: {Path.GetFileName(destPath)}; pedido={row.Lookup.OrderNumber}";
            if (_backblazeOptions.CurrentValue.Enabled)
                importDetail = $"{importDetail}; Backblaze: subida OK";

            await _obtainLog.WriteAsync(
                new FileObtainedLogEntry(
                    name,
                    sourceRelativePath,
                    channel,
                    agency,
                    row.Lookup.OrderNumber,
                    ObtainOutcomes.Imported,
                    importDetail,
                    uploadId),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LogAndScheduleRetryAsync(
        string name,
        string sourceRelativePath,
        string channel,
        string? agency,
        string? order,
        string outcome,
        string detail,
        long? uploadId,
        CancellationToken cancellationToken)
    {
        await _obtainLog.WriteAsync(
            new FileObtainedLogEntry(name, sourceRelativePath, channel, agency, order, outcome, detail, uploadId),
            cancellationToken).ConfigureAwait(false);
        await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, outcome, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static async Task<byte[]> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        return await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}
