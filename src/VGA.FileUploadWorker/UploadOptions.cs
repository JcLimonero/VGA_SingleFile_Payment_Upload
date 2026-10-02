namespace VGA.FileUploadWorker;

public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    /// <summary>Raíz del árbol (por ejemplo Z:\).</summary>
    public string RootPath { get; set; } = "";

    /// <summary>
    /// Nombres de carpeta “canal” donde pueden aparecer archivos pendientes (solo en el nivel raíz de esa carpeta, no dentro de subcarpetas).
    /// </summary>
    public List<string> ChannelFolderNames { get; set; } = new() { "EFECTIVO", "TPV", "DB" };

    /// <summary>Tras importar correctamente, el archivo se mueve a esta subcarpeta dentro del mismo canal.</summary>
    /// <remarks>Se crea junto con <see cref="FailureSubfolder"/> si no existen.</remarks>
    public string SuccessSubfolder { get; set; } = "PROCESADOS";

    /// <summary>Si falla la importación, el archivo se mueve a esta subcarpeta dentro del mismo canal.</summary>
    /// <remarks>Se crea junto con <see cref="SuccessSubfolder"/> si no existen.</remarks>
    public string FailureSubfolder { get; set; } = "CORRECCION";

    /// <summary>Intervalo entre escaneos (segundos).</summary>
    public int ScanIntervalSeconds { get; set; } = 10;

    /// <summary>Patrón de archivos (por ejemplo *.xml o *.*).</summary>
    public string FileSearchPattern { get; set; } = "*.*";

    /// <summary>Si es false, no se persiste el binario en BD (solo hash y metadatos).</summary>
    public bool StoreFileContent { get; set; } = true;

    /// <summary>
    /// Ruta relativa al directorio del ejecutable del documento JSON del árbol de carpetas (referencia para el equipo).
    /// </summary>
    public string? DealerFolderTreePath { get; set; }

    /// <summary>
    /// Si es true, los archivos cuyo nombre no siga el patrón Agencia_pedido_... no se insertan en BD y van a la carpeta de fallo.
    /// </summary>
    public bool RequireValidPaymentFileName { get; set; }

    /// <summary>
    /// Minutos de espera antes de volver a intentar archivos que siguieron en el canal (vista sin fila, error MySQL, nombre sin parsear, etc.).
    /// Los archivos nuevos (sin fila en esta tabla) se intentan en cada escaneo normal.
    /// </summary>
    public int RetryPendingIntervalMinutes { get; set; } = 30;

    public HashSet<string> ChannelNameSet(StringComparer comparer) =>
        new(ChannelFolderNames.Where(static s => !string.IsNullOrWhiteSpace(s)).Select(static s => s.Trim()), comparer);
}
