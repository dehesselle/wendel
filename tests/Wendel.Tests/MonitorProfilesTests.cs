using Xunit;

namespace Wendel.Tests;

public class MonitorProfilesTests
{
    [Theory]
    [InlineData(1, 2, false)]
    [InlineData(2, 2, true)]
    [InlineData(1, 1, false)]
    [InlineData(2, 1, true)]
    public void StandardDecodesWhatTheStandardDefines(int reported, int maximum, bool expected)
    {
        Assert.Equal(expected, MonitorProfiles.Standard.Decode(reported, maximum));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(3, 2)]
    [InlineData(0x81, 1)]
    [InlineData(0x82, 1)]
    [InlineData(2, 0)]
    public void StandardRejectsEverythingElse(int reported, int maximum)
    {
        Assert.Null(MonitorProfiles.Standard.Decode(reported, maximum));
    }

    [Theory]
    [InlineData(0x81, 1, false)]
    [InlineData(0x82, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(2, 1, true)]
    public void U3225QEDecodesTheHighBit(int reported, int maximum, bool expected)
    {
        Assert.Equal(expected, MonitorProfiles.For("U3225QE").Decode(reported, maximum));
    }

    [Theory]
    [InlineData(0x83, 1)]
    [InlineData(0x80, 1)]
    [InlineData(0x82, 0)]
    public void U3225QERejectsEverythingElse(int reported, int maximum)
    {
        Assert.Null(MonitorProfiles.For("U3225QE").Decode(reported, maximum));
    }

    [Fact]
    public void U3225QEWritesPlainValues()
    {
        var control = MonitorProfiles.For("U3225QE");
        Assert.Equal(0x66, control.Code);
        Assert.Equal(1, control.Off);
        Assert.Equal(2, control.On);
    }

    [Fact]
    public void ZeroCanBeAValue()
    {
        var control = new AutoBrightnessControl(0xE8, off: 0, on: 1);
        Assert.Equal(false, control.Decode(0, 1));
        Assert.Equal(true, control.Decode(1, 1));
    }

    [Theory]
    [InlineData("u3225qe")]
    [InlineData("U3225QE")]
    public void ModelsMatchIgnoringCase(string model)
    {
        Assert.NotSame(MonitorProfiles.Standard, MonitorProfiles.For(model));
        Assert.Equal("U3225QE", MonitorProfiles.NameFor(model.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("P2715Q")]
    [InlineData("U3225")]
    [InlineData(null)]
    public void OtherModelsGetTheStandard(string? model)
    {
        Assert.Same(MonitorProfiles.Standard, MonitorProfiles.For(model));
        Assert.Equal(MonitorProfiles.StandardName, MonitorProfiles.NameFor(model));
    }
}
