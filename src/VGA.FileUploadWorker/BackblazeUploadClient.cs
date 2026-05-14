using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace VGA.FileUploadWorker;

public sealed class BackblazeUploadClient : IBackblazeUploadClient
{
    public const string HttpClientName = "Backblaze";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<BackblazeUploadOptions> _options;

    public BackblazeUploadClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<BackblazeUploadOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<(bool Success, string? ErrorDetail)> UploadAsync(
        string filePath,
        long idSingleFile,
        long idDocumentFile,
        CancellationToken cancellationToken)
    {
        var o = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.UploadUrl))
            return (false, "BackblazeUpload:UploadUrl no está configurado.");

        if (!File.Exists(filePath))
            return (false, $"No existe el archivo a subir: {filePath}");

        var uri = new Uri(o.UploadUrl.Trim(), UriKind.Absolute);
        var maxAttempts = 1 + Math.Max(0, o.MaxRetries);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri);
                foreach (var (key, value) in o.Headers)
                {
                    if (string.IsNullOrWhiteSpace(key) || value is null)
                        continue;
                    request.Headers.TryAddWithoutValidation(key.Trim(), value);
                }

                await using var fileStream = File.OpenRead(filePath);
                using var streamContent = new StreamContent(fileStream);
                streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                using var form = new MultipartFormDataContent();
                form.Add(streamContent, "file", Path.GetFileName(filePath));
                form.Add(new StringContent(idSingleFile.ToString(CultureInfo.InvariantCulture)), "idSingleFile");
                form.Add(new StringContent(idDocumentFile.ToString(CultureInfo.InvariantCulture)), "idDocumentFile");
                request.Content = form;

                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return (true, null);

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var detail = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 500)}";

                if (!ShouldRetry(response.StatusCode) || attempt == maxAttempts - 1)
                    return (false, detail);

                await Task.Delay(250 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == maxAttempts - 1)
                    return (false, "Tiempo de espera agotado al subir a Backblaze.");
                await Task.Delay(250 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == maxAttempts - 1)
                    return (false, ex.Message);
                await Task.Delay(250 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }

        return (false, "Reintentos de subida agotados.");
    }

    private static bool ShouldRetry(HttpStatusCode code) =>
        code == HttpStatusCode.RequestTimeout
        || code == HttpStatusCode.TooManyRequests
        || (int)code >= 500;

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";
}
