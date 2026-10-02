using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VGA.FileUploadWorker;

/// <summary>Nombre y Content-Type seguros para el multipart que se envía al API de Backblaze.</summary>
public static class UploadFileMetadata
{
    public const string DefaultMime = "application/octet-stream";

    private static readonly Regex InvalidChars = new("[^A-Za-z0-9 ._,()\\-]", RegexOptions.Compiled);
    private static readonly Regex RepeatedDots = new("\\.{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Quita acentos (ñ → n, é → e) y reemplaza el resto de caracteres fuera de ASCII por «_».
    /// El API no interpreta el nombre codificado que .NET genera para caracteres no ASCII y rechaza la extensión.
    /// </summary>
    public static string ToAsciiSafeFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return fileName;

        var decomposed = fileName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        var result = InvalidChars.Replace(sb.ToString().Normalize(NormalizationForm.FormC), "_");
        return RepeatedDots.Replace(result, ".");
    }

    public static string GuessMime(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            ".bmp" => "image/bmp",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".xml" => "application/xml",
            ".json" => "application/json",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".zip" => "application/zip",
            _ => DefaultMime,
        };
}
