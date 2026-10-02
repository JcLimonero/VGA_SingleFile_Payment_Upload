using VGA.FileUploadWorker;
using Xunit;

namespace VGA.FileUploadWorker.Tests;

public class UploadFileMetadataTests
{
    [Theory]
    [InlineData("GGA_2661_REPORTE DE CUPON_10875990 MAURICIO CASTAÑEDA HUESCA.pdf", "GGA_2661_REPORTE DE CUPON_10875990 MAURICIO CASTANEDA HUESCA.pdf")]
    [InlineData("GGM_416_Desembolso Cristian Vinalay García.pdf", "GGM_416_Desembolso Cristian Vinalay Garcia.pdf")]
    [InlineData("BMW_PV243732_Impresión NBXI.pdf", "BMW_PV243732_Impresion NBXI.pdf")]
    [InlineData("KAG_20190_WhatsApp Scan 2026-06-16 at 5.45.17 p. m..pdf", "KAG_20190_WhatsApp Scan 2026-06-16 at 5.45.17 p. m.pdf")]
    [InlineData("Acu_7399_23.pdf", "Acu_7399_23.pdf")]
    [InlineData("VCO_3387_IRMA 260,450.00.pdf", "VCO_3387_IRMA 260,450.00.pdf")]
    [InlineData("a/b$c.pdf", "a_b_c.pdf")]
    public void ToAsciiSafeFileName_RemovesDiacriticsAndKeepsExtension(string input, string expected)
    {
        Assert.Equal(expected, UploadFileMetadata.ToAsciiSafeFileName(input));
    }

    [Theory]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.JPEG", "image/jpeg")]
    [InlineData("AUD_13505_PAGO.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("a.unknown", "application/octet-stream")]
    [InlineData("sinextension", "application/octet-stream")]
    public void GuessMime_FallsBackToOctetStream(string path, string expected)
    {
        Assert.Equal(expected, UploadFileMetadata.GuessMime(path));
    }

    [Theory]
    [InlineData("{\"message\":\"Error al subir archivo: no tomes available\"}", true)]
    [InlineData("{\"message\":\"Error al subir archivo: incident id 30a61381abdb\"}", true)]
    [InlineData("{\"message\":\"Error al subir archivo: Extensión de archivo no permitida\"}", false)]
    [InlineData(null, false)]
    public void IsTransientBackendError_DetectsBackblazeTransientMessages(string? body, bool expected)
    {
        Assert.Equal(expected, BackblazeUploadClient.IsTransientBackendError(body));
    }
}
