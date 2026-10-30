// SPDX-License-Identifier: MIT
using System.Buffers.Binary;

namespace EgressController.Mac;

/// <summary>Pure bounded format inspection, not a code-signature or kernel-loadability verdict.</summary>
public static class MachOInspector
{
    public const int MaximumArchitectures = 64;
    private const uint ExecuteFileType = 2;
    private const uint MaximumLoadCommands = 8192;
    private const uint MaximumCommandBytes = 16 * 1024 * 1024;

    /// <summary>Reads a complete seekable stream, restoring its original position. Every FAT slice must be MH_EXECUTE.</summary>
    public static bool IsExecutable(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek) return false;
        long original;
        try { original = stream.Position; }
        catch (Exception error) when (error is IOException or NotSupportedException or ObjectDisposedException) { return false; }
        try
        {
            long length = stream.Length;
            Span<byte> header = stackalloc byte[8];
            if (length < 4 || !ReadAt(stream, 0, header[..4], length)) return false;
            uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (magic is 0xfeedface or 0xcefaedfe or 0xfeedfacf or 0xcffaedfe)
                return ThinExecutable(stream, 0, length, null);
            bool fat64 = magic is 0xcafebabf or 0xbfbafeca;
            bool bigEndian = magic is 0xcafebabe or 0xcafebabf;
            if (magic is not (0xcafebabe or 0xbebafeca or 0xcafebabf or 0xbfbafeca)
                || !ReadAt(stream, 0, header, length)) return false;
            uint count = U32(header[4..], bigEndian);
            if (count is 0 or > MaximumArchitectures) return false;
            int architectureSize = fat64 ? 32 : 20;
            long tableEnd = 8 + (long)count * architectureSize;
            if (tableEnd > length) return false;
            var slices = new List<Slice>((int)count);
            var identities = new HashSet<(uint Cpu, uint Subtype)>();
            Span<byte> architecture = stackalloc byte[32];
            for (int index = 0; index < count; index++)
            {
                if (!ReadAt(stream, 8 + (long)index * architectureSize, architecture[..architectureSize], length)) return false;
                uint cpu = U32(architecture, bigEndian), subtype = U32(architecture[4..], bigEndian);
                ulong offset = fat64 ? U64(architecture[8..], bigEndian) : U32(architecture[8..], bigEndian);
                ulong size = fat64 ? U64(architecture[16..], bigEndian) : U32(architecture[12..], bigEndian);
                uint alignment = U32(architecture[(fat64 ? 24 : 16)..], bigEndian);
                if (offset < (ulong)tableEnd || offset > (ulong)length || size < 28 || size > (ulong)length - offset
                    || alignment > (fat64 ? 63u : 31u) || (offset & ((1UL << (int)alignment) - 1)) != 0
                    || fat64 && U32(architecture[28..], bigEndian) != 0 || !identities.Add((cpu, subtype))) return false;
                slices.Add(new((long)offset, (long)size, cpu, subtype));
            }
            long previousEnd = tableEnd;
            foreach (var slice in slices.OrderBy(slice => slice.Offset))
            {
                if (slice.Offset < previousEnd || !ThinExecutable(stream, slice.Offset, slice.Size, slice)) return false;
                previousEnd = slice.Offset + slice.Size; // Both operands were bounded by the stream length above.
            }
            return true;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ObjectDisposedException or ArgumentException or OverflowException)
        { return false; }
        finally
        {
            try { stream.Position = original; }
            catch (Exception error) when (error is IOException or NotSupportedException or ObjectDisposedException) { }
        }
    }

    private static bool ThinExecutable(Stream stream, long offset, long size, Slice? expected)
    {
        Span<byte> header = stackalloc byte[32];
        if (size < 28 || !ReadAt(stream, offset, header[..4], stream.Length)) return false;
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        bool is64 = magic is 0xfeedfacf or 0xcffaedfe;
        bool bigEndian = magic is 0xfeedface or 0xfeedfacf;
        if (magic is not (0xfeedface or 0xcefaedfe or 0xfeedfacf or 0xcffaedfe)) return false;
        int headerSize = is64 ? 32 : 28;
        if (size < headerSize || !ReadAt(stream, offset, header[..headerSize], stream.Length)) return false;
        uint cpu = U32(header[4..], bigEndian), subtype = U32(header[8..], bigEndian);
        if (cpu is 0 or uint.MaxValue || U32(header[12..], bigEndian) != ExecuteFileType
            || expected is { } architecture && (cpu != architecture.Cpu || subtype != architecture.Subtype)
            || is64 && U32(header[28..], bigEndian) != 0) return false;
        uint commandCount = U32(header[16..], bigEndian), commandBytes = U32(header[20..], bigEndian);
        if (commandCount > MaximumLoadCommands || commandBytes > MaximumCommandBytes
            || commandBytes > size - headerSize || (ulong)commandCount * 8 > commandBytes) return false;
        long commandPosition = offset + headerSize;
        long commandEnd = commandPosition + commandBytes;
        Span<byte> command = stackalloc byte[8];
        for (uint index = 0; index < commandCount; index++)
        {
            if (commandEnd - commandPosition < 8 || !ReadAt(stream, commandPosition, command, stream.Length)) return false;
            uint commandSize = U32(command[4..], bigEndian);
            if (commandSize < 8 || commandSize % (is64 ? 8u : 4u) != 0 || commandSize > commandEnd - commandPosition) return false;
            commandPosition += commandSize;
        }
        return commandPosition == commandEnd;
    }
    private static bool ReadAt(Stream stream, long offset, Span<byte> destination, long length)
    {
        if (offset < 0 || offset > length - destination.Length) return false;
        stream.Position = offset; stream.ReadExactly(destination); return true;
    }
    private static uint U32(ReadOnlySpan<byte> bytes, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    private static ulong U64(ReadOnlySpan<byte> bytes, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    private readonly record struct Slice(long Offset, long Size, uint Cpu, uint Subtype);
}
