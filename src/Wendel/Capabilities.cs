using System;
using System.Collections.Generic;
using System.Linq;

namespace Wendel;

/// <summary>
/// Reads a DDC/CI capabilities string, which looks like
/// <c>(prot(monitor)type(lcd)cmds(01 02)vcp(02 10 14(05 08) 60(0F 11))mccs_ver(2.1))</c>.
/// Monitors don't all format it alike, and some leave out the spaces between codes.
/// </summary>
internal static class Capabilities
{
    private const string Section = "vcp(";

    /// <summary>The VCP codes listed, sorted and without their permitted values; empty if there are none.</summary>
    public static IReadOnlyList<byte> VcpCodes(string capabilities)
    {
        var codes = new SortedSet<byte>();
        int start = FindSection(capabilities);
        if (start < 0)
            return codes.ToList();

        int depth = 1;
        for (int i = start; i < capabilities.Length && depth > 0; i++)
        {
            char c = capabilities[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (depth == 1 && i + 1 < capabilities.Length && Hex(c) >= 0 && Hex(capabilities[i + 1]) >= 0)
            {
                codes.Add((byte)(Hex(c) * 16 + Hex(capabilities[i + 1])));
                i++;
            }
        }
        return codes.ToList();
    }

    /// <summary>The codes as hexadecimal pairs separated by spaces.</summary>
    public static string Format(IEnumerable<byte> codes) => string.Join(" ", codes.Select(c => c.ToString("X2")));

    /// <summary>The position after "vcp(", which isn't to be confused with "vcpname(" or the end of a longer word.</summary>
    private static int FindSection(string capabilities)
    {
        int index = 0;
        while ((index = capabilities.IndexOf(Section, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (index == 0 || !char.IsLetterOrDigit(capabilities[index - 1]))
                return index + Section.Length;
            index += Section.Length;
        }
        return -1;
    }

    private static int Hex(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'a' && c <= 'f')
            return c - 'a' + 10;
        if (c >= 'A' && c <= 'F')
            return c - 'A' + 10;
        return -1;
    }
}
