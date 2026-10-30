namespace ServiceLib.Handler.Fmt;

/// <summary>
/// NW3 links. sni/pin are the fork's explicit share extensions; never drop trust settings.
/// Unknown options are rejected so future connection semantics cannot silently change.
/// </summary>
public static class NowhereFmt
{
    public static ProfileItem? Resolve(string str, out string msg)
    {
        msg = ResUI.ConfigurationFormatIncorrect;
        try
        {
            if (!HasValidEscapes(str) || str.Any(char.IsControl) || str.Contains('\\')
                || !Uri.TryCreate(str, UriKind.Absolute, out var uri)
                || !uri.Scheme.Equals("nowhere", StringComparison.OrdinalIgnoreCase)
                || HasPath(str) || uri.UserInfo.Contains(':'))
            {
                return null;
            }

            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in (uri.Query.Length == 0 ? string.Empty : uri.Query[1..]).Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = entry.Split('=', 2);
                var name = Decode(pair[0]);
                if (name is not ("net" or "morph" or "sni" or "pin"))
                {
                    return null;
                }
                // Match Nowhere's first-value semantics, including an invalid first value.
                if (!query.ContainsKey(name))
                {
                    query.Add(name, pair.Length == 2 ? Decode(pair[1]) : string.Empty);
                }
            }
            var carrier = query.GetValueOrDefault("net", "tcp");
            var morph = query.GetValueOrDefault("morph", "1");
            if (morph is not ("0" or "1"))
            {
                return null;
            }
            var item = new ProfileItem
            {
                ConfigType = EConfigType.Nowhere,
                Address = uri.IdnHost.Trim('[', ']'),
                Port = uri.Port,
                Password = Decode(uri.UserInfo),
                Remarks = Decode((uri.Fragment.Length == 0 ? string.Empty : uri.Fragment[1..])),
                Sni = NormalizeOptional(query.GetValueOrDefault("sni")),
            };
            item.SetProtocolExtra(item.GetProtocolExtra() with
            {
                NowhereCarrier = carrier,
                NowhereMorph = morph == "1",
                NowherePin = NormalizeOptional(query.GetValueOrDefault("pin")),
            });
            if (!IsValid(item))
            {
                return null;
            }
            msg = string.Empty;
            return item;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            // Never log the untrusted URL or URI exception: both can contain the shared key.
            return null;
        }
    }

    public static bool IsValid(ProfileItem item)
    {
        var extra = item.GetProtocolExtra();
        return item.ConfigType == EConfigType.Nowhere
            && item.Port is > 0 and <= 65535
            && IsHost(item.Address)
            && IsLowerHex(item.Password, 32)
            && (extra.NowhereCarrier ?? "tcp") is "tcp" or "udp"
            && (string.IsNullOrEmpty(extra.NowherePin) || IsLowerHex(extra.NowherePin, 64))
            && (string.IsNullOrEmpty(item.Sni) || IsDnsName(item.Sni));
    }

    public static string? ToUri(ProfileItem? item)
    {
        if (item == null || !IsValid(item))
        {
            return null;
        }
        var builder = CreateBuilder(item, "nowhere");
        builder.Port = item.Port;
        builder.Query = BuildQuery(item, false, 0);
        builder.Fragment = Uri.EscapeDataString(item.Remarks ?? string.Empty);
        return builder.Uri.GetLeftPart(UriPartial.Authority) + builder.Uri.Query + builder.Uri.Fragment;
    }

    public static string BuildServiceUrl(ProfileItem item, int localPort)
    {
        if (!IsValid(item) || localPort is < 1 or > 65535)
        {
            throw new ArgumentException("Invalid Nowhere endpoint or local SOCKS port.");
        }
        var builder = CreateBuilder(item, "vector");
        builder.Path = $"/{item.GetProtocolExtra().NowhereCarrier ?? "tcp"}:{item.Port}";
        builder.Query = BuildQuery(item, true, localPort);
        return builder.Uri.AbsoluteUri;
    }

    private static UriBuilder CreateBuilder(ProfileItem item, string scheme) => new()
    {
        Scheme = scheme,
        Host = item.Address,
        Port = -1,
        UserName = item.Password,
        Path = string.Empty,
    };

    private static string BuildQuery(ProfileItem item, bool service, int localPort)
    {
        var extra = item.GetProtocolExtra();
        var options = new List<KeyValuePair<string, string>>();
        options.Add(service
            ? new("in", $"127.0.0.1:{localPort}")
            : new("net", extra.NowhereCarrier ?? "tcp"));
        options.Add(new("morph", extra.NowhereMorph == false ? "0" : "1"));
        if (!string.IsNullOrEmpty(item.Sni))
        {
            options.Add(new("sni", item.Sni));
        }
        if (!string.IsNullOrEmpty(extra.NowherePin))
        {
            options.Add(new("pin", extra.NowherePin));
        }
        return string.Join("&", options.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static string NormalizeOptional(string? value) => value is null or "" or "none" ? string.Empty : value;
    private static string Decode(string value)
    {
        // Nowhere uses percent decoding, not HTML form decoding: '+' remains literal.
        var raw = Encoding.UTF8.GetBytes(value);
        using var bytes = new MemoryStream(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'%')
            {
                if (i + 2 >= raw.Length || !Uri.IsHexDigit((char)raw[i + 1]) || !Uri.IsHexDigit((char)raw[i + 2]))
                {
                    throw new ArgumentException("Invalid Nowhere percent encoding.");
                }
                bytes.WriteByte(Convert.ToByte(Encoding.ASCII.GetString(raw, i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.WriteByte(raw[i]);
            }
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }
    private static bool IsLowerHex(string? value, int length) => value?.Length == length
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }
        if (IPAddress.TryParse(host, out var address))
        {
            return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)
                && !host.Contains('%');
        }
        return IsDnsName(host);
    }

    private static bool IsDnsName(string host) => host.Length is > 0 and <= 253
        && !IPAddress.TryParse(host, out _)
        && host.Split('.').All(label => label.Length is > 0 and <= 63
            && label[0] != '-' && label[^1] != '-'
            && label.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'));

    private static bool HasPath(string value)
    {
        var start = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = value.IndexOfAny(['/', '?', '#'], start);
        return end >= 0 && value[end] == '/';
    }

    private static bool HasValidEscapes(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                continue;
            }
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
            {
                return false;
            }
            i += 2;
        }
        return true;
    }
}
