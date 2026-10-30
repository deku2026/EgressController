using Google.Protobuf;
using System.Text;

namespace EgressController.Policy;

/// <summary>Validates the entire supplied DAT and confirms a nonempty tag; never reads files.</summary>
public static class GeoDatInspector
{
    public const int MaximumDatBytes = 64 * 1024 * 1024;
    private const int MaximumEntries = 100_000;
    private const int MaximumItems = 2_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool HasTag(byte[] bytes, string tag, RuleDataKind kind)
    {
        if (bytes is null || bytes.Length is 0 or > MaximumDatBytes || !ValidTag(tag) || kind is not (RuleDataKind.Domain or RuleDataKind.Ip)) return false;
        try
        {
            var input = new CodedInputStream(bytes);
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = false; var entries = 0; var items = 0;
            while (!input.IsAtEnd)
            {
                var tagPosition = input.Position;
                if (++entries > MaximumEntries || input.ReadTag() != 10 || input.Position - tagPosition != 1) return false;
                var rawEntry = input.ReadBytes();
                if (rawEntry.Length < 3 || rawEntry[0] != 10 || rawEntry[1] > 127) return false;
                var entry = rawEntry.CreateCodedInput();
                if (entry.ReadTag() != 10) return false;
                string code = ReadString(entry, 127);
                if (!ValidTag(code) || code != code.ToUpperInvariant()) return false;
                var count = 0; var reverseSeen = false;
                while (!entry.IsAtEnd)
                {
                    switch (entry.ReadTag())
                    {
                        case 18:
                            if (++items > MaximumItems) return false;
                            var value = entry.ReadBytes().CreateCodedInput();
                            if (kind == RuleDataKind.Domain ? !ValidDomain(value) : !ValidCidr(value)) return false;
                            count++;
                            break;
                        case 24 when kind == RuleDataKind.Ip:
                            // The compiler emits positive IP inventories; inverse sets need an explicit policy model.
                            if (reverseSeen || entry.ReadUInt64() != 0) return false;
                            reverseSeen = true;
                            break;
                        default: return false;
                    }
                }
                if (count == 0 || !tags.Add(code)) return false;
                if (string.Equals(code, tag.ToUpperInvariant(), StringComparison.Ordinal)) found = true;
            }
            return found;
        }
        catch (Exception exception) when (exception is InvalidProtocolBufferException or InvalidDataException or ArgumentException or InvalidOperationException or OverflowException or DecoderFallbackException)
        { return false; }
    }

    private static bool ValidDomain(CodedInputStream input)
    {
        int type = 0; var typeSeen = false; string? text = null; var attributes = 0;
        while (!input.IsAtEnd)
        {
            switch (input.ReadTag())
            {
                case 8:
                    if (typeSeen) return false;
                    var rawType = input.ReadUInt64(); typeSeen = true;
                    if (rawType > 3) return false;
                    type = (int)rawType;
                    break;
                case 18:
                    if (text is not null) return false;
                    text = ReadString(input, 4096);
                    if (text.Length is 0 or > 4096) return false;
                    break;
                case 26:
                    if (++attributes > 256 || !ValidAttribute(input.ReadBytes().CreateCodedInput())) return false;
                    break;
                default: return false;
            }
        }
        if (text is null) return false;
        var kind = type switch { 0 => DomainMatchKind.Keyword, 1 => DomainMatchKind.Regex, 2 => DomainMatchKind.Suffix, _ => DomainMatchKind.Full };
        return DomainRuleValidation.IsValid(new(kind, text));
    }

    private static bool ValidAttribute(CodedInputStream input)
    {
        string? key = null; var typedValueSeen = false;
        while (!input.IsAtEnd)
        {
            switch (input.ReadTag())
            {
                case 10:
                    if (key is not null) return false;
                    key = ReadString(input, 128);
                    if (key.Length is 0 or > 128 || key.Any(char.IsControl)) return false;
                    break;
                case 16:
                    if (typedValueSeen || input.ReadUInt64() > 1) return false;
                    typedValueSeen = true;
                    break;
                case 24:
                    if (typedValueSeen) return false;
                    _ = input.ReadInt64(); typedValueSeen = true;
                    break;
                default: return false;
            }
        }
        return key is not null;
    }

    private static bool ValidCidr(CodedInputStream input)
    {
        ByteString? bytes = null; uint prefix = 0; var prefixSeen = false;
        while (!input.IsAtEnd)
        {
            switch (input.ReadTag())
            {
                case 10:
                    if (bytes is not null) return false;
                    bytes = input.ReadBytes(); if (bytes.Length is not (4 or 16)) return false;
                    break;
                case 16:
                    if (prefixSeen) return false;
                    var rawPrefix = input.ReadUInt64();
                    if (rawPrefix > 128) return false;
                    prefix = (uint)rawPrefix; prefixSeen = true;
                    break;
                default: return false;
            }
        }
        if (bytes is null || prefix > bytes.Length * 8) return false;
        // A missing prefix is protobuf's valid zero default, requiring the all-zero network.
        for (var bit = (int)prefix; bit < bytes.Length * 8; bit++)
            if ((bytes[bit / 8] & (1 << (7 - bit % 8))) != 0) return false;
        return true;
    }
    private static string ReadString(CodedInputStream input, int maxCharacters)
    {
        var bytes = input.ReadBytes();
        if (bytes.Length > maxCharacters * 4) throw new InvalidDataException("DAT string exceeds its bounded schema field.");
        return StrictUtf8.GetString(bytes.Span);
    }
    private static bool ValidTag(string? value) => value is { Length: > 0 and <= 127 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
