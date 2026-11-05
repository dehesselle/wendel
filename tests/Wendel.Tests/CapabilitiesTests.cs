using System.Linq;
using Xunit;

namespace Wendel.Tests;

public class CapabilitiesTests
{
    private static string Codes(string capabilities) =>
        string.Join(" ", Capabilities.VcpCodes(capabilities).Select(c => c.ToString("X2")));

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

    /// <summary>As reported by a real Dell U3225QE.</summary>
    private const string U3225QE =
        "(prot(monitor)type(lcd)model(U3225QE)cmds(01 02 03 07 0C E3 F3)vcp(02 04 05 08 10 12 14(01 04 05 06 08 09 0B 0C) 16 18 1A 52 60( 19 0F 11) 66(00F2) 67 68 87 AA(00 01 04 02) AC AE B2 B6 C6(01) C8 C9 CA CC(02 03 04 06 09 0A 0D 0E) D6(01 04 05) DC(00 03 05) DF E0(03) E2(00 02 04 0C 0D 0F 10 11 13 0B 1B 1A 3D 14 23 24 27 3A) E5 E7(00 01 FF) E8 E9(00 01 02 21 22 24) EA(F800 F801 FE) EE EF(0F) F0(00 05 06 09 0A 31 32 34 36 A1) F1 F2 F5(100 200 500 600 700 800 900 A00 B00 C00 D00 E00 F00 1000 1100 1101 1102 1103 1104 1105 1106 1200 1300 1400 1500 1600 1700 1A00 1B00 1C00 1D00) FE FD)mccs_ver(2.1)mswhql(1))";

    [Fact]
    public void ReadsARealString()
    {
        Assert.Equal("U3225QE", Capabilities.Model(U3225QE));
        Assert.Equal(
            "02 04 05 08 10 12 14 16 18 1A 52 60 66 67 68 87 AA AC AE B2 B6 C6 C8 C9 CA CC D6 DC DF E0 E2 E5 E7 E8 E9 EA EE EF F0 F1 F2 F5 FD FE",
            Codes(U3225QE));
    }

    [Theory]
    [InlineData("(type(lcd)MODEL(Some Model)vcp(10))", "Some Model")]
    [InlineData("model( U3225QE )", "U3225QE")]
    [InlineData("(type(lcd)vcp(10))", null)]
    [InlineData("model()", null)]
    [InlineData("model(cut off", null)]
    [InlineData("submodel(X)", null)]
    [InlineData("", null)]
    public void ReadsTheModel(string text, string? expected)
    {
        Assert.Equal(expected, Capabilities.Model(text));
    }
}
