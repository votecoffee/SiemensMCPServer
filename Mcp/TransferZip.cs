using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SiemensMCPServer.Mcp
{
    // Bounded ZIP extraction with explicit central/local headers and CRC checks.
    // ZIP64, encrypted, multi-disk and non-stored/deflate archives are unsupported.
    internal static class TransferZip
    {
        private sealed class Member { internal string Name = ""; internal uint Crc, Size, Compressed, Offset; internal ushort Flags, Method; internal byte[] RawName = Array.Empty<byte>(); }
        internal static void Extract(string archive, string destination)
        {
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Extraction destination must be new.");
            using (var stream = File.OpenRead(archive))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (stream.Length < 22 || stream.Length > 600L * 1024 * 1024) throw new InvalidDataException("ZIP size is outside the supported bound.");
                stream.Position = Math.Max(0, stream.Length - 65557); long tailOffset = stream.Position;
                byte[] tail = reader.ReadBytes((int)(stream.Length - stream.Position)); int end = -1;
                for (int i = tail.Length - 22; i >= 0; i--)
                    if (BitConverter.ToUInt32(tail, i) == 0x06054b50 && i + 22 + BitConverter.ToUInt16(tail, i + 20) == tail.Length) { end = i; break; }
                if (end < 0) throw new InvalidDataException("ZIP end record is missing or truncated.");
                ushort count = BitConverter.ToUInt16(tail, end + 10);
                uint centralSize = BitConverter.ToUInt32(tail, end + 12), centralOffset = BitConverter.ToUInt32(tail, end + 16);
                if (BitConverter.ToUInt16(tail, end + 4) != 0 || BitConverter.ToUInt16(tail, end + 6) != 0
                    || BitConverter.ToUInt16(tail, end + 8) != count || count == ushort.MaxValue || centralOffset == uint.MaxValue || centralSize == uint.MaxValue)
                    throw new NotSupportedException("ZIP64 and multi-disk archives are unsupported.");
                if (count == 0 || count > 10000 || (long)centralOffset + centralSize != tailOffset + end) throw new InvalidDataException("ZIP directory bounds/count are inconsistent.");
                stream.Position = centralOffset; var members = new List<Member>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                for (int i = 0; i < count; i++)
                {
                    if (reader.ReadUInt32() != 0x02014b50) throw new InvalidDataException("Invalid ZIP central record.");
                    reader.ReadUInt16(); reader.ReadUInt16();
                    var m = new Member { Flags = reader.ReadUInt16(), Method = reader.ReadUInt16() };
                    reader.ReadUInt32(); m.Crc = reader.ReadUInt32(); m.Compressed = reader.ReadUInt32(); m.Size = reader.ReadUInt32();
                    ushort nameSize = reader.ReadUInt16(), extra = reader.ReadUInt16(), comment = reader.ReadUInt16(), disk = reader.ReadUInt16();
                    reader.ReadUInt16(); uint attributes = reader.ReadUInt32(); m.Offset = reader.ReadUInt32(); m.RawName = reader.ReadBytes(nameSize);
                    if (m.RawName.Length != nameSize || nameSize == 0 || disk != 0 || (m.Flags & 1) != 0 || (m.Method != 0 && m.Method != 8)
                        || m.Size == uint.MaxValue || m.Compressed == uint.MaxValue || (attributes >> 16 & 0xf000) == 0xa000)
                        throw new NotSupportedException("Unsupported ZIP encryption, compression, link or ZIP64 entry.");
                    if ((m.Flags & 0x800) != 0) m.Name = new UTF8Encoding(false, true).GetString(m.RawName);
                    else { if (m.RawName.Any(b => b > 127)) throw new NotSupportedException("Non-ASCII ZIP names require the UTF-8 flag."); m.Name = Encoding.ASCII.GetString(m.RawName); }
                    string relative = m.Name.EndsWith("/", StringComparison.Ordinal) ? m.Name.Substring(0, m.Name.Length - 1) : m.Name;
                    TransferBundle.SafePath(destination, relative);
                    if (relative.Split('/').Length > 32 || !names.Add(relative) || m.Size > NativeXmlValidation.MaximumBytes || (total += m.Size) > 512L * 1024 * 1024)
                        throw new InvalidDataException("ZIP has duplicate names or exceeds transfer bounds.");
                    members.Add(m); stream.Position += extra + comment;
                    if (stream.Position > (long)centralOffset + centralSize) throw new InvalidDataException("Truncated ZIP directory.");
                }
                if (stream.Position != (long)centralOffset + centralSize) throw new InvalidDataException("ZIP directory member count mismatch.");
                foreach (var m in members)
                {
                    stream.Position = m.Offset;
                    if (reader.ReadUInt32() != 0x04034b50) throw new InvalidDataException("Missing local ZIP header.");
                    reader.ReadUInt16(); ushort flags = reader.ReadUInt16(), method = reader.ReadUInt16(); reader.ReadUInt32();
                    uint crc = reader.ReadUInt32(), compressed = reader.ReadUInt32(), size = reader.ReadUInt32();
                    ushort nameSize = reader.ReadUInt16(), extra = reader.ReadUInt16(); byte[] name = reader.ReadBytes(nameSize);
                    if (!name.SequenceEqual(m.RawName) || flags != m.Flags || method != m.Method || ((flags & 8) == 0 && (crc != m.Crc || compressed != m.Compressed || size != m.Size))
                        || stream.Position + extra + m.Compressed > centralOffset) throw new InvalidDataException("ZIP local/central member metadata mismatch.");
                }
                stream.Position = 0;
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
                {
                    if (zip.Entries.Count != members.Count) throw new InvalidDataException("ZIP reader member count mismatch.");
                    Directory.CreateDirectory(destination);
                    for (int i = 0; i < members.Count; i++)
                    {
                        var m = members[i]; var entry = zip.Entries[i];
                        if (entry.Length != m.Size || entry.CompressedLength != m.Compressed || entry.FullName != m.Name) throw new InvalidDataException("ZIP member metadata mismatch.");
                        if (m.Name.EndsWith("/", StringComparison.Ordinal)) { if (m.Size != 0 || m.Crc != 0) throw new InvalidDataException("ZIP directory contains data or an invalid empty CRC."); continue; }
                        string target = TransferBundle.SafePath(destination, m.Name); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using (var input = entry.Open())
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                        {
                            var buffer = new byte[81920]; long bytes = 0; uint crc = uint.MaxValue; int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                bytes += read; if (bytes > m.Size) throw new InvalidDataException("ZIP member exceeds declared length.");
                                for (int n = 0; n < read; n++) crc = CrcTable[(crc ^ buffer[n]) & 255] ^ (crc >> 8);
                                output.Write(buffer, 0, read);
                            }
                            if (bytes != m.Size || (crc ^ uint.MaxValue) != m.Crc) throw new InvalidDataException("ZIP member CRC or length mismatch: " + m.Name);
                        }
                    }
                }
            }
        }
        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i => { uint c = (uint)i; for (int n = 0; n < 8; n++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; return c; }).ToArray();
    }
}
