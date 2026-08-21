namespace MirrorPowerAI.Windows.Tests.Platform;

public sealed class SettingsWindowGeometryTests
{
    [Fact]
    public void TryCalculate_StandardWorkingAreaAt100Percent_CentersPreferredBounds()
    {
        var calculated = SettingsWindowGeometry.TryCalculate(
            new PhysicalWorkArea(0, 0, 1920, 1040),
            dpiScale: 1,
            out var placement);

        Assert.True(calculated);
        Assert.Equal(580, placement.Left);
        Assert.Equal(160, placement.Top);
        Assert.Equal(760, placement.Width);
        Assert.Equal(720, placement.Height);
        Assert.Equal(520, placement.MinimumWidth);
        Assert.Equal(520, placement.MinimumHeight);
        Assert.Equal(1766, placement.MaximumWidth);
        Assert.Equal(936, placement.MaximumHeight);
        AssertFits(new PhysicalWorkArea(0, 0, 1920, 1040), placement);
    }

    [Fact]
    public void TryCalculate_Constrained200PercentWorkingArea_CapsHeightAndRelaxesMinimum()
    {
        var calculated = SettingsWindowGeometry.TryCalculate(
            new PhysicalWorkArea(0, 0, 2560, 1040),
            dpiScale: 2,
            out var placement);

        Assert.True(calculated);
        Assert.Equal(520, placement.Left);
        Assert.Equal(52, placement.Top);
        Assert.Equal(1520, placement.Width);
        Assert.Equal(936, placement.Height);
        Assert.Equal(1040, placement.MinimumWidth);
        Assert.Equal(720, placement.MinimumHeight);
        Assert.Equal(2355, placement.MaximumWidth);
        Assert.Equal(936, placement.MaximumHeight);
        Assert.Equal(360, placement.MinimumHeight / 2);
        Assert.Equal(468, placement.MaximumHeight / 2);
        Assert.True(placement.MinimumHeight < placement.MaximumHeight);
        AssertFits(new PhysicalWorkArea(0, 0, 2560, 1040), placement);
    }

    [Fact]
    public void TryCalculate_SmallNegativeCoordinateWorkingArea_UsesBoundedCenteredGeometry()
    {
        var workArea = new PhysicalWorkArea(-1280, 50, -960, 300);

        var calculated = SettingsWindowGeometry.TryCalculate(workArea, dpiScale: 2, out var placement);

        Assert.True(calculated);
        Assert.Equal(-1267, placement.Left);
        Assert.Equal(62, placement.Top);
        Assert.Equal(294, placement.Width);
        Assert.Equal(225, placement.Height);
        Assert.Equal(placement.MaximumWidth, placement.MinimumWidth);
        Assert.Equal(placement.MaximumHeight, placement.MinimumHeight);
        AssertFits(workArea, placement);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TryCalculate_InvalidDpiScale_ReturnsFalse(double dpiScale)
    {
        var calculated = SettingsWindowGeometry.TryCalculate(
            new PhysicalWorkArea(0, 0, 800, 600),
            dpiScale,
            out var placement);

        Assert.False(calculated);
        Assert.Equal(default, placement);
    }

    [Fact]
    public void TryCalculate_EmptyWorkingArea_ReturnsFalse()
    {
        var calculated = SettingsWindowGeometry.TryCalculate(
            new PhysicalWorkArea(10, 20, 10, 500),
            dpiScale: 1,
            out var placement);

        Assert.False(calculated);
        Assert.Equal(default, placement);
    }

    [Fact]
    public void TryCalculate_ExtremeVirtualDesktopCoordinates_DoesNotOverflowAndStaysInBounds()
    {
        var workArea = new PhysicalWorkArea(int.MinValue, -100, int.MaxValue, 100);

        var calculated = SettingsWindowGeometry.TryCalculate(workArea, dpiScale: 1, out var placement);

        Assert.True(calculated);
        AssertFits(workArea, placement);
        Assert.InRange(placement.MaximumWidth, 1, int.MaxValue);
        Assert.InRange(placement.MaximumHeight, 1, int.MaxValue);
    }

    private static void AssertFits(PhysicalWorkArea workArea, SettingsWindowPlacement placement)
    {
        Assert.InRange(placement.Width, placement.MinimumWidth, placement.MaximumWidth);
        Assert.InRange(placement.Height, placement.MinimumHeight, placement.MaximumHeight);
        Assert.True((long)placement.Left >= workArea.Left);
        Assert.True((long)placement.Top >= workArea.Top);
        Assert.True((long)placement.Left + placement.Width <= workArea.Right);
        Assert.True((long)placement.Top + placement.Height <= workArea.Bottom);
    }
}
