namespace VGA.FileUploadWorker;

/// <summary>
/// Nombres esperados en EFECTIVO/TPV, p. ej. Acu_7399_23.pdf → agencia Acu, pedido 7399.
/// </summary>
public static class PaymentFileNameParser
{
    /// <summary>
    /// Intenta extraer abreviatura de agencia (antes del primer _) y número de pedido (segundo tramo entre _).
    /// </summary>
    public static bool TryParse(string fileName, out string? agencyAbbreviation, out string? orderNumber)
    {
        agencyAbbreviation = null;
        orderNumber = null;
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var stem = Path.GetFileNameWithoutExtension(fileName.Trim());
        if (stem.Length == 0)
            return false;

        var parts = stem.Split('_', StringSplitOptions.None);
        if (parts.Length < 2)
            return false;

        var agency = parts[0].Trim();
        var order = parts[1].Trim();
        if (agency.Length == 0 || order.Length == 0)
            return false;

        agencyAbbreviation = agency;
        orderNumber = order;
        return true;
    }
}
