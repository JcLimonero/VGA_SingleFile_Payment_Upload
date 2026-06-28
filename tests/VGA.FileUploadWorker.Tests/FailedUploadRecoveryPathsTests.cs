using VGA.FileUploadWorker;
using Xunit;

namespace VGA.FileUploadWorker.Tests;

public class FailedUploadRecoveryPathsTests
{
    [Fact]
    public void ResolveOriginalPath_CombinesRootAndRelative()
    {
        var path = FailedUploadRecoveryPaths.ResolveOriginalPath(
            @"Z:\",
            @"Dealer\Agencia\DB\Acu_7399_23.pdf");

        Assert.EndsWith("Dealer\\Agencia\\DB\\Acu_7399_23.pdf", path, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Z:", path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, @"Z:\x\DB\PROCESADOS\f.pdf", true, false, RecoveryMoveAction.Move)]
    [InlineData(true, @"Z:\x\DB\PROCESADOS\f.pdf", false, false, RecoveryMoveAction.DisableOnly)]
    [InlineData(true, @"Z:\x\DB\PROCESADOS\f.pdf", true, true, RecoveryMoveAction.DisableOnly)]
    [InlineData(false, null, false, false, RecoveryMoveAction.DisableOnly)]
    public void ResolveAction_ReturnsExpectedAction(
        bool processSucceeded,
        string? processedPath,
        bool sourceExists,
        bool destinationExists,
        RecoveryMoveAction expected)
    {
        var action = FailedUploadRecoveryPaths.ResolveAction(
            processSucceeded,
            processedPath,
            sourceExists,
            destinationExists,
            out _);

        Assert.Equal(expected, action);
    }

    [Fact]
    public void BuildGroupKey_UsesPaymentUploadIdWhenPresent()
    {
        var row = new FailedUploadTrackingRow(
            1, 42, "a\\DB\\f.pdf", "DB", "f.pdf", true, "Z:\\p.pdf", "f_stamped.pdf", 100, "err");

        Assert.Equal("upload:42", FailedUploadRecoveryPaths.BuildGroupKey(row));
    }

    [Fact]
    public void BuildGroupKey_FallsBackToPathWhenNoPaymentUploadId()
    {
        var row = new FailedUploadTrackingRow(
            1, null, "a\\DB\\f.pdf", "DB", "f.pdf", false, null, null, 100, "err");

        Assert.Equal("path:a\\DB\\f.pdf|", FailedUploadRecoveryPaths.BuildGroupKey(row));
    }

    [Fact]
    public void BuildPlanItem_AggregatesDocumentByFileIdsForMultiOrder()
    {
        var rows = new[]
        {
            new FailedUploadTrackingRow(1, 10, "d\\DB\\Acu_(1,2).pdf", "DB", "Acu_(1,2).pdf", false, null, null, 101, "cloud err"),
            new FailedUploadTrackingRow(2, 10, "d\\DB\\Acu_(1,2).pdf", "DB", "Acu_(1,2).pdf", false, null, null, 102, "cloud err"),
        };

        var item = FailedUploadRecoveryPaths.BuildPlanItem("upload:10", rows, @"Z:\");

        Assert.Equal(RecoveryMoveAction.DisableOnly, item.Action);
        Assert.Equal(new long[] { 101, 102 }, item.DocumentByFileIds);
        Assert.Equal(new long[] { 1, 2 }, item.TrackingIds);
    }
}
