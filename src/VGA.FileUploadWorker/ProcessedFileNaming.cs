namespace VGA.FileUploadWorker;

public static class ProcessedFileNaming
{
    /// <summary>
    /// Nombre destino en PROCESADOS: {stem}_{yyyyMMdd_HHmmss_fff}_{uploadId}.ext o ..._{uploadId}_{n}.ext si hay colisión.
    /// </summary>
    public static (string DestPath, string DestFileName) ResolveProcessedDestination(
        string sourcePath,
        string destinationDirectory,
        long uploadId)
    {
        Directory.CreateDirectory(destinationDirectory);
        var originalName = Path.GetFileName(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(originalName);
        var ext = Path.GetExtension(originalName);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
        var newName = $"{stem}_{stamp}_{uploadId}{ext}";
        var dest = Path.Combine(destinationDirectory, newName);
        if (!File.Exists(dest))
            return (dest, newName);

        for (var i = 1; i < 10_000; i++)
        {
            var altName = $"{stem}_{stamp}_{uploadId}_{i:0000}{ext}";
            var alt = Path.Combine(destinationDirectory, altName);
            if (!File.Exists(alt))
                return (alt, altName);
        }

        throw new IOException($"No se encontró nombre libre al renombrar en PROCESADOS: {originalName}");
    }

    public static void MoveWithUniqueName(string sourcePath, string destinationDirectory)
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

    /// <summary>Mueve a destino con nombre fijo; si existe, añade _0001 antes de la extensión.</summary>
    public static string MoveToDestinationWithFileName(string sourcePath, string destinationDirectory, string destFileName)
    {
        Directory.CreateDirectory(destinationDirectory);
        var dest = Path.Combine(destinationDirectory, destFileName);
        if (!File.Exists(dest))
        {
            File.Move(sourcePath, dest, overwrite: false);
            return dest;
        }

        var stem = Path.GetFileNameWithoutExtension(destFileName);
        var ext = Path.GetExtension(destFileName);
        for (var i = 1; i < 10_000; i++)
        {
            var altName = $"{stem}_{i:0000}{ext}";
            var alt = Path.Combine(destinationDirectory, altName);
            if (!File.Exists(alt))
            {
                File.Move(sourcePath, alt, overwrite: false);
                return alt;
            }
        }

        throw new IOException($"No se encontró nombre libre en PROCESADOS para: {destFileName}");
    }
}
