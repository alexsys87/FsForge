using System.Buffers.Binary;
using FsForge.Core.Compression;

namespace FsForge.Core.Firmware;

/// <summary>
/// The blocks OpenWrt's fwtool appends to a sysupgrade image: metadata (JSON behind an 8-byte header) and a
/// signature, each followed by a 16-byte big-endian trailer: magic "FWx0", a CRC-32, the type, three bytes of padding
/// and the size of the block with its trailer. The CRC of a trailer covers everything in the file in front of it
/// (the image and the blocks before), computed as fwtool does: the reflected CRC-32 started at ~0, not inverted at the end.
/// </summary>
internal sealed class Fwtool
{
    public const uint Magic = 0x46577830;      // "FWx0"
    public const int TrailerSize = 16;
    public const byte TypeSignature = 0, TypeInfo = 1;

    /// <summary>A block: its payload (header and data) and trailer, at <see cref="Start"/> .. <see cref="End"/>.</summary>
    public sealed record Block(long Start, long End, byte Type)
    {
        public long PayloadLength => End - Start - TrailerSize;
    }

    private Fwtool(List<Block> blocks, bool crcValid)
    {
        Blocks = blocks;
        CrcValid = crcValid;
    }

    /// <summary>The blocks in the order of the file.</summary>
    public List<Block> Blocks { get; }

    /// <summary>Where the image in front of the blocks ends.</summary>
    public long ContentEnd => Blocks[0].Start;

    /// <summary>Every trailer's CRC is what fwtool would have written: the layout is known, the CRCs can be made again.</summary>
    public bool CrcValid { get; }

    public bool HasSignature => Blocks.Any(b => b.Type == TypeSignature);

    /// <summary>The fwtool blocks at the end of the file, or null when it does not end with one.</summary>
    public static Fwtool? Read(IBlockSource source, long notBefore, CancellationToken ct)
    {
        var blocks = new List<Block>();
        long end = source.Length;
        byte[] t = new byte[TrailerSize];
        while (blocks.Count < 16 && end - TrailerSize >= notBefore)
        {
            source.ReadExactly(end - TrailerSize, t);
            if (BinaryPrimitives.ReadUInt32BigEndian(t) != Magic) break;
            uint size = BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan(12));
            if (size < TrailerSize || size > end - notBefore) break;
            blocks.Insert(0, new Block(end - size, end, t[8]));
            end -= size;
        }
        if (blocks.Count == 0) return null;

        // The CRC of each trailer: the running CRC of everything in front of it.
        bool valid = true;
        uint crc = ~0u;
        crc = Raw(source, 0, blocks[0].Start, crc, ct);
        foreach (Block b in blocks)
        {
            crc = Raw(source, b.Start, b.End - TrailerSize, crc, ct);
            source.ReadExactly(b.End - TrailerSize, t);
            if (BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan(4)) != crc) valid = false;
            crc = RawUpdate(crc, t);
        }
        return new Fwtool(blocks, valid);
    }

    /// <summary>
    /// Writes the blocks behind new content: payloads and trailers as they were, with the CRCs made again over what was
    /// written in front of them. <paramref name="crc"/> is fwtool's running CRC of that content.
    /// </summary>
    public void Write(Stream output, IBlockSource source, uint crc, CancellationToken ct)
    {
        byte[] t = new byte[TrailerSize];
        byte[] buf = new byte[1 << 16];
        foreach (Block b in Blocks)
        {
            for (long o = b.Start; o < b.End - TrailerSize;)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, b.End - TrailerSize - o);
                source.ReadExactly(o, buf.AsSpan(0, n));
                output.Write(buf, 0, n);
                crc = RawUpdate(crc, buf.AsSpan(0, n));
                o += n;
            }
            source.ReadExactly(b.End - TrailerSize, t);
            BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(4), crc);
            output.Write(t);
            crc = RawUpdate(crc, t);
        }
    }

    /// <summary>The blocks without the CRCs of their trailers: what a rewrite must keep.</summary>
    public IEnumerable<(string Name, IBlockSource Data)> Sections(IBlockSource source)
    {
        int i = 0;
        foreach (Block b in Blocks)
        {
            byte[] data = source.ReadBytes(b.Start, (int)Math.Min(int.MaxValue, b.End - b.Start));
            data.AsSpan(data.Length - TrailerSize + 4, 4).Clear();
            yield return ($"fwtool {(b.Type == TypeSignature ? "signature" : b.Type == TypeInfo ? "metadata" : "block")} {i++}", new MemoryBlockSource(data));
        }
    }

    /// <summary>fwtool's CRC step: the reflected CRC-32 without the final inversion (the core's CRC inverts in and out).</summary>
    public static uint RawUpdate(uint crc, ReadOnlySpan<byte> data) => ~Crc32.Update(~crc, data);

    private static uint Raw(IBlockSource source, long from, long to, uint crc, CancellationToken ct)
    {
        byte[] buf = new byte[1 << 20];
        for (long o = from; o < to;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buf.Length, to - o);
            source.ReadExactly(o, buf.AsSpan(0, n));
            crc = RawUpdate(crc, buf.AsSpan(0, n));
            o += n;
        }
        return crc;
    }
}
