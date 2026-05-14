namespace VGA.FileUploadWorker;

public sealed class BackblazeUploadOptions
{
    public const string SectionName = "BackblazeUpload";

    public bool Enabled { get; set; }

    /// <summary>URL completa del endpoint POST multipart.</summary>
    public string UploadUrl { get; set; } = "https://apisvanguardia.com:400/backblaze/upload";

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Reintentos adicionales tras el primer intento (0 = solo un intento).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Cabeceras HTTP opcionales (p. ej. Authorization). Claves con espacios se omiten.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
