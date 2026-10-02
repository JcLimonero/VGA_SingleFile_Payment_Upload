namespace VGA.FileUploadWorker;

public interface IPendingImportRetryStore
{
    /// <summary>Si el archivo está en periodo de espera entre reintentos, no se procesa en este escaneo.</summary>
    Task<bool> ShouldDeferAsync(string sourceRelativePath, CancellationToken cancellationToken);

    /// <summary>Programa el siguiente intento tras el intervalo configurado en Upload:RetryPendingIntervalMinutes.</summary>
    Task ScheduleRetryAsync(string sourceRelativePath, string lastOutcome, CancellationToken cancellationToken);

    /// <summary>Quita la espera al importar con éxito (archivo ya en PROCESADOS).</summary>
    Task ClearAsync(string sourceRelativePath, CancellationToken cancellationToken);

    Task<string?> GetLastOutcomeAsync(string sourceRelativePath, CancellationToken cancellationToken);
}
