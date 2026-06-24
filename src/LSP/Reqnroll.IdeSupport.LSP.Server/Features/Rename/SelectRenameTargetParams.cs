#nullable enable

using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>Parameters for reqnroll/selectRenameTarget notification (F16).</summary>
public sealed class SelectRenameTargetParams
{
    [JsonProperty("uri")]
    public string Uri { get; set; } = "";

    [JsonProperty("version")]
    public int Version { get; set; }

    [JsonProperty("attributeIndex")]
    public int AttributeIndex { get; set; }
}
