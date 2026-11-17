using System.Buffers.Binary;
using System.Text;

namespace FsForge.Core.Firmware;

/// <summary>A property of a flattened device tree; the value of a large one (embedded data) is not read.</summary>
internal sealed class FdtProperty
{
    public string Name { get; init; } = "";

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

    private Fdt(FdtNode root, uint totalSize, uint version)
    {
        Root = root;
        TotalSize = totalSize;
        Version = version;
    }

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
                    stack.Peek().Properties[name] = new FdtProperty
                    {
                        Name = name, Offset = pos, Length = (int)length,
                        Value = length <= SmallValue ? source.ReadBytes(pos, (int)length) : null,
                    };
                    pos += (length + 3) & ~3L;
                    break;
                }
                case TokenNop:
                    break;
                case TokenEnd:
                    if (root is null || stack.Count != 0) throw new FsException("Corrupt device tree: the structure block ends inside a node.");
                    return new Fdt(root, total, version);
                default:
                    throw new FsException($"Corrupt device tree: unknown token 0x{token:X}.");
            }
        }
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
