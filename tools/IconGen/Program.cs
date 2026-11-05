// SPDX-FileCopyrightText: 2026 René de Hesselle <dehesselle@web.de>
//
// SPDX-License-Identifier: 0BSD

using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;

namespace Wendel;

/// <summary>
/// Writes the app icon as a multi-size .ico for the exe's Win32 resources. Run by the build.
/// </summary>
internal static class Program
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: IconGen <output.ico>");
            return 1;
        }

        var images = new List<byte[]>();
        foreach (int size in Sizes)
        {
            using var bitmap = Artwork.RenderAppIcon(size);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            images.Add(png.ToArray());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        using var writer = new BinaryWriter(File.Create(args[0]));

        // ICONDIR
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)Sizes.Length);

        // ICONDIRENTRY per image; PNG-encoded entries are supported since Windows Vista.
        int offset = 6 + 16 * Sizes.Length;
        for (int i = 0; i < Sizes.Length; i++)
        {
            byte dimension = Sizes[i] >= 256 ? (byte)0 : (byte)Sizes[i];
            writer.Write(dimension);
            writer.Write(dimension);
            writer.Write((byte)0); // no palette
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // color planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (byte[] image in images)
            writer.Write(image);
        return 0;
    }
}
