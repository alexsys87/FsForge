using System.Buffers.Binary;
using System.Text;

namespace FsForge.Core.Firmware;

/// <summary>A property of a flattened device tree; the value of a large one (embedded data) is not read.</summary>
internal sealed class FdtProperty
{
    public string Name { get; init; } = "";

    /// <summary>Where the name is in the strings block.</summary>
    public uint NameOffset { get; init; }

    /// <summary>Position of the value in the source.</summary>
    public long Offset { get; init; }

    public int Length { get; init; }

    /// <summary>The value, when it is small enough to be read along with the tree.</summary>
    public byte[]? Value { get; init; }

    /// <summary>The value as a string (the first one of a string list).</summary>
    public string? Text
    {
        get
        {
            if (Value is null) return null;
            int n = Array.IndexOf(Value, (byte)0);
            return Encoding.UTF8.GetString(Value, 0, n < 0 ? Value.Length : n);
        }
    }

    /// <summary>A 32- or 64-bit cell value (big-endian), or null.</summary>
    public ulong? Number => Value?.Length switch
    {
        4 => BinaryPrimitives.ReadUInt32BigEndian(Value),
        8 => BinaryPrimitives.ReadUInt64BigEndian(Value),
        _ => null,
    };
}

internal sealed class FdtNode
{
    public string Name { get; init; } = "";
    public Dictionary<string, FdtProperty> Properties { get; } = new(StringComparer.Ordinal);

    /// <summary>The properties in the order of the tree.</summary>
    public List<FdtProperty> PropertyList { get; } = new();

    public List<FdtNode> Children { get; } = new();

    public FdtNode? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

    public FdtProperty? this[string property] => Properties.GetValueOrDefault(property);
}

/// <summary>
/// A flattened device tree (the "DTB" format, devicetree specification chapter 5): the header, the structure block
/// of nodes and properties, and the strings block of property names. Used for FIT images, whose embedded data can
/// be large: such values are only located, not read.
/// </summary>
internal sealed class Fdt
{
    public const uint Magic = 0xD00DFEED;
    public const int HeaderSize = 40;

    private const uint TokenBeginNode = 1, TokenEndNode = 2, TokenProp = 3, TokenNop = 4, TokenEnd = 9;

    /// <summary>Values up to this size are read with the tree.</summary>
    private const int SmallValue = 64 << 10;

    private Fdt(IBlockSource source, FdtNode root, uint totalSize, uint version)
    {
        Source = source;
        Root = root;
        TotalSize = totalSize;
        Version = version;
    }

    private IBlockSource Source { get; }

    private byte[] _strings = Array.Empty<byte>();
    private byte[] _reserveMap = new byte[16];
    private uint _bootCpu, _lastCompatible = 16;

    public FdtNode Root { get; }

    /// <summary>Size of the tree blob as its header gives it.</summary>
    public uint TotalSize { get; }

    public uint Version { get; }

    /// <summary>True when a valid tree header starts at <paramref name="at"/>.</summary>
    public static bool HasHeader(IBlockSource source, long at = 0)
    {
        if (source.Length - at < HeaderSize) return false;
        Span<byte> h = stackalloc byte[HeaderSize];
        if (source.Read(at, h) < HeaderSize) return false;
        uint total = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
        uint version = BinaryPrimitives.ReadUInt32BigEndian(h[20..]), last = BinaryPrimitives.ReadUInt32BigEndian(h[24..]);
        return BinaryPrimitives.ReadUInt32BigEndian(h) == Magic && total >= HeaderSize && total <= source.Length - at
               && version >= 16 && last <= 17;
    }

    /// <summary>Reads the tree that starts at <paramref name="at"/>.</summary>
    public static Fdt Read(IBlockSource source, long at = 0)
    {
        if (!HasHeader(source, at)) throw new FsException("Not a flattened device tree.");
        byte[] h = source.ReadBytes(at, HeaderSize);
        uint total = U32(h, 4), offStruct = U32(h, 8), offStrings = U32(h, 12), version = U32(h, 20), sizeStrings = U32(h, 32);
        uint sizeStruct = version >= 17 ? U32(h, 36) : total - Math.Min(total, offStruct);
        if (offStruct >= total || (ulong)offStruct + sizeStruct > total || offStrings > total || (ulong)offStrings + sizeStrings > total || sizeStrings > 16 << 20)
            throw new FsException("Corrupt device tree: its blocks lie outside it.");
        byte[] strings = source.ReadBytes(at + offStrings, (int)sizeStrings);

        long pos = at + offStruct, end = pos + sizeStruct;
        var stack = new Stack<FdtNode>();
        FdtNode? root = null;
        int nodes = 0;
        while (true)
        {
            if (pos + 4 > end) throw new FsException("Corrupt device tree: the structure block has no end.");
            uint token = BinaryPrimitives.ReadUInt32BigEndian(source.ReadBytes(pos, 4));
            pos += 4;
            switch (token)
            {
                case TokenBeginNode:
                {
                    string name = ReadName(source, pos, end, out int consumed);
                    pos += (consumed + 3) & ~3;
                    if (++nodes > 100000 || stack.Count > 64) throw new FsException("Corrupt device tree: too many or too deeply nested nodes.");
                    var node = new FdtNode { Name = name };
                    if (stack.Count == 0)
                    {
                        if (root is not null) throw new FsException("Corrupt device tree: it has more than one root node.");
                        root = node;
                    }
                    else stack.Peek().Children.Add(node);
                    stack.Push(node);
                    break;
                }
                case TokenEndNode:
                    if (stack.Count == 0) throw new FsException("Corrupt device tree: a node ends that never began.");
                    stack.Pop();
                    break;
                case TokenProp:
                {
                    if (stack.Count == 0 || pos + 8 > end) throw new FsException("Corrupt device tree: a property lies outside any node.");
                    byte[] p = source.ReadBytes(pos, 8);
                    uint length = U32(p, 0), nameOff = U32(p, 4);
                    pos += 8;
                    if (pos + length > end || nameOff >= strings.Length) throw new FsException("Corrupt device tree: a property lies outside the structure block.");
                    int nameEnd = Array.IndexOf(strings, (byte)0, (int)nameOff);
                    string name = Encoding.UTF8.GetString(strings, (int)nameOff, (nameEnd < 0 ? strings.Length : nameEnd) - (int)nameOff);
                    var property = new FdtProperty
                    {
                        Name = name, NameOffset = nameOff, Offset = pos, Length = (int)length,
                        Value = length <= SmallValue ? source.ReadBytes(pos, (int)length) : null,
                    };
                    stack.Peek().Properties[name] = property;
                    stack.Peek().PropertyList.Add(property);
                    pos += (length + 3) & ~3L;
                    break;
                }
                case TokenNop:
                    break;
                case TokenEnd:
                    if (root is null || stack.Count != 0) throw new FsException("Corrupt device tree: the structure block ends inside a node.");
                    return new Fdt(source, root, total, version)
                    {
                        _strings = strings, _reserveMap = ReadReserveMap(source, at, U32(h, 16), total), _bootCpu = U32(h, 28), _lastCompatible = U32(h, 24),
                    };
                default:
                    throw new FsException($"Corrupt device tree: unknown token 0x{token:X}.");
            }
        }
    }

    /// <summary>The memory reservation block up to and with its terminating empty entry.</summary>
    private static byte[] ReadReserveMap(IBlockSource source, long at, uint offset, uint total)
    {
        var map = new List<byte>();
        for (long pos = at + offset; pos + 16 <= at + total && map.Count < 16 * 1024; pos += 16)
        {
            byte[] entry = source.ReadBytes(pos, 16);
            map.AddRange(entry);
            if (entry.AsSpan().IndexOfAnyExcept((byte)0) < 0) return map.ToArray();
        }
        throw new FsException("Corrupt device tree: the memory reservation block has no end.");
    }

    /// <summary>
    /// The tree written again as dtc lays it out (header, memory reservation block, structure, strings), with the
    /// values <paramref name="replace"/> gives in place of the old ones; everything else as it was. Large values are
    /// taken from the source as they are written.
    /// </summary>
    public List<IBlockSource> Write(Func<FdtProperty, IBlockSource?> replace)
    {
        var structure = new List<IBlockSource>();
        var small = new MemoryStream();
        void Flush()
        {
            if (small.Length == 0) return;
            structure.Add(new MemoryBlockSource(small.ToArray()));
            small.SetLength(0);
        }
        void Word(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); small.Write(b); }
        void Pad(long length) { for (long i = length; i % 4 != 0; i++) small.WriteByte(0); }
        void Node(FdtNode n)
        {
            Word(TokenBeginNode);
            byte[] name = Encoding.UTF8.GetBytes(n.Name + "\0");
            small.Write(name);
            Pad(name.Length);
            foreach (FdtProperty p in n.PropertyList)
            {
                IBlockSource value = replace(p) ?? (p.Value is not null ? new MemoryBlockSource(p.Value) : new SubBlockSource(Source, p.Offset, p.Length, ownsInner: false));
                if (value.Length > uint.MaxValue) throw new FsException($"The value of '{p.Name}' is too large for a device tree.");
                Word(TokenProp);
                Word((uint)value.Length);
                Word(p.NameOffset);
                if (value.Length <= SmallValue) small.Write(value.ReadBytes(0, (int)value.Length));
                else
                {
                    Flush();
                    structure.Add(value);
                }
                Pad(value.Length);
            }
            foreach (FdtNode c in n.Children) Node(c);
            Word(TokenEndNode);
        }
        Node(Root);
        Word(TokenEnd);
        Flush();

        long structSize = structure.Sum(p => p.Length);
        long offStruct = HeaderSize + _reserveMap.Length, offStrings = offStruct + structSize, total = offStrings + _strings.Length;
        if (total > uint.MaxValue) throw new FsException("The device tree would be larger than 4 GiB.");
        byte[] h = new byte[HeaderSize];
        uint[] fields = { Magic, (uint)total, (uint)offStruct, (uint)offStrings, HeaderSize, 17, Math.Min(_lastCompatible, 17u), _bootCpu, (uint)_strings.Length, (uint)structSize };
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4 * i), fields[i]);
        var pieces = new List<IBlockSource> { new MemoryBlockSource(h), new MemoryBlockSource(_reserveMap) };
        pieces.AddRange(structure);
        pieces.Add(new MemoryBlockSource(_strings));
        return pieces;
    }

    private static string ReadName(IBlockSource source, long pos, long end, out int consumed)
    {
        byte[] buf = source.ReadBytes(pos, (int)Math.Min(256, end - pos));
        int n = Array.IndexOf(buf, (byte)0);
        if (n < 0) throw new FsException("Corrupt device tree: a node name has no end.");
        consumed = n + 1;
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
}
