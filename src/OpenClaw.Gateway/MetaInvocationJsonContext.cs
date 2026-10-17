using System.Text.Json.Serialization;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MetaInvocationRecord))]
[JsonSerializable(typeof(List<MetaInvocationRecord>))]
[JsonSerializable(typeof(MetaSkillInvocationRequest))]
[JsonSerializable(typeof(MetaInvocationResumeRequest))]
[JsonSerializable(typeof(MetaInvocationResponse))]
[JsonSerializable(typeof(MetaInvocationPauseInfo))]
[JsonSerializable(typeof(MetaApprovalSummary))]
[JsonSerializable(typeof(MetaApprovalSummary[]))]
[JsonSerializable(typeof(MetaApprovalDecisionRequest))]
[JsonSerializable(typeof(MetaApprovalReceipt))]
internal sealed partial class MetaInvocationJsonContext : JsonSerializerContext;