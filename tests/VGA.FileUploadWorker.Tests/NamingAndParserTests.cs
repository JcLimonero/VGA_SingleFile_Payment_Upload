using VGA.FileUploadWorker;
using Xunit;

namespace VGA.FileUploadWorker.Tests;

public class PaymentFileNameParserTests
{
    [Theory]
    [InlineData("Acu_7399_23.pdf", "Acu", "7399")]
    [InlineData("Acu_(99)_x.pdf", "Acu", "99")]
    public void TryParse_LegacySingleOrder(string fileName, string agency, string order)
    {
        var ok = PaymentFileNameParser.TryParse(fileName, out var info);
        Assert.True(ok);
        Assert.NotNull(info);
        Assert.Equal(agency, info.Value.AgencyAbbreviation);
        Assert.Single(info.Value.OrderNumbers);
        Assert.Equal(order, info.Value.OrderNumbers[0]);
    }

    [Fact]
    public void TryParse_MultiOrderWithSpaces()
    {
        var ok = PaymentFileNameParser.TryParse("Acu_(1, 2, 3)_data.pdf", out var info);
        Assert.True(ok);
        Assert.NotNull(info);
        Assert.Equal("Acu", info.Value.AgencyAbbreviation);
        Assert.Equal(new[] { "1", "2", "3" }, info.Value.OrderNumbers);
    }

    [Fact]
    public void TryParse_DeduplicatesOrders()
    {
        var ok = PaymentFileNameParser.TryParse("Acu_(1,1,2)_x.pdf", out var info);
        Assert.True(ok);
        Assert.NotNull(info);
        Assert.Equal(new[] { "1", "2" }, info.Value.OrderNumbers);
    }

    [Theory]
    [InlineData("Acu_.pdf")]
    [InlineData("Acu_().pdf")]
    [InlineData("solo.pdf")]
    public void TryParse_InvalidNames(string fileName)
    {
        var ok = PaymentFileNameParser.TryParse(fileName, out var info);
        Assert.False(ok);
        Assert.Null(info);
    }
}

public class CorreccionFileNamingTests
{
    [Theory]
    [InlineData("doc.pdf", "doc_C.pdf")]
    [InlineData("doc_C.pdf", "doc_C.pdf")]
    public void ToCorrectedProcessedFileName_AddsSuffixOnce(string input, string expected)
    {
        Assert.Equal(expected, CorreccionFileNaming.ToCorrectedProcessedFileName(input));
    }
}

public class ProcessedFileNamingTests
{
    [Fact]
    public void ResolveProcessedDestination_UsesUploadIdInName()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var source = Path.Combine(temp, "Acu_1_a.pdf");
            File.WriteAllText(source, "x");

            var (_, destName) = ProcessedFileNaming.ResolveProcessedDestination(source, temp, 42);

            Assert.Contains("_42.pdf", destName, StringComparison.Ordinal);
            Assert.StartsWith("Acu_1_a_", destName, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
