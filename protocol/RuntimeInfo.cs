using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quodery.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeInfo(string Version)
{
    public static bool TryRead(JsonElement payload, out RuntimeInfo? info)
    {
        info = null;
        try { info = payload.Deserialize<RuntimeInfo>(ProtocolJson.Options); }
        catch (JsonException) { return false; }
        return info is not null && System.Version.TryParse(info.Version, out _);
    }
}
