namespace VGA.FileUploadWorker;

public sealed class DsMonitorOptions
{
    public const string SectionName = "DsMonitor";

    /// <summary>Si es false, no se envía ningún heartbeat.</summary>
    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "https://portal.dealersolutions.com.mx/api/portal";

    /// <summary>Token Bearer. Si está vacío, el heartbeat queda deshabilitado (usar user-secrets o variable de entorno DsMonitor__Token).</summary>
    public string? Token { get; set; }

    /// <summary>Identificador de la integración en DS Monitor.</summary>
    public string Integration { get; set; } = "vga-payment-upload";

    /// <summary>Nombre legible de la integración.</summary>
    public string DisplayName { get; set; } = "VGA:SingleFile:PaymentUpload";

    /// <summary>Cada cuántos minutos se envía el heartbeat (también se informa al monitor como cadencia esperada).</summary>
    public int IntervalMinutes { get; set; } = 30;
}
