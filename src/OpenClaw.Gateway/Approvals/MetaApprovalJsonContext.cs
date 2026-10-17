using System.Text.Json.Serialization;
using OpenClaw.Core.Approvals;

namespace OpenClaw.Gateway.Approvals;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MetaApprovalRecord))]
[JsonSerializable(typeof(List<MetaApprovalRecord>))]
internal sealed partial class MetaApprovalJsonContext : JsonSerializerContext;
