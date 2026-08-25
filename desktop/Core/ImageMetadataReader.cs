using System.Windows.Media.Imaging;
using System.IO;

namespace AichanToolbox.Core;

internal static class ImageMetadataReader
{
    public static (int Width, int Height) ReadDimensions(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);
            var dimensions = extension switch
            {
                ".png" => ReadPng(reader),
                ".jpg" or ".jpeg" => ReadJpeg(reader),
                ".webp" => ReadWebP(reader),
                _ => (0, 0)
            };
            if (dimensions.Item1 > 0 && dimensions.Item2 > 0) return dimensions;
        }
        catch { }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return frame.PixelWidth > 0 && frame.PixelHeight > 0
                ? (frame.PixelWidth, frame.PixelHeight)
                : (0, 0);
        }
        catch { return (0, 0); }
    }

    public static string FormatName(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "PNG",
            ".jpg" or ".jpeg" => "JPG",
            ".webp" => "WebP",
            ".bmp" => "BMP",
            ".gif" => "GIF",
            ".tif" or ".tiff" => "TIFF",
            var value when value.Length > 1 => value[1..].ToUpperInvariant(),
            _ => "其他"
        };
    }

    public static string RouteFormat(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "png",
            ".jpg" or ".jpeg" => "jpg",
            ".webp" => "webp",
            _ => "other"
        };
    }

    private static (int, int) ReadPng(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 24) return (0, 0);
        reader.BaseStream.Position = 16;
        var width = ReadInt32BigEndian(reader);
        var height = ReadInt32BigEndian(reader);
        return width > 0 && height > 0 ? (width, height) : (0, 0);
    }

    private static (int, int) ReadJpeg(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 4 || reader.ReadByte() != 0xff || reader.ReadByte() != 0xd8)
            return (0, 0);

        var orientation = 1;
        while (reader.BaseStream.Position + 4 <= reader.BaseStream.Length)
        {
            byte markerStart;
            do { markerStart = reader.ReadByte(); }
            while (markerStart != 0xff && reader.BaseStream.Position < reader.BaseStream.Length);

            byte marker;
            do { marker = reader.ReadByte(); } while (marker == 0xff);
            if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) continue;

            var length = ReadUInt16BigEndian(reader);
            if (length < 2 || reader.BaseStream.Position + length - 2 > reader.BaseStream.Length) break;
            if (marker == 0xe1)
            {
                orientation = ReadExifOrientation(reader.ReadBytes(length - 2));
                continue;
            }
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                reader.ReadByte();
                var height = ReadUInt16BigEndian(reader);
                var width = ReadUInt16BigEndian(reader);
                return orientation is >= 5 and <= 8 ? (height, width) : (width, height);
            }
            reader.BaseStream.Position += length - 2;
        }
        return (0, 0);
    }

    private static int ReadExifOrientation(byte[] data)
    {
        if (data.Length < 14
            || data[0] != (byte)'E' || data[1] != (byte)'x' || data[2] != (byte)'i' || data[3] != (byte)'f'
            || data[4] != 0 || data[5] != 0) return 1;

        const int tiff = 6;
        var littleEndian = data[tiff] == (byte)'I' && data[tiff + 1] == (byte)'I';
        var bigEndian = data[tiff] == (byte)'M' && data[tiff + 1] == (byte)'M';
        if (!littleEndian && !bigEndian) return 1;

        ushort ReadUInt16(int offset)
            => littleEndian
                ? (ushort)(data[offset] | data[offset + 1] << 8)
                : (ushort)(data[offset] << 8 | data[offset + 1]);
        uint ReadUInt32(int offset)
            => littleEndian
                ? (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24)
                : (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

        if (ReadUInt16(tiff + 2) != 42) return 1;
        var ifdOffset = ReadUInt32(tiff + 4);
        var directory = checked(tiff + (int)ifdOffset);
        if (directory < tiff || directory + 2 > data.Length) return 1;
        var entries = ReadUInt16(directory);
        for (var index = 0; index < entries; index++)
        {
            var entry = directory + 2 + index * 12;
            if (entry + 12 > data.Length) break;
            if (ReadUInt16(entry) != 0x0112) continue;
            var value = ReadUInt16(entry + 8);
            return value is >= 1 and <= 8 ? value : 1;
        }
        return 1;
    }

    private static (int, int) ReadWebP(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 30) return (0, 0);
        if (new string(reader.ReadChars(4)) != "RIFF") return (0, 0);
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WEBP") return (0, 0);

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunk = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var start = reader.BaseStream.Position;
            if (chunk == "VP8X" && size >= 10)
            {
                var data = reader.ReadBytes(10);
                var width = 1 + data[4] + (data[5] << 8) + (data[6] << 16);
                var height = 1 + data[7] + (data[8] << 8) + (data[9] << 16);
                return (width, height);
            }
            if (chunk == "VP8L" && size >= 5)
            {
                var data = reader.ReadBytes(5);
                if (data[0] != 0x2f) return (0, 0);
                var width = 1 + data[1] + ((data[2] & 0x3f) << 8);
                var height = 1 + ((data[2] & 0xc0) >> 6) + (data[3] << 2) + ((data[4] & 0x0f) << 10);
                return (width, height);
            }
            if (chunk == "VP8 " && size >= 10)
            {
                var data = reader.ReadBytes(10);
                if (data[3] == 0x9d && data[4] == 0x01 && data[5] == 0x2a)
                {
                    var width = (data[6] | data[7] << 8) & 0x3fff;
                    var height = (data[8] | data[9] << 8) & 0x3fff;
                    return (width, height);
                }
            }
            reader.BaseStream.Position = Math.Min(reader.BaseStream.Length, start + size + (size & 1));
        }
        return (0, 0);
    }

    private static int ReadInt32BigEndian(BinaryReader reader)
        => reader.ReadByte() << 24 | reader.ReadByte() << 16 | reader.ReadByte() << 8 | reader.ReadByte();

    private static int ReadUInt16BigEndian(BinaryReader reader)
        => reader.ReadByte() << 8 | reader.ReadByte();
}
