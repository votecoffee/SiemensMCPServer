using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SiemensMCPServer.Mcp
{
    // Verify the entire archive without recreating its filesystem hierarchy.
    // Callers read selected logical members; ZIP64/encryption remain unsupported.
    internal static class TransferZip
    {
        private sealed class Member { internal string Name = ""; internal uint Crc, Size, Compressed, Offset; internal ushort Flags, Method; internal byte[] RawName = Array.Empty<byte>(); }
        internal sealed class Reader : IDisposable
        {
            private readonly FileStream stream;
            private readonly ZipArchive zip;
            private readonly Member[] members;
            private readonly Dictionary<string, int> byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal TransferFile[] Inventory { get; }
            internal int MemberCount => members.Length;
            internal Reader(string archive)
            {
                stream = File.OpenRead(archive);
                try
                {
                    members = Parse(stream);
                    stream.Position = 0; zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
                    if (zip.Entries.Count != members.Length) throw new InvalidDataException("ZIP reader member count mismatch.");
                    var files = new List<TransferFile>();
                    for (int i = 0; i < members.Length; i++)
                    {
                        var member = members[i]; var entry = zip.Entries[i];
                        if (entry.Length != member.Size || entry.CompressedLength != member.Compressed || entry.FullName != member.Name) throw new InvalidDataException("ZIP member metadata mismatch.");
                        if (member.Name.EndsWith("/", StringComparison.Ordinal))
                        {
                            if (member.Size != 0 || member.Crc != 0) throw new InvalidDataException("ZIP directory contains data or an invalid empty CRC.");
                            continue;
                        }
                        byName.Add(member.Name, i);
                        string hash = Verify(i);
                        hashes.Add(member.Name, hash);
                        files.Add(new TransferFile { Path = member.Name, Bytes = member.Size, Sha256 = hash });
                    }
                    if (files.Count == 0) throw new InvalidDataException("ZIP contains no files.");
                    Inventory = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
                }
                catch { zip?.Dispose(); stream.Dispose(); throw; }
            }
            private string Verify(int index, Stream? destination = null)
            {
                var member = members[index];
                using (var input = zip.Entries[index].Open())
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[81920]; long bytes = 0; uint crc = uint.MaxValue; int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        bytes += read;
                        if (bytes > member.Size) throw new InvalidDataException("ZIP member exceeds declared length.");
                        for (int n = 0; n < read; n++) crc = CrcTable[(crc ^ buffer[n]) & 255] ^ (crc >> 8);
                        sha.TransformBlock(buffer, 0, read, buffer, 0);
                        destination?.Write(buffer, 0, read);
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    if (bytes != member.Size || (crc ^ uint.MaxValue) != member.Crc) throw new InvalidDataException("ZIP member CRC or length mismatch: " + member.Name);
                    return BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
                }
            }
            internal byte[] Read(string relative)
            {
                TransferBundle.ValidateRelativePath(relative);
                if (!byName.TryGetValue(relative, out int index)) throw new FileNotFoundException("Required archive member is missing: " + relative);
                using (var output = new MemoryStream((int)members[index].Size))
                {
                    if (Verify(index, output) != hashes[relative]) throw new InvalidDataException("Archive member changed after source inventory: " + relative);
                    return output.ToArray();
                }
            }
            public void Dispose() { zip.Dispose(); stream.Dispose(); }
        }
        internal static Reader Open(string archive) => new Reader(archive);
        internal static void Extract(string archive, string destination)
        {
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Extraction destination must be new.");
            using (var source = Open(archive))
            {
                Directory.CreateDirectory(destination);
                foreach (var file in source.Inventory)
                {
                    string target = TransferBundle.SafePath(destination, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    {
                        byte[] bytes = source.Read(file.Path); output.Write(bytes, 0, bytes.Length);
                    }
                }
            }
        }
        private static Member[] Parse(FileStream stream)
        {
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
                    TransferBundle.ValidateRelativePath(relative);
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
                return members.ToArray();
            }
        }
        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i => { uint c = (uint)i; for (int n = 0; n < 8; n++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; return c; }).ToArray();
    }
}
