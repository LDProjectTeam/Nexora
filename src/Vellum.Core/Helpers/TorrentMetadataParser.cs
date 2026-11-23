using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Vellum.Core.Helpers
{
    /// <summary>
    /// Lightweight BEncode parser to read basic metadata (.torrent name and total size)
    /// without relying on heavy external dependencies.
    /// </summary>
    public static class TorrentMetadataParser
    {
        public sealed class Metadata
        {
            public string Name { get; set; } = string.Empty;
            public long TotalSize { get; set; }
        }

        public static Metadata Parse(byte[] bytes)
        {
            var meta = new Metadata();
            if (bytes == null || bytes.Length == 0)
                return meta;

            try
            {
                using var ms = new MemoryStream(bytes);
                var root = Decode(ms) as Dictionary<string, object>;
                if (root != null && root.TryGetValue("info", out var infoObj) && infoObj is Dictionary<string, object> info)
                {
                    if (info.TryGetValue("name", out var nameObj) && nameObj is byte[] nameBytes)
                    {
                        meta.Name = Encoding.UTF8.GetString(nameBytes);
                    }
                    else if (info.TryGetValue("name.utf-8", out var nameUtf8Obj) && nameUtf8Obj is byte[] nameUtf8Bytes)
                    {
                        meta.Name = Encoding.UTF8.GetString(nameUtf8Bytes);
                    }

                    // Single file
                    if (info.TryGetValue("length", out var lengthObj) && lengthObj is long length)
                    {
                        meta.TotalSize = length;
                    }
                    // Multi file
                    else if (info.TryGetValue("files", out var filesObj) && filesObj is List<object> files)
                    {
                        long sum = 0;
                        foreach (var f in files)
                        {
                            if (f is Dictionary<string, object> fileDict &&
                                fileDict.TryGetValue("length", out var fileLen) &&
                                fileLen is long fLength)
                            {
                                sum += fLength;
                            }
                        }
                        meta.TotalSize = sum;
                    }
                }
            }
            catch
            {
                // Return whatever was successfully parsed
            }

            return meta;
        }

        private static object? Decode(Stream stream)
        {
            int b = stream.ReadByte();
            if (b == -1) return null;

            char ch = (char)b;
            if (ch == 'i')
            {
                // Integer: i<number>e
                var sb = new StringBuilder();
                while (true)
                {
                    int next = stream.ReadByte();
                    if (next == -1 || next == 'e') break;
                    sb.Append((char)next);
                }
                return long.TryParse(sb.ToString(), out var num) ? num : 0L;
            }
            if (ch == 'l')
            {
                // List: l<items>e
                var list = new List<object>();
                while (true)
                {
                    int peek = stream.ReadByte();
                    if (peek == -1 || peek == 'e') break;
                    stream.Position--;
                    var item = Decode(stream);
                    if (item != null) list.Add(item);
                }
                return list;
            }
            if (ch == 'd')
            {
                // Dictionary: d<key><value>e
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                while (true)
                {
                    int peek = stream.ReadByte();
                    if (peek == -1 || peek == 'e') break;
                    stream.Position--;
                    var keyObj = Decode(stream);
                    if (keyObj is not byte[] keyBytes) break;
                    var key = Encoding.UTF8.GetString(keyBytes);
                    var val = Decode(stream);
                    if (val != null) dict[key] = val;
                }
                return dict;
            }
            if (char.IsAsciiDigit(ch))
            {
                // String: <length>:<bytes>
                var lenSb = new StringBuilder();
                lenSb.Append(ch);
                while (true)
                {
                    int next = stream.ReadByte();
                    if (next == -1 || next == ':') break;
                    lenSb.Append((char)next);
                }
                if (int.TryParse(lenSb.ToString(), out var strLen) && strLen >= 0)
                {
                    var buffer = new byte[strLen];
                    int read = 0;
                    while (read < strLen)
                    {
                        int r = stream.Read(buffer, read, strLen - read);
                        if (r <= 0) break;
                        read += r;
                    }
                    return buffer;
                }
            }

            return null;
        }
    }
}
