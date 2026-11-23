using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scio.Abstractions
{
    public enum PermissionProfile
    {
        ReadOnly,
        SafeWrite,
        UnsafeCode
    }

    public enum VerificationStatus
    {
        ReadOnly,
        DryRunPreview,
        Verified,
        Partial,
        SelfReportedVerified,
        Unverified,
        Refused,
        Failed
    }

    public sealed class ToolRequest
    {
        [JsonProperty("request_id")]
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonProperty("tool")]
        public string Tool { get; set; } = string.Empty;

        [JsonProperty("arguments")]
        public JObject Arguments { get; set; } = new JObject();

        [JsonProperty("target_document")]
        public string? TargetDocument { get; set; }

        [JsonProperty("dry_run")]
        public bool? DryRun { get; set; }

        [JsonProperty("verbosity")]
        public string Verbosity { get; set; } = "standard";

        [JsonProperty("timeout_ms")]
        public int TimeoutMs { get; set; } = 120_000;
    }

    public sealed class ToolError
    {
        [JsonProperty("code")]
        public string Code { get; set; } = "internal_error";

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
        public string? Path { get; set; }

        [JsonProperty("details", NullValueHandling = NullValueHandling.Ignore)]
        public JToken? Details { get; set; }
    }

    public sealed class ToolResponse
    {
        [JsonProperty("request_id")]
        public string RequestId { get; set; } = string.Empty;

        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("verification")]
        public string Verification { get; set; } = "read_only";

        [JsonProperty("host_verified")]
        public bool HostVerified { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public JToken? Data { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public ToolError? Error { get; set; }

        [JsonProperty("elapsed_ms")]
        public long ElapsedMs { get; set; }

        [JsonProperty("queued_ms")]
        public long QueuedMs { get; set; }

        public static ToolResponse Success(
            string requestId,
            JToken? data,
            string summary = "",
            VerificationStatus status = VerificationStatus.ReadOnly,
            bool hostVerified = false)
        {
            return new ToolResponse
            {
                RequestId = requestId,
                Ok = true,
                Verification = FormatStatus(status),
                HostVerified = hostVerified,
                Summary = summary,
                Data = data
            };
        }

        public static ToolResponse Refusal(string requestId, string code, string message, string? path = null, JToken? details = null)
        {
            return new ToolResponse
            {
                RequestId = requestId,
                Ok = false,
                Verification = FormatStatus(VerificationStatus.Refused),
                HostVerified = false,
                Summary = message,
                Error = new ToolError
                {
                    Code = code,
                    Message = message,
                    Path = path,
                    Details = details
                }
            };
        }

        public static ToolResponse Failure(string requestId, string code, string message, JToken? details = null)
        {
            return new ToolResponse
            {
                RequestId = requestId,
                Ok = false,
                Verification = FormatStatus(VerificationStatus.Failed),
                HostVerified = false,
                Summary = message,
                Error = new ToolError
                {
                    Code = code,
                    Message = message,
                    Details = details
                }
            };
        }

        private static string FormatStatus(VerificationStatus status)
        {
            switch (status)
            {
                case VerificationStatus.ReadOnly: return "read_only";
                case VerificationStatus.DryRunPreview: return "dry_run_preview";
                case VerificationStatus.Verified: return "verified";
                case VerificationStatus.SelfReportedVerified: return "self_reported_verified";
                case VerificationStatus.Refused: return "refused";
                default: return "failed";
            }
        }
    }

    public sealed class ToolDescriptor
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("pack")]
        public string Pack { get; set; } = "core";

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("read_only")]
        public bool ReadOnly { get; set; }

        [JsonProperty("destructive")]
        public bool Destructive { get; set; }

        [JsonProperty("open_world")]
        public bool OpenWorld { get; set; }

        [JsonProperty("server_side")]
        public bool ServerSide { get; set; }

        [JsonProperty("input_schema")]
        public JObject InputSchema { get; set; } = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false
        };
    }

    public interface IToolModule
    {
        string ModuleName { get; }
        IEnumerable<ToolDescriptor> DescribeTools();
    }
}
