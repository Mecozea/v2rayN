namespace ServiceLib.Tests.Fmt;

public class NowhereFmtTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly string Pin = new('a', 64);

    [Test]
    public async Task RoundTrip_PreservesCarrierTrustAndName()
    {
        var link = $"nowhere://{Key}@[2001:db8::1]:8443?net=udp&morph=0&sni=relay.example&pin={Pin}#%E4%B8%AD%E6%96%87%2520%2B";
        var first = FmtHandler.ResolveConfig(link, out _);
        await first.Should().NotBeNull();
        var exported = FmtHandler.GetShareUri(first!);
        var second = FmtHandler.ResolveConfig(exported!, out _);
        await second.Should().NotBeNull();
        await second!.Address.Should().BeEqualTo("2001:db8::1");
        await second.Password.Should().BeEqualTo(Key);
        await second.GetProtocolExtra().NowhereCarrier.Should().BeEqualTo("udp");
        await second.GetProtocolExtra().NowhereMorph.Should().BeEqualTo(false);
        await second.GetProtocolExtra().NowherePin.Should().BeEqualTo(Pin);
        await second.Sni.Should().BeEqualTo("relay.example");
        await second.Remarks.Should().BeEqualTo("中文%20+");
        await second.IsValid().Should().BeTrue();
    }

    [Test]
    public async Task Defaults_AndDuplicateFirstValue_AreStable()
    {
        var item = NowhereFmt.Resolve($"NOWHERE://{Key}@relay.example:443?net=udp&net=tcp&morph=0&morph=invalid", out _);
        await item.Should().NotBeNull();
        await item!.GetProtocolExtra().NowhereCarrier.Should().BeEqualTo("udp");
        await item.GetProtocolExtra().NowhereMorph.Should().BeEqualTo(false);
        var defaults = NowhereFmt.Resolve($"nowhere://{Key}@relay.example:443", out _);
        await defaults.Should().NotBeNull();
        await defaults!.GetProtocolExtra().NowhereCarrier.Should().BeEqualTo("tcp");
        await defaults.GetProtocolExtra().NowhereMorph.Should().BeEqualTo(true);
    }

    [Test]
    [Arguments("?net=bad&net=tcp")]
    [Arguments("?net=")]
    [Arguments("??net=udp")]
    [Arguments("?morph=invalid&morph=1")]
    [Arguments("?morph=")]
    [Arguments("?pin=bad")]
    [Arguments("?morph=%2531")]
    [Arguments("?sni=hello%252eworld")]
    [Arguments("?pin=BAD")]
    [Arguments("?sni=127.0.0.1")]
    [Arguments("?sni=bad%20name")]
    [Arguments("?in=0.0.0.0:1080")]
    [Arguments("?rate=1")]
    [Arguments("?Net=tcp")]
    [Arguments("?unknown=future")]
    [Arguments("?morph=%ZZ")]
    [Arguments("#%FF")]
    [Arguments("/tcp:443")]
    [Arguments("/")]
    public async Task InvalidOptions_AreRejected(string suffix)
    {
        var item = NowhereFmt.Resolve($"nowhere://{Key}@relay.example:443{suffix}", out var error);
        await item.Should().BeNull();
        await error.Should().BeEqualTo(ResUI.ConfigurationFormatIncorrect);
    }

    [Test]
    [Arguments("nowhere://short@relay.example:443")]
    [Arguments("nowhere://0123456789abcdef0123456789abcdef@*:443")]
    [Arguments("nowhere://0123456789abcdef0123456789abcdef@%2A:443")]
    [Arguments("nowhere://0123456789ABCDEF0123456789abcdef@relay.example:443")]
    [Arguments("nowhere://0123456789abcdef0123456789abcdef:secret@relay.example:443")]
    [Arguments("nowhere://0123456789abcdef0123456789abcdef@0.0.0.0:443")]
    [Arguments("nowhere://0123456789abcdef0123456789abcdef@[::]:443")]
    [Arguments("nowhere://0123456789abcdef0123456789abcdef@relay.example:0")]
    [Arguments("nowhere://0123456789abcdef0123456789abcdef@relay.example")]
    public async Task InvalidEndpoint_AreRejected(string link)
    {
        await NowhereFmt.Resolve(link, out _).Should().BeNull();
    }

    [Test]
    public async Task ServiceUrl_IsLoopbackOnly_AndOmitsDisplayName()
    {
        var item = NowhereFmt.Resolve($"nowhere://{Key}@[2001:db8::1]:8443?net=udp&morph=0&pin={Pin}#secret-name", out _)!;
        var service = new Uri(NowhereFmt.BuildServiceUrl(item, 12345));
        await service.Scheme.Should().BeEqualTo("vector");
        await service.Port.Should().BeEqualTo(-1);
        await service.AbsolutePath.Should().BeEqualTo("/udp:8443");
        await service.Fragment.Should().BeEqualTo(string.Empty);
        var query = Utils.ParseQueryString(service.Query);
        await query["in"].Should().BeEqualTo("127.0.0.1:12345");
        await query["pin"].Should().BeEqualTo(Pin);
        await item.Address.Should().BeEqualTo("2001:db8::1");
        await item.Port.Should().BeEqualTo(8443);
    }

    [Test]
    public async Task Fragment_PreservesLiteralLeadingHash()
    {
        var item = NowhereFmt.Resolve($"nowhere://{Key}@relay.example:443##name", out _);
        await item.Should().NotBeNull();
        await item!.Remarks.Should().BeEqualTo("#name");
        var restored = NowhereFmt.Resolve(NowhereFmt.ToUri(item)!, out _);
        await restored!.Remarks.Should().BeEqualTo("#name");
    }

    [Test]
    public async Task DuplicateValue_IgnoresInvalidUtf8AfterValidFirstValue()
    {
        var item = NowhereFmt.Resolve($"nowhere://{Key}@relay.example:443?net=udp&net=%FF&morph=0&morph=%FF", out _);
        await item.Should().NotBeNull();
        await item!.GetProtocolExtra().NowhereCarrier.Should().BeEqualTo("udp");
        await item.GetProtocolExtra().NowhereMorph.Should().BeEqualTo(false);
    }

    [Test]
    public async Task Persistence_PreservesProtocolOptions()
    {
        var item = NowhereFmt.Resolve($"nowhere://{Key}@relay.example:443?net=udp&morph=0&pin={Pin}", out _)!;
        var restored = JsonUtils.Deserialize<ProfileItem>(JsonUtils.Serialize(item))!;
        await NowhereFmt.ToUri(restored).Should().BeEqualTo(NowhereFmt.ToUri(item));
    }
}
