using Xunit;

namespace Wendel.Tests;

public class SliderLayoutTests
{
    [Theory]
    [InlineData(96, 0)]
    [InlineData(96, 37)]
    [InlineData(96, 100)]
    [InlineData(144, 0)]
    [InlineData(144, 63)]
    [InlineData(144, 100)]
    [InlineData(192, 50)]
    public void PercentRoundTripsThroughThumbPosition(int dpi, int percent)
    {
        var layout = new SliderLayout(1, dpi);
        var thumb = layout.ThumbBounds(0, percent);
        Assert.Equal(percent, layout.PercentAt(thumb.Top + thumb.Height / 2));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void PercentIsClampedOutsideTheTrack(int dpi)
    {
        var layout = new SliderLayout(1, dpi);
        var track = layout.TrackBounds(0);
        Assert.Equal(100, layout.PercentAt(track.Top - 500));
        Assert.Equal(0, layout.PercentAt(track.Bottom + 500));
    }

    [Theory]
    [InlineData(1, 96)]
    [InlineData(2, 96)]
    [InlineData(3, 96)]
    [InlineData(1, 144)]
    [InlineData(3, 144)]
    public void EveryColumnFindsItselfAtItsTrack(int monitors, int dpi)
    {
        var layout = new SliderLayout(monitors, dpi);
        for (int column = 0; column < monitors; column++)
        {
            var track = layout.TrackBounds(column);
            Assert.Equal(column, layout.ColumnAt(track.X + track.Width / 2));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void PositionsOutsideTheColumnsGoToTheNearestOne(int monitors)
    {
        var layout = new SliderLayout(monitors, 96);
        Assert.Equal(0, layout.ColumnAt(-100));
        Assert.Equal(0, layout.ColumnAt(0));
        Assert.Equal(monitors - 1, layout.ColumnAt(layout.PanelSize.Width + 100));
    }

    [Fact]
    public void NoMonitorsHaveNoColumn()
    {
        Assert.Equal(-1, new SliderLayout(0, 96).ColumnAt(10));
    }

    [Fact]
    public void PanelGrowsWithMonitorsAndScaling()
    {
        var one = new SliderLayout(1, 96).PanelSize;
        var two = new SliderLayout(2, 96).PanelSize;
        var scaled = new SliderLayout(1, 144).PanelSize;
        Assert.True(two.Width > one.Width);
        Assert.Equal(one.Height, two.Height);
        Assert.Equal(one.Width * 3 / 2, scaled.Width);
        Assert.Equal(one.Height * 3 / 2, scaled.Height);
    }

    [Theory]
    [InlineData(3, 96)]
    [InlineData(3, 144)]
    public void EverythingFitsInsideThePanel(int monitors, int dpi)
    {
        var layout = new SliderLayout(monitors, dpi);
        var panel = new System.Drawing.Rectangle(System.Drawing.Point.Empty, layout.PanelSize);
        for (int column = 0; column < monitors; column++)
        {
            Assert.True(panel.Contains(layout.LabelBounds(column)));
            Assert.True(panel.Contains(layout.ThumbBounds(column, 0)));
            Assert.True(panel.Contains(layout.ThumbBounds(column, 100)));
        }
    }
}
