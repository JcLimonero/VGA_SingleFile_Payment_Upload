using Microsoft.Extensions.Options;

namespace VGA.FileUploadWorker;

public sealed class FolderUploadBackgroundService : BackgroundService
{
    private readonly ILogger<FolderUploadBackgroundService> _logger;
    private readonly ISqliteConnectionProvider _sqlite;
    private readonly PaymentFileImportService _importService;
    private readonly CorrectionFolderProcessor _correctionProcessor;
    private readonly UploadOptions _options;

    public FolderUploadBackgroundService(
        ILogger<FolderUploadBackgroundService> logger,
        ISqliteConnectionProvider sqlite,
        PaymentFileImportService importService,
        CorrectionFolderProcessor correctionProcessor,
        IOptions<UploadOptions> options)
    {
        _logger = logger;
        _sqlite = sqlite;
        _importService = importService;
        _correctionProcessor = correctionProcessor;
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
            await _correctionProcessor.ProcessAsync(root, channelDir, channelName, cancellationToken).ConfigureAwait(false);

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

            var successDir = Path.Combine(channelDir, _options.SuccessSubfolder);
            var failureDir = Path.Combine(channelDir, _options.FailureSubfolder);

            foreach (var fullPath in filesInChannel)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, fullPath);
                await _importService.ProcessFileAsync(
                    fullPath,
                    relative,
                    channelName,
                    successDir,
                    failureDir,
                    cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Bajo cada canal (EFECTIVO, TPV, DB, etc.) deben existir las carpetas de éxito y fallo; si faltan, se crean.
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
            _logger.LogWarning(ex, "No se pudieron crear carpetas PROCESADOS/CORRECCION bajo {Channel}", channelDir);
        }
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
