namespace VGA.FileUploadWorker;

public static class CorreccionFileNaming
{
    private const string CorrectedSuffix = "_C";

    /// <summary>stem + "_C" + ext, sin duplicar "_C" si ya está en el nombre base.</summary>
    public static string ToCorrectedProcessedFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        if (stem.EndsWith(CorrectedSuffix, StringComparison.OrdinalIgnoreCase))
            return $"{stem}{ext}";
        return $"{stem}{CorrectedSuffix}{ext}";
    }
}
