using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace VGA.FileUploadWorker;

public sealed class FolderUploadBackgroundService : BackgroundService
{
    private readonly ILogger<FolderUploadBackgroundService> _logger;
    private readonly IFileUploadRepository _repository;
    private readonly IFileObtainLogWriter _obtainLog;
    private readonly ISqliteConnectionProvider _sqlite;
    private readonly UploadOptions _options;

    public FolderUploadBackgroundService(
        ILogger<FolderUploadBackgroundService> logger,
        IFileUploadRepository repository,
        IFileObtainLogWriter obtainLog,
        ISqliteConnectionProvider sqlite,
        IOptions<UploadOptions> options)
    {
        _logger = logger;
        _repository = repository;
        _obtainLog = obtainLog;
        _sqlite = sqlite;
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

            foreach (var fullPath in Directory.EnumerateFiles(channelDir, _options.FileSearchPattern, SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, fullPath);
                await TryProcessOneFileAsync(fullPath, relative, channelName, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<string> EnumerateChannelDirectories(string root, HashSet<string> channels)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (leaf.Length == 0 || !channels.Contains(leaf))
                continue;
            yield return dir;
        }
    }

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

                MoveWithUniqueName(fullPath, successDir);
                await _obtainLog.WriteAsync(
                    new FileObtainedLogEntry(
                        name,
                        sourceRelativePath,
                        channel,
                        agency,
                        order,
                        ObtainOutcomes.Imported,
                        parsed ? null : "Importado sin agencia/pedido parseados en el nombre",
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
