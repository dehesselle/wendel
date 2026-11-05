// SPDX-FileCopyrightText: 2026 René de Hesselle <dehesselle@web.de>
//
// SPDX-License-Identifier: 0BSD

using System.Text;
using Xunit;

namespace Wendel.Tests;

public class SettingsTests
{
    [Fact]
    public void RoundTrips()
    {
        var settings = new Settings
        {
            BarPosition = BarPosition.BottomRightCorner,
            Opacity = 0.3,
            CollapsedThickness = 12,
            WheelStep = 2,
        };
        var copy = Settings.Deserialize(settings.Serialize());
        Assert.Equal(BarPosition.BottomRightCorner, copy.BarPosition);
        Assert.Equal(0.3, copy.Opacity);
        Assert.Equal(12, copy.CollapsedThickness);
        Assert.Equal(2, copy.WheelStep);
    }

    [Fact]
    public void MissingMembersKeepDefaults()
    {
        var copy = Settings.Deserialize(Encoding.UTF8.GetBytes("{\"WheelStep\":3}"));
        Assert.Equal(3, copy.WheelStep);
        Assert.Equal(BarPosition.Left, copy.BarPosition);
        Assert.Equal(0.5, copy.Opacity);
        Assert.Equal(8, copy.CollapsedThickness);
    }

    [Fact]
    public void UnknownPositionFallsBackToLeft()
    {
        var copy = Settings.Deserialize(Encoding.UTF8.GetBytes("{\"Position\":\"Middle\"}"));
        Assert.Equal(BarPosition.Left, copy.BarPosition);
    }

    [Fact]
    public void ClampsValues()
    {
        var settings = new Settings { Opacity = 5, CollapsedThickness = 0, WheelStep = 0 };
        Assert.Equal(1.0, settings.EffectiveOpacity);
        Assert.Equal(2, settings.EffectiveThickness);
        Assert.Equal(1, settings.EffectiveWheelStep);
    }
}
