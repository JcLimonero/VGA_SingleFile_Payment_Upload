namespace VGA.FileUploadWorker;

public sealed class BackblazeUploadOptions
{
    public const string SectionName = "BackblazeUpload";

    public bool Enabled { get; set; }

    /// <summary>URL completa del endpoint POST multipart.</summary>
    public string UploadUrl { get; set; } = "http://192.168.190.140:455/backblaze/upload";

    /// <summary>Timeout por intento HTTP (red interna).</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Reintentos adicionales tras el primer intento (0 = solo un intento).</summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>Cabeceras HTTP opcionales (p. ej. Authorization). Claves con espacios se omiten.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
