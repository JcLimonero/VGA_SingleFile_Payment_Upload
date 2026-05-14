using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace VGA.FileUploadWorker;

public sealed class FolderUploadBackgroundService : BackgroundService
{
    private readonly ILogger<FolderUploadBackgroundService> _logger;
    private readonly IFileUploadRepository _repository;
    private readonly IFileObtainLogWriter _obtainLog;
    private readonly ISqliteConnectionProvider _sqlite;
    private readonly IDocumentRelationViewGate _documentGate;
    private readonly IDocumentByFileInserter _documentByFile;
    private readonly IBackblazeUploadClient _backblazeUpload;
    private readonly IOptionsMonitor<BackblazeUploadOptions> _backblazeOptions;
    private readonly IPendingImportRetryStore _pendingRetry;
    private readonly UploadOptions _options;

    public FolderUploadBackgroundService(
        ILogger<FolderUploadBackgroundService> logger,
        IFileUploadRepository repository,
        IFileObtainLogWriter obtainLog,
        ISqliteConnectionProvider sqlite,
        IDocumentRelationViewGate documentGate,
        IDocumentByFileInserter documentByFile,
        IBackblazeUploadClient backblazeUpload,
        IOptionsMonitor<BackblazeUploadOptions> backblazeOptions,
        IPendingImportRetryStore pendingRetry,
        IOptions<UploadOptions> options)
    {
        _logger = logger;
        _repository = repository;
        _obtainLog = obtainLog;
        _sqlite = sqlite;
        _documentGate = documentGate;
        _documentByFile = documentByFile;
        _backblazeUpload = backblazeUpload;
        _backblazeOptions = backblazeOptions;
        _pendingRetry = pendingRetry;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SqliteSchema.EnsureCreatedAsync(_sqlite, stoppingToken).ConfigureAwait(false);

        var root = NormalizeRoot(_options.RootPath);
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.ScanIntervalSeconds, 1, 86_400));
        var channels = _options.ChannelNameSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(root))
        {
            _logger.LogError("Upload:RootPath no está configurado (por ejemplo Z:\\).");
            return;
        }

        if (channels.Count == 0)
        {
            _logger.LogError("Upload:ChannelFolderNames está vacío.");
            return;
        }

        _logger.LogInformation(
            "Servicio de importación iniciado. Raíz: {Root}, canales: {Channels}, intervalo: {Interval}s, BD: {Db}",
            root,
            string.Join(", ", channels),
            interval.TotalSeconds,
            SummarizeSqlitePath(_sqlite.ConnectionString));

        LogDealerTreeDocumentPath();

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try
                {
                    await ProcessPendingFilesAsync(root, channels, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error no controlado durante el escaneo de archivos.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Servicio de importación detenido.");
        }
    }

    private async Task ProcessPendingFilesAsync(string root, HashSet<string> channels, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            _logger.LogWarning("La raíz no existe o no está accesible: {Root}", root);
            return;
        }

        foreach (var channelDir in EnumerateChannelDirectories(root, channels))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channelName = Path.GetFileName(channelDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            EnsureProcessedAndCancelledFolders(channelDir);

            IEnumerable<string> filesInChannel;
            try
            {
                filesInChannel = Directory.EnumerateFiles(channelDir, _options.FileSearchPattern, SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Sin acceso para listar archivos en canal: {Path}", channelDir);
                continue;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "No se pudo listar archivos en canal: {Path}", channelDir);
                continue;
            }

            foreach (var fullPath in filesInChannel)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, fullPath);
                await TryProcessOneFileAsync(fullPath, relative, channelName, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Recorre el árbol desde la raíz sin usar AllDirectories (evita fallar en System Volume Information, etc.).
    /// </summary>
    private IEnumerable<string> EnumerateChannelDirectories(string root, HashSet<string> channels)
    {
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Sin acceso al listar subcarpetas, se omite: {Path}", current);
                continue;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "No se pudieron listar subcarpetas, se omite: {Path}", current);
                continue;
            }

            foreach (var child in children)
            {
                var trimmed = child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var leaf = Path.GetFileName(trimmed);
                if (leaf.Length == 0)
                    continue;
                if (IsSkippableSystemDirectory(leaf))
                    continue;

                if (channels.Contains(leaf))
                    yield return child;

                queue.Enqueue(child);
            }
        }
    }

    private static bool IsSkippableSystemDirectory(string directoryName) =>
        directoryName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
        || directoryName.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
        || directoryName.Equals("Recovery", StringComparison.OrdinalIgnoreCase);

    private async Task TryProcessOneFileAsync(
        string fullPath,
        string sourceRelativePath,
        string channel,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(fullPath);
        var channelDir = Path.GetDirectoryName(fullPath)
                         ?? throw new InvalidOperationException($"Ruta inválida: {fullPath}");
        var successDir = Path.Combine(channelDir, _options.SuccessSubfolder);
        var failureDir = Path.Combine(channelDir, _options.FailureSubfolder);

        if (await _pendingRetry.ShouldDeferAsync(sourceRelativePath, cancellationToken).ConfigureAwait(false))
            return;

        var parsed = PaymentFileNameParser.TryParse(name, out var agency, out var order);
        if (!parsed)
        {
            _logger.LogWarning(
                "Nombre sin patrón Agencia_pedido_: {File}. Se espera p. ej. Acu_7399_23.pdf",
                sourceRelativePath);
            if (_options.RequireValidPaymentFileName)
            {
                try
                {
                    MoveWithUniqueName(fullPath, failureDir);
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
                        "Nombre sin patrón Agencia_pedido_",
                        null),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await _obtainLog.WriteAsync(
                new FileObtainedLogEntry(
                    name,
                    sourceRelativePath,
                    channel,
                    null,
                    null,
                    ObtainOutcomes.SkippedUnparsedName,
                    "Sin agencia y pedido en el nombre no se puede consultar view_upload_documents_relation.",
                    null),
                cancellationToken).ConfigureAwait(false);
            await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.SkippedUnparsedName, cancellationToken).ConfigureAwait(false);
            return;
        }

        var lookup = await _documentGate.LookupRelatedRowAsync(agency!, order!, cancellationToken).ConfigureAwait(false);
        switch (lookup.Verdict)
        {
            case DocumentRelationGateVerdict.MissingConfiguration:
                await _obtainLog.WriteAsync(
                    new FileObtainedLogEntry(
                        name,
                        sourceRelativePath,
                        channel,
                        agency,
                        order,
                        ObtainOutcomes.BlockedMissingMysqlConfig,
                        "Configure ConnectionStrings:DocumentRelationMysql o DocumentRelationMysql:Password.",
                        null),
                    cancellationToken).ConfigureAwait(false);
                await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.BlockedMissingMysqlConfig, cancellationToken).ConfigureAwait(false);
                return;
            case DocumentRelationGateVerdict.QueryError:
                await _obtainLog.WriteAsync(
                    new FileObtainedLogEntry(
                        name,
                        sourceRelativePath,
                        channel,
                        agency,
                        order,
                        ObtainOutcomes.BlockedMysqlError,
                        "Error al consultar la vista en MySQL.",
                        null),
                    cancellationToken).ConfigureAwait(false);
                await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.BlockedMysqlError, cancellationToken).ConfigureAwait(false);
                return;
            case DocumentRelationGateVerdict.NoRelatedRow:
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
                        "No existe registro en view_upload_documents_relation para esta agencia y pedido.",
                        null),
                    cancellationToken).ConfigureAwait(false);
                await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.PendingNoRelatedRow, cancellationToken).ConfigureAwait(false);
                return;
            case DocumentRelationGateVerdict.RelatedRowFound:
                if (!lookup.IdFile.HasValue)
                {
                    await _obtainLog.WriteAsync(
                        new FileObtainedLogEntry(
                            name,
                            sourceRelativePath,
                            channel,
                            agency,
                            order,
                            ObtainOutcomes.BlockedMysqlError,
                            "La vista no devolvió IdFile.",
                            null),
                        cancellationToken).ConfigureAwait(false);
                    await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.BlockedMysqlError, cancellationToken).ConfigureAwait(false);
                    return;
                }

                break;
            default:
                throw new InvalidOperationException($"Verdict no contemplado: {lookup.Verdict}");
        }

        byte[]? bytes = null;
        byte[]? hash = null;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await using (var fs = new FileStream(
                                 fullPath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.ReadWrite,
                                 bufferSize: 65_536,
                                 options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (_options.StoreFileContent)
                    {
                        bytes = await ReadAllAsync(fs, cancellationToken).ConfigureAwait(false);
                        hash = SHA256.HashData(bytes);
                    }
                    else
                    {
                        hash = await ComputeSha256Async(fs, cancellationToken).ConfigureAwait(false);
                    }
                }

                var size = new FileInfo(fullPath).Length;
                var id = await _repository.InsertUploadAsync(
                    name,
                    sourceRelativePath,
                    channel,
                    agency,
                    order,
                    size,
                    _options.StoreFileContent ? bytes : null,
                    hash,
                    cancellationToken).ConfigureAwait(false);

                var (destPath, destFileName) = ResolveProcessedDestination(fullPath, successDir, id);

                var insertResult = await _documentByFile.TryInsertAsync(name, destFileName, lookup.IdFile!.Value, cancellationToken).ConfigureAwait(false);
                if (!insertResult.Ok)
                {
                    await _repository.DeleteUploadByIdAsync(id, cancellationToken).ConfigureAwait(false);
                    await _obtainLog.WriteAsync(
                        new FileObtainedLogEntry(
                            name,
                            sourceRelativePath,
                            channel,
                            agency,
                            order,
                            ObtainOutcomes.BlockedDocumentByFileInsert,
                            "No se pudo insertar en documentbyfile (MySQL).",
                            null),
                        cancellationToken).ConfigureAwait(false);
                    await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.BlockedDocumentByFileInsert, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!insertResult.DocumentByFileId.HasValue)
                {
                    await _repository.DeleteUploadByIdAsync(id, cancellationToken).ConfigureAwait(false);
                    _logger.LogError("documentbyfile insertó sin devolver Id; se revierte SQLite para {File}", name);
                    await _obtainLog.WriteAsync(
                        new FileObtainedLogEntry(
                            name,
                            sourceRelativePath,
                            channel,
                            agency,
                            order,
                            ObtainOutcomes.BlockedDocumentByFileInsert,
                            "Insert en documentbyfile sin Id devuelto.",
                            null),
                        cancellationToken).ConfigureAwait(false);
                    await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.BlockedDocumentByFileInsert, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var documentByFileId = insertResult.DocumentByFileId.Value;

                File.Move(fullPath, destPath, overwrite: false);
                var storedAs = destPath;
                _logger.LogInformation("Archivo guardado en PROCESADOS como {StoredName}", Path.GetFileName(storedAs));
                await _pendingRetry.ClearAsync(sourceRelativePath, cancellationToken).ConfigureAwait(false);

                var importDetail = $"Guardado como: {Path.GetFileName(storedAs)}";
                var outcome = ObtainOutcomes.Imported;
                if (_backblazeOptions.CurrentValue.Enabled)
                {
                    var (uploadOk, uploadErr) = await _backblazeUpload
                        .UploadAsync(destPath, lookup.IdFile!.Value, documentByFileId, cancellationToken)
                        .ConfigureAwait(false);
                    if (!uploadOk)
                    {
                        _logger.LogError("Fallo subida Backblaze para {File}: {Detail}", Path.GetFileName(storedAs), uploadErr);
                        outcome = ObtainOutcomes.FailedBackblazeUpload;
                        importDetail = $"{importDetail}; Backblaze: {uploadErr}";
                    }
                }

                await _obtainLog.WriteAsync(
                    new FileObtainedLogEntry(
                        name,
                        sourceRelativePath,
                        channel,
                        agency,
                        order,
                        outcome,
                        importDetail,
                        id),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(250 * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                await Task.Delay(250 * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo al procesar {File}; se moverá a {Folder}.", sourceRelativePath, _options.FailureSubfolder);
                try
                {
                    MoveWithUniqueName(fullPath, failureDir);
                }
                catch (Exception moveEx)
                {
                    _logger.LogError(moveEx, "No se pudo mover el archivo en error: {File}", name);
                }

                await _obtainLog.WriteAsync(
                    new FileObtainedLogEntry(
                        name,
                        sourceRelativePath,
                        channel,
                        agency,
                        order,
                        ObtainOutcomes.Failed,
                        ex.Message,
                        null),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        _logger.LogWarning("No se pudo leer el archivo tras varios intentos: {File}", sourceRelativePath);
        await _obtainLog.WriteAsync(
            new FileObtainedLogEntry(
                name,
                sourceRelativePath,
                channel,
                agency,
                order,
                ObtainOutcomes.ReadRetriesExhausted,
                "No se pudo abrir/leer el archivo tras reintentos.",
                null),
            cancellationToken).ConfigureAwait(false);
        await _pendingRetry.ScheduleRetryAsync(sourceRelativePath, ObtainOutcomes.ReadRetriesExhausted, cancellationToken).ConfigureAwait(false);
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
        var h = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return h;
    }

    /// <summary>
    /// Bajo cada EFECTIVO/TPV deben existir las carpetas de éxito y fallo; si faltan, se crean.
    /// </summary>
    private void EnsureProcessedAndCancelledFolders(string channelDir)
    {
        var processed = Path.Combine(channelDir, _options.SuccessSubfolder);
        var cancelled = Path.Combine(channelDir, _options.FailureSubfolder);
        try
        {
            Directory.CreateDirectory(processed);
            Directory.CreateDirectory(cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudieron crear carpetas PROCESADOS/CANCELADOS bajo {Channel}", channelDir);
        }
    }

    /// <summary>
    /// Nombre destino en PROCESADOS: {stem}_{yyyyMMdd_HHmmss_fff}_{uploadId}.ext o ..._{uploadId}_{n}.ext si hay colisión.
    /// </summary>
    private static (string DestPath, string DestFileName) ResolveProcessedDestination(
        string sourcePath,
        string destinationDirectory,
        long uploadId)
    {
        Directory.CreateDirectory(destinationDirectory);
        var originalName = Path.GetFileName(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(originalName);
        var ext = Path.GetExtension(originalName);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
        var newName = $"{stem}_{stamp}_{uploadId}{ext}";
        var dest = Path.Combine(destinationDirectory, newName);
        if (!File.Exists(dest))
            return (dest, newName);

        for (var i = 1; i < 10_000; i++)
        {
            var altName = $"{stem}_{stamp}_{uploadId}_{i:0000}{ext}";
            var alt = Path.Combine(destinationDirectory, altName);
            if (!File.Exists(alt))
                return (alt, altName);
        }

        throw new IOException($"No se encontró nombre libre al renombrar en PROCESADOS: {originalName}");
    }

    private void MoveWithUniqueName(string sourcePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var name = Path.GetFileName(sourcePath);
        var dest = Path.Combine(destinationDirectory, name);
        if (!File.Exists(dest))
        {
            File.Move(sourcePath, dest, overwrite: false);
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; i < 10_000; i++)
        {
            dest = Path.Combine(destinationDirectory, $"{stem}_{i:0000}{ext}");
            if (!File.Exists(dest))
            {
                File.Move(sourcePath, dest, overwrite: false);
                return;
            }
        }

        throw new IOException($"No se encontró nombre libre para mover: {name}");
    }

    private void LogDealerTreeDocumentPath()
    {
        var rel = _options.DealerFolderTreePath;
        if (string.IsNullOrWhiteSpace(rel))
            return;

        var full = Path.IsPathRooted(rel) ? rel : Path.Combine(AppContext.BaseDirectory, rel);
        if (File.Exists(full))
            _logger.LogInformation("Documento de árbol de carpetas (referencia): {Path}", full);
        else
            _logger.LogWarning("No se encontró el JSON del árbol de carpetas: {Path}", full);
    }

    private static string SummarizeSqlitePath(string connectionString)
    {
        const string key = "Data Source=";
        var i = connectionString.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return "(SQLite)";
        var start = i + key.Length;
        var end = connectionString.IndexOf(';', start);
        var path = end < 0 ? connectionString[start..] : connectionString[start..end];
        return path.Trim().Trim('"');
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "";
        var t = root.Trim();
        if (t.Length >= 2 && t[^1] == Path.VolumeSeparatorChar)
            return t + Path.DirectorySeparatorChar;
        if (!t.EndsWith(Path.DirectorySeparatorChar) && !t.EndsWith(Path.AltDirectorySeparatorChar))
            return t + Path.DirectorySeparatorChar;
        return t;
    }
}
