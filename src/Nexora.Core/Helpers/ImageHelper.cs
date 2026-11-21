using System;
using System.IO;

public static class ImageSizeReader
{
    public static (int Width, int Height)? GetDimensions(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 16) return null;

            byte b0 = reader.ReadByte();
            byte b1 = reader.ReadByte();

            // PNG: \x89PNG
            if (b0 == 0x89 && b1 == 0x50)
            {
                stream.Seek(16, SeekOrigin.Begin);
                int width = ReadBigEndianInt32(reader);
                int height = ReadBigEndianInt32(reader);
                return (width, height);
            }

            // GIF: GIF87a / GIF89a
            if (b0 == 0x47 && b1 == 0x49) // 'G', 'I'
            {
                stream.Seek(6, SeekOrigin.Begin);
                int width = reader.ReadUInt16();
                int height = reader.ReadUInt16();
                return (width, height);
            }

            // BMP: BM
            if (b0 == 0x42 && b1 == 0x4D)
            {
                stream.Seek(18, SeekOrigin.Begin);
                int width = reader.ReadInt32();
                int height = Math.Abs(reader.ReadInt32());
                return (width, height);
            }

            // JPEG: FF D8
            if (b0 == 0xFF && b1 == 0xD8)
            {
                while (stream.Position < stream.Length - 8)
                {
                    byte markerPrefix = reader.ReadByte();
                    if (markerPrefix != 0xFF) continue;

                    byte marker = reader.ReadByte();
                    // Skip fill bytes
                    while (marker == 0xFF) { marker = reader.ReadByte(); }

                    if (marker == 0xD9 || marker == 0xDA) // EOI or SOS
                        break;

                    if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    {
                        // SOF marker
                        reader.ReadUInt16(); // length
                        reader.ReadByte();   // precision
                        int height = ReadBigEndianUInt16(reader);
                        int width = ReadBigEndianUInt16(reader);
                        return (width, height);
                    }
                    else
                    {
                        int len = ReadBigEndianUInt16(reader);
                        if (len < 2) break;
                        stream.Seek(len - 2, SeekOrigin.Current);
                    }
                }
            }

            // WEBP: RIFF....WEBP
            if (b0 == 0x52 && b1 == 0x49) // 'R', 'I'
            {
                stream.Seek(8, SeekOrigin.Begin);
                byte[] webp = reader.ReadBytes(4);
                if (System.Text.Encoding.ASCII.GetString(webp) == "WEBP")
                {
                    byte[] chunkHeader = reader.ReadBytes(4);
                    string chunk = System.Text.Encoding.ASCII.GetString(chunkHeader);
                    if (chunk == "VP8X")
                    {
                        stream.Seek(8, SeekOrigin.Current);
                        byte[] wBytes = reader.ReadBytes(3);
                        byte[] hBytes = reader.ReadBytes(3);
                        int width = 1 + (wBytes[0] | (wBytes[1] << 8) | (wBytes[2] << 16));
                        int height = 1 + (hBytes[0] | (hBytes[1] << 8) | (hBytes[2] << 16));
                        return (width, height);
                    }
                    else if (chunk == "VP8 ")
                    {
                        stream.Seek(10, SeekOrigin.Current);
                        int width = reader.ReadUInt16() & 0x3FFF;
                        int height = reader.ReadUInt16() & 0x3FFF;
                        return (width, height);
                    }
                    else if (chunk == "VP8L")
                    {
                        stream.Seek(5, SeekOrigin.Current);
                        byte[] b = reader.ReadBytes(4);
                        int width = 1 + (((b[1] & 0x3F) << 8) | b[0]);
                        int height = 1 + (((b[3] & 0xF) << 10) | (b[2] << 2) | ((b[1] & 0xC0) >> 6));
                        return (width, height);
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private static int ReadBigEndianInt32(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }

    private static int ReadBigEndianUInt16(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(2);
        return (bytes[0] << 8) | bytes[1];
    }
}
