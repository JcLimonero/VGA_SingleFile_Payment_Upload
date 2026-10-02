using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace VGA.FileUploadWorker;

public sealed class DsMonitorClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly DsMonitorOptions _options;
    private readonly ILogger<DsMonitorClient> _logger;

    public DsMonitorClient(HttpClient http, IOptions<DsMonitorOptions> options, ILogger<DsMonitorClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.Enabled && !string.IsNullOrWhiteSpace(_options.Token);

    /// <summary>Nunca lanza: un DS Monitor caído no debe afectar al worker.</summary>
    public async Task ReportAsync(string status, string? message, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return;

        var report = new CorridaReport(
            _options.Integration,
            _options.DisplayName,
            status,
            message,
            Math.Max(1, _options.IntervalMinutes));

        try
        {
            var json = JsonSerializer.Serialize(report, JsonOptions);
            var url = $"{_options.BaseUrl.TrimEnd('/')}/ingesta/ejecuciones";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("DS Monitor: {Status} {Body}", (int)response.StatusCode, body);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DS Monitor: sin respuesta: {Message}", ex.Message);
        }
    }

    private sealed record CorridaReport(
        [property: JsonPropertyName("integracion")] string Integracion,
        [property: JsonPropertyName("nombre")] string Nombre,
        [property: JsonPropertyName("estado")] string Estado,
        [property: JsonPropertyName("mensaje")] string? Mensaje,
        [property: JsonPropertyName("cadaMinutos")] int CadaMinutos);
}
