// SPDX-FileCopyrightText: 2026 René de Hesselle <dehesselle@web.de>
//
// SPDX-License-Identifier: 0BSD

using System;
using System.IO;
using Xunit;

namespace Wendel.Tests;

[Collection("Log")]
public sealed class LogTests : IDisposable
{
    private readonly string originalPath = Log.FilePath;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "wendel-tests-" + Guid.NewGuid().ToString("N"));

    public LogTests()
    {
        Log.FilePath = Path.Combine(dir, "wendel.log");
    }

    public void Dispose()
    {
        Log.FilePath = originalPath;
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    [Fact]
    public void WritesEntryWithIndentedDetails()
    {
        Log.Error("Something failed", "first\nsecond\r\n");
        var lines = File.ReadAllLines(Log.FilePath);
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} ERROR Something failed$", lines[0]);
        Assert.Equal("  first", lines[1]);
        Assert.Equal("  second", lines[2]);
    }

    [Fact]
    public void AppendsEntries()
    {
        Log.Info("one");
        Log.Info("two");
        var lines = File.ReadAllLines(Log.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(" INFO  two", lines[1]);
    }

    [Fact]
    public void RotatesLargeFile()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Log.OldFilePath, "older");
        File.WriteAllText(Log.FilePath, new string('x', 1024 * 1024 + 1));
        Log.Rotate();
        Assert.False(File.Exists(Log.FilePath));
        Assert.Equal(1024 * 1024 + 1, new FileInfo(Log.OldFilePath).Length);
    }

    [Fact]
    public void KeepsSmallFile()
    {
        Log.Info("small");
        Log.Rotate();
        Assert.True(File.Exists(Log.FilePath));
        Assert.False(File.Exists(Log.OldFilePath));
    }
}
