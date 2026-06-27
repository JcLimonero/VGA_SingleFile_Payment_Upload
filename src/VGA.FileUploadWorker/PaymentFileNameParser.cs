namespace VGA.FileUploadWorker;

/// <summary>
/// Nombres en carpetas de canal (EFECTIVO, TPV, DB, etc.):
/// legacy <c>Acu_7399_23.pdf</c> → agencia Acu, pedido 7399;
/// multi-pedido <c>Acu_(1,2,3)_data.pdf</c> → agencia Acu, pedidos 1, 2, 3.
/// </summary>
public static class PaymentFileNameParser
{
    public readonly record struct PaymentFileNameInfo(
        string AgencyAbbreviation,
        IReadOnlyList<string> OrderNumbers);

    /// <summary>
    /// Extrae agencia (primer tramo) y uno o más pedidos (segundo tramo: legacy o entre paréntesis separados por coma).
    /// </summary>
    public static bool TryParse(string fileName, out PaymentFileNameInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var stem = Path.GetFileNameWithoutExtension(fileName.Trim());
        if (stem.Length == 0)
            return false;

        var parts = stem.Split('_', StringSplitOptions.None);
        if (parts.Length < 2)
            return false;

        var agency = parts[0].Trim();
        if (agency.Length == 0)
            return false;

        if (!TryParseOrderSegment(parts[1], out var orders))
            return false;

        orders = orders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (orders.Count == 0)
            return false;

        info = new PaymentFileNameInfo(agency, orders);
        return true;
    }

    private static bool TryParseOrderSegment(string segment, out List<string> orders)
    {
        orders = new List<string>();
        var trimmed = segment.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')')
        {
            var inner = trimmed[1..^1];
            foreach (var part in inner.Split(','))
            {
                var order = part.Trim();
                if (order.Length > 0)
                    orders.Add(order);
            }

            return orders.Count > 0;
        }

        orders.Add(trimmed);
        return true;
    }
}
