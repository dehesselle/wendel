using Xunit;

namespace Wendel.Tests;

public class CapabilitiesTests
{
    private static string Codes(string capabilities) => Capabilities.Format(Capabilities.VcpCodes(capabilities));

    [Fact]
    public void ReadsCodesOfATypicalString()
    {
        const string text = "(prot(monitor)type(lcd)model(U2723QE)cmds(01 02 03 07 0C E3 F3)vcp(02 04 05 10 12 66 F0)mswhql(1)mccs_ver(2.1))";
        Assert.Equal("02 04 05 10 12 66 F0", Codes(text));
    }

    [Fact]
    public void SkipsPermittedValues()
    {
        Assert.Equal("10 14 60 62", Codes("vcp(10 14(05 08 0B) 60(0F 11 (12 13)) 62)"));
    }

    [Fact]
    public void ReadsCodesWithoutSpaces()
    {
        Assert.Equal("10 12 14 66", Codes("vcp(101214(0508)66)"));
    }

    [Fact]
    public void AcceptsOtherCaseAndSortsAndDropsDuplicates()
    {
        Assert.Equal("10 AB F0", Codes("VCP(f0 10 aB 10)"));
    }

    [Fact]
    public void IgnoresOtherSections()
    {
        Assert.Equal("10", Codes("cmds(01 02)vcpname(66(Foo))vcp(10)mccs_ver(2.1)"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("(prot(monitor)type(lcd))")]
    [InlineData("vcp()")]
    [InlineData("vcp(")]
    public void FindsNothingWithoutCodes(string text)
    {
        Assert.Empty(Capabilities.VcpCodes(text));
    }

    [Fact]
    public void KeepsCodesOfATruncatedString()
    {
        Assert.Equal("10 12", Codes("vcp(10 12 1"));
    }
}
