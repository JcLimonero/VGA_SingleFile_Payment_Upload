using Microsoft.Extensions.Options;

namespace VGA.FileUploadWorker;

public sealed class CorrectionFolderProcessor
{
    private readonly IDocumentByFileCorreccionService _documentCorreccion;
    private readonly IFileObtainLogWriter _obtainLog;
    private readonly UploadOptions _options;
    private readonly ILogger<CorrectionFolderProcessor> _logger;

    public CorrectionFolderProcessor(
        IDocumentByFileCorreccionService documentCorreccion,
        IFileObtainLogWriter obtainLog,
        IOptions<UploadOptions> options,
        ILogger<CorrectionFolderProcessor> logger)
    {
        _documentCorreccion = documentCorreccion;
        _obtainLog = obtainLog;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(
        string root,
        string channelDir,
        string channelName,
        CancellationToken cancellationToken)
    {
        var correctionDir = Path.Combine(channelDir, _options.FailureSubfolder);
        if (!Directory.Exists(correctionDir))
            return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(correctionDir, _options.FileSearchPattern, SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo listar archivos en CORRECCION: {Path}", correctionDir);
            return;
        }

        var processedDir = Path.Combine(channelDir, _options.SuccessSubfolder);
        Directory.CreateDirectory(processedDir);

        foreach (var fullPath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessOneFileAsync(root, correctionDir, processedDir, channelName, fullPath, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ProcessOneFileAsync(
        string root,
        string correctionDir,
        string processedDir,
        string channelName,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var originalName = Path.GetFileName(fullPath);
        var sourceRelative = Path.GetRelativePath(root, fullPath);
        string? agency = null;
        string? order = null;
        if (PaymentFileNameParser.TryParse(originalName, out var fileInfo) && fileInfo is not null)
        {
            agency = fileInfo.Value.AgencyAbbreviation;
            order = string.Join(",", fileInfo.Value.OrderNumbers);
        }

        _logger.LogInformation(
            "Corrección: procesando {File} en {CorrectionDir} (canal {Channel})",
            originalName,
            correctionDir,
            channelName);

        var result = await _documentCorreccion.TryDisableByPathDocumentAsync(originalName, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case DocumentByFileCorreccionStatus.Disabled:
                _logger.LogInformation(
                    "Corrección: PathDocument={PathDocument} desactivado en documentbyfile (filas={Rows})",
                    originalName,
                    result.RowsAffected);
                break;
            case DocumentByFileCorreccionStatus.AlreadyDisabled:
                _logger.LogInformation(
                    "Corrección: PathDocument={PathDocument} ya estaba deshabilitado (Enabled=0)",
                    originalName);
                break;
            case DocumentByFileCorreccionStatus.NotFound:
                _logger.LogWarning(
                    "Corrección: sin fila en documentbyfile para PathDocument={PathDocument}; archivo permanece en CORRECCION",
                    originalName);
                await _obtainLog.WriteAsync(
                    new FileObtainedLogEntry(
                        originalName,
                        sourceRelative,
                        channelName,
                        agency,
                        order,
                        ObtainOutcomes.CorreccionFailed,
                        $"PathDocument={originalName}; estado=NotFound",
                        null),
                    cancellationToken).ConfigureAwait(false);
                return;
            case DocumentByFileCorreccionStatus.Failed:
                _logger.LogError(
                    "Corrección: error MySQL para PathDocument={PathDocument}: {Detail}",
                    originalName,
                    result.ErrorDetail);
                await _obtainLog.WriteAsync(
                    new FileObtainedLogEntry(
                        originalName,
                        sourceRelative,
                        channelName,
                        agency,
                        order,
                        ObtainOutcomes.CorreccionFailed,
                        $"PathDocument={originalName}; estado=Failed; {result.ErrorDetail}",
                        null),
                    cancellationToken).ConfigureAwait(false);
                return;
        }

        var destName = CorreccionFileNaming.ToCorrectedProcessedFileName(originalName);
        try
        {
            var destPath = ProcessedFileNaming.MoveToDestinationWithFileName(fullPath, processedDir, destName);
            var finalName = Path.GetFileName(destPath);
            var detail =
                $"PathDocument={originalName}; destino={finalName}; filas={result.RowsAffected}; estado={result.Status}";
            _logger.LogInformation(
                "Corrección: {Original} desactivado en documentbyfile (Enabled=0), movido a PROCESADOS como {DestName} ({DestPath})",
                originalName,
                finalName,
                destPath);

            await _obtainLog.WriteAsync(
                new FileObtainedLogEntry(
                    originalName,
                    sourceRelative,
                    channelName,
                    agency,
                    order,
                    ObtainOutcomes.CorreccionProcessed,
                    detail,
                    null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Corrección: no se pudo mover {File} a PROCESADOS como {DestName}",
                originalName,
                destName);
            await _obtainLog.WriteAsync(
                new FileObtainedLogEntry(
                    originalName,
                    sourceRelative,
                    channelName,
                    agency,
                    order,
                    ObtainOutcomes.CorreccionFailed,
                    $"PathDocument={originalName}; destino={destName}; moveError={ex.Message}",
                    null),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
