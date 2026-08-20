using HyperDrop.Core.HyperV;

namespace HyperDrop.Core.Tests;

public sealed class HyperVErrorMessagesTests
{
    [Theory]
    [InlineData(0u, "completed successfully")]
    [InlineData(32769u, "Hyper-V Administrators")]
    [InlineData(32775u, "running")]
    [InlineData(32772u, "timed out")]
    public void ForMethodReturn_ExplainsKnownCodes(uint code, string expectedFragment)
    {
        var message = HyperVErrorMessages.ForMethodReturn(code);

        Assert.Contains(expectedFragment, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForMethodReturn_UnknownCode_StillMentionsTheNumber()
    {
        Assert.Contains("12345", HyperVErrorMessages.ForMethodReturn(12345));
    }

    [Fact]
    public void TryExtractHResult_FindsTheCodeInsideAHyperVMessage()
    {
        var hresult = HyperVErrorMessages.TryExtractHResult(
            "'WIN11' failed to copy the file. (0x80070050)");

        Assert.Equal(0x80070050u, hresult);
    }

    [Fact]
    public void TryExtractHResult_ReturnsNullWhenAbsent()
    {
        Assert.Null(HyperVErrorMessages.TryExtractHResult("Something went wrong."));
    }

    [Fact]
    public void ForJobFailure_AppendsTheDecodedHResultToHyperVsOwnText()
    {
        var message = HyperVErrorMessages.ForJobFailure(
            0,
            "'WIN11' failed to copy the file. (0x80070070)");

        Assert.Contains("failed to copy the file", message, StringComparison.Ordinal);
        Assert.Contains("out of disk space", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForJobFailure_WithNoDescription_FallsBackToTheErrorCode()
    {
        var message = HyperVErrorMessages.ForJobFailure(32769, errorDescription: null);

        Assert.Contains("administrator", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForJobFailure_WithNothingUseful_StillSaysSomething()
    {
        var message = HyperVErrorMessages.ForJobFailure(0, errorDescription: null);

        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Theory]
    [InlineData("Copy failed (0x80070050)", "Overwrite")]
    [InlineData("Copy failed (0x800700B7)", "Overwrite")]
    [InlineData("Copy failed (0x80070003)", "Create destination folders")]
    [InlineData("Copy failed (0x80070070)", "disk space")]
    [InlineData("Copy failed (0x80070005)", "local folder")]
    [InlineData("Copy failed (0x80070032)", "integration services")]
    public void RemedyFor_SuggestsTheMatchingFix(string errorText, string expectedFragment)
    {
        var remedy = HyperVErrorMessages.RemedyFor(errorText);

        Assert.NotNull(remedy);
        Assert.Contains(expectedFragment, remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemedyFor_FallsBackToTextMatchingWhenNoHResultIsPresent()
    {
        var remedy = HyperVErrorMessages.RemedyFor("A file named 'x' already exists in the guest.");

        Assert.NotNull(remedy);
        Assert.Contains("Overwrite", remedy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemedyFor_UnknownProblem_ReturnsNull()
    {
        Assert.Null(HyperVErrorMessages.RemedyFor("Something entirely unexpected happened."));
    }

    [Fact]
    public void RemedyFor_EmptyInput_ReturnsNull()
    {
        Assert.Null(HyperVErrorMessages.RemedyFor(null));
        Assert.Null(HyperVErrorMessages.RemedyFor("   "));
    }
}
