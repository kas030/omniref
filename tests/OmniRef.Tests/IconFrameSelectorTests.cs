using OmniRef.App.Services;

namespace OmniRef.Tests;

public sealed class IconFrameSelectorTests
{
    private static readonly int[] AvailableWidths =
        [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [Theory]
    [InlineData(20, 20)]
    [InlineData(25, 24)]
    [InlineData(30, 32)]
    [InlineData(35, 32)]
    [InlineData(40, 40)]
    public void SelectBestWidth_ChoosesClosestFrame(
        int targetPixelWidth,
        int expectedPixelWidth)
    {
        var result = IconFrameSelector.SelectBestWidth(
            AvailableWidths,
            targetPixelWidth);

        Assert.Equal(expectedPixelWidth, result);
    }

    [Fact]
    public void SelectBestWidth_PrefersLargerFrameWhenDistancesMatch()
    {
        var result = IconFrameSelector.SelectBestWidth([24, 32], 28);

        Assert.Equal(32, result);
    }
}
