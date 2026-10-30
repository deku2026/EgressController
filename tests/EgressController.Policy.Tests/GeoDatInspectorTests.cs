using Google.Protobuf;
using System.Text;

namespace EgressController.Policy.Tests;

public sealed class GeoDatInspectorTests
{
    [Fact]
    public void AcceptsCompilerOutputForBothKindsAndRequiresTheExactNonemptyTag()
    {
        var rules = GeoRuleCompiler.Parse("payload: ['DOMAIN,exact.example.test', 'DOMAIN-SUFFIX,example.test', 'DOMAIN-KEYWORD,service', 'DOMAIN-REGEX,^a[.]example[.]test$', 'IP-CIDR,203.0.113.0/24', 'IP-CIDR6,2001:db8::/32']", "service", RuleDataKind.Classical);
        Assert.True(rules.IsSuccess); var dat = GeoRuleCompiler.Compile([rules]);
        Assert.True(GeoDatInspector.HasTag(dat.GeoSite, "SERVICE", RuleDataKind.Domain));
        Assert.True(GeoDatInspector.HasTag(dat.GeoIp, "service", RuleDataKind.Ip));
        Assert.False(GeoDatInspector.HasTag(dat.GeoSite, "absent", RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag(dat.GeoSite, "service", RuleDataKind.Ip));
        Assert.False(GeoDatInspector.HasTag(dat.GeoIp, "service", RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag(dat.GeoSite, "service", RuleDataKind.Classical));
        Assert.False(GeoDatInspector.HasTag([], "service", RuleDataKind.Domain));
    }

    [Fact]
    public void EveryTruncatedPrefixAndCorruptTrailingEntryIsRejected()
    {
        var valid = DomainDat(Domain(2, "example.test"));
        for (var length = 0; length < valid.Length; length++) Assert.False(GeoDatInspector.HasTag(valid[..length], "service", RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag([.. valid, 10, 5, 1], "service", RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag([.. valid, 24, 1], "service", RuleDataKind.Domain));
    }

    [Fact]
    public void RejectsEmptyDuplicateAndUnknownSchemaFieldsAcrossEntireFile()
    {
        Assert.False(GeoDatInspector.HasTag(DomainDat([]), "service", RuleDataKind.Domain));
        var duplicateCode = Encode(w => { Text(w, 1, "SERVICE"); Text(w, 1, "SERVICE"); Message(w, 2, Domain(2, "example.test")); });
        Assert.False(GeoDatInspector.HasTag(Root(duplicateCode), "service", RuleDataKind.Domain));
        var repeatedTags = Encode(w => { Message(w, 1, Entry("service", Domain(2, "example.test"))); Message(w, 1, Entry("SERVICE", Domain(2, "example.test"))); });
        Assert.False(GeoDatInspector.HasTag(repeatedTags, "service", RuleDataKind.Domain));
        var invalidOtherTag = Encode(w => { Message(w, 1, Entry("service", Domain(2, "example.test"))); Message(w, 1, Entry("other", Domain(4, "example.test"))); });
        Assert.False(GeoDatInspector.HasTag(invalidOtherTag, "service", RuleDataKind.Domain));
        var unknownDomainField = Encode(w => { Text(w, 2, "example.test"); w.WriteTag(4, WireFormat.WireType.Varint); w.WriteUInt32(1); });
        Assert.False(GeoDatInspector.HasTag(DomainDat(unknownDomainField), "service", RuleDataKind.Domain));
    }

    [Fact]
    public void ValidatesDomainEnumValueUtf8AndProtobufDefaultKeyword()
    {
        Assert.True(GeoDatInspector.HasTag(DomainDat(Encode(w => Text(w, 2, "keyword"))), "service", RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag(DomainDat(Domain(4, "example.test")), "service", RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag(DomainDat(Domain(2, "https://example.test")), "service", RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag(DomainDat(Domain(3, "bad/path")), "service", RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag(DomainDat(Domain(0, "")), "service", RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag(DomainDat(Domain(1, "(?<=unsupported)lookbehind")), "service", RuleDataKind.Domain));
        var invalidUtf8 = Encode(w => { w.WriteTag(2, WireFormat.WireType.LengthDelimited); w.WriteBytes(ByteString.CopyFrom([255])); });
        Assert.False(GeoDatInspector.HasTag(DomainDat(invalidUtf8), "service", RuleDataKind.Domain));
        var repeatedValue = Encode(w => { Text(w, 2, "one"); Text(w, 2, "two"); });
        Assert.False(GeoDatInspector.HasTag(DomainDat(repeatedValue), "service", RuleDataKind.Domain));
    }

    [Fact]
    public void ValidatesAttributeOneofAndAllowsItsValidTypedValues()
    {
        var valid = Encode(w => { Text(w, 2, "keyword"); Message(w, 3, Encode(a => { Text(a, 1, "category"); a.WriteTag(2, WireFormat.WireType.Varint); a.WriteBool(true); })); });
        Assert.True(GeoDatInspector.HasTag(DomainDat(valid), "service", RuleDataKind.Domain));
        var invalid = Encode(w => { Text(w, 2, "keyword"); Message(w, 3, Encode(a => { Text(a, 1, "category"); a.WriteTag(2, WireFormat.WireType.Varint); a.WriteBool(true); a.WriteTag(3, WireFormat.WireType.Varint); a.WriteInt64(1); })); });
        Assert.False(GeoDatInspector.HasTag(DomainDat(invalid), "service", RuleDataKind.Domain));
    }

    [Fact]
    public void ValidatesIpv4Ipv6LengthsPrefixesAndCanonicalHostBits()
    {
        Assert.True(GeoDatInspector.HasTag(IpDat(Cidr([203, 0, 113, 0], 24)), "service", RuleDataKind.Ip));
        Assert.True(GeoDatInspector.HasTag(IpDat(Cidr(new byte[16], 128)), "service", RuleDataKind.Ip));
        Assert.True(GeoDatInspector.HasTag(IpDat(Cidr(new byte[4], null)), "service", RuleDataKind.Ip));
        Assert.False(GeoDatInspector.HasTag(IpDat(Cidr(new byte[5], 24)), "service", RuleDataKind.Ip));
        Assert.False(GeoDatInspector.HasTag(IpDat(Cidr(new byte[4], 33)), "service", RuleDataKind.Ip));
        Assert.False(GeoDatInspector.HasTag(IpDat(Cidr(new byte[16], 129)), "service", RuleDataKind.Ip));
        Assert.False(GeoDatInspector.HasTag(IpDat(Cidr([203, 0, 113, 1], 24)), "service", RuleDataKind.Ip));
        Assert.False(GeoDatInspector.HasTag(IpDat(Cidr([203, 0, 113, 0], null)), "service", RuleDataKind.Ip));
        Assert.False(GeoDatInspector.HasTag(IpDat(Cidr(new byte[4], 1UL << 32)), "service", RuleDataKind.Ip));
        var repeatedPrefix = Encode(w => { Message(w, 1, new byte[4]); w.WriteTag(2, WireFormat.WireType.Varint); w.WriteUInt32(24); w.WriteTag(2, WireFormat.WireType.Varint); w.WriteUInt32(32); });
        Assert.False(GeoDatInspector.HasTag(IpDat(repeatedPrefix), "service", RuleDataKind.Ip));
        var inverse = Encode(w => { Text(w, 1, "SERVICE"); Message(w, 2, Cidr(new byte[4], 0)); w.WriteTag(3, WireFormat.WireType.Varint); w.WriteBool(true); });
        Assert.False(GeoDatInspector.HasTag(Root(inverse), "service", RuleDataKind.Ip));
    }

    [Fact]
    public void InputAndTagSizeBoundsAreEnforcedBeforeParsing()
    {
        Assert.False(GeoDatInspector.HasTag(new byte[GeoDatInspector.MaximumDatBytes + 1], "service", RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag(DomainDat(Domain(2, "example.test")), new string('a', 129), RuleDataKind.Domain));
        Assert.False(GeoDatInspector.HasTag(DomainDat(Domain(2, "example.test")), "../service", RuleDataKind.Domain));
    }
    [Fact]
    public void GeneratedCodesMatchThePinnedCoreStreamingSeekRatherThanOnlyProtobufDecoding()
    {
        var rules = GeoRuleCompiler.Parse("payload: ['DOMAIN-SUFFIX,example.test', 'IP-CIDR,203.0.113.0/24']", "service", RuleDataKind.Classical);
        var dat = GeoRuleCompiler.Compile([rules]);
        Assert.True(CoreSeekMatches(dat.GeoSite, "service")); Assert.True(CoreSeekMatches(dat.GeoIp, "service"));
        var lowercaseCode = Root(Encode(w => { Text(w, 1, "service"); Message(w, 2, Domain(2, "example.test")); }));
        Assert.False(CoreSeekMatches(lowercaseCode, "service")); Assert.False(GeoDatInspector.HasTag(lowercaseCode, "service", RuleDataKind.Domain));
        var codeNotFirst = Root(Encode(w => { Message(w, 2, Domain(2, "example.test")); Text(w, 1, "SERVICE"); }));
        Assert.False(CoreSeekMatches(codeNotFirst, "service")); Assert.False(GeoDatInspector.HasTag(codeNotFirst, "service", RuleDataKind.Domain));
        var noncanonicalTopField = new byte[] { 138, 0 }.Concat(dat.GeoSite.Skip(1)).ToArray();
        Assert.False(CoreSeekMatches(noncanonicalTopField, "service")); Assert.False(GeoDatInspector.HasTag(noncanonicalTopField, "service", RuleDataKind.Domain));
        var maxTag = new string('a', 127); var longRules = GeoRuleCompiler.Parse("payload: ['+.example.test']", maxTag, RuleDataKind.Domain);
        var longDat = GeoRuleCompiler.Compile([longRules]); Assert.True(CoreSeekMatches(longDat.GeoSite, maxTag)); Assert.True(GeoDatInspector.HasTag(longDat.GeoSite, maxTag, RuleDataKind.Domain));
        Assert.False(GeoRuleCompiler.Parse("payload: ['+.example.test']", new string('a', 128), RuleDataKind.Domain).IsSuccess);
    }
    // Independent small model of v26.10.29 geodat_loader.go seek's exact prefix contract.
    private static bool CoreSeekMatches(byte[] bytes, string requested)
    {
        var code = Encoding.ASCII.GetBytes(requested.ToUpperInvariant()); var position = 0;
        while (position < bytes.Length)
        {
            var tag = bytes[position++]; ulong length = 0; var shift = 0;
            while (true)
            {
                if (position >= bytes.Length || shift >= 64) return false;
                var part = bytes[position++]; length |= (ulong)(part & 127) << shift;
                if ((part & 128) == 0) break;
                shift += 7;
            }
            if (length == 0 || length > (ulong)(bytes.Length - position)) return false;
            var body = bytes.AsSpan(position, (int)length);
            if (tag == 10 && body.Length >= code.Length + 2 && body[0] == 10 && body[1] == code.Length && body.Slice(2, code.Length).SequenceEqual(code)) return true;
            position += (int)length;
        }
        return false;
    }
    private static byte[] DomainDat(byte[] domain) => Root(Entry("service", domain));
    private static byte[] IpDat(byte[] cidr) => Root(Entry("service", cidr));
    private static byte[] Entry(string code, byte[] value) => Encode(w => { Text(w, 1, code.ToUpperInvariant()); if (value.Length > 0) Message(w, 2, value); });
    private static byte[] Root(byte[] entry) => Encode(w => Message(w, 1, entry));
    private static byte[] Domain(int type, string text) => Encode(w => { w.WriteTag(1, WireFormat.WireType.Varint); w.WriteInt32(type); Text(w, 2, text); });
    private static byte[] Cidr(byte[] bytes, ulong? prefix) => Encode(w => { Message(w, 1, bytes); if (prefix is { } value) { w.WriteTag(2, WireFormat.WireType.Varint); w.WriteUInt64(value); } });
    private static byte[] Encode(Action<CodedOutputStream> emit) { using var stream = new MemoryStream(); using (var output = new CodedOutputStream(stream, true)) { emit(output); output.Flush(); } return stream.ToArray(); }
    private static void Message(CodedOutputStream output, int field, byte[] bytes) { output.WriteTag(field, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(bytes)); }
    private static void Text(CodedOutputStream output, int field, string text) => Message(output, field, Encoding.UTF8.GetBytes(text));
}
