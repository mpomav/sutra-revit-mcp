using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    /// <summary>
    /// Bounded asynchronous job queue for long-running MCP tool calls (scio_submit_job / scio_job_status).
    /// </summary>
    public sealed class AsyncJobManager
    {
        private sealed class JobEntry
        {
            public string JobId { get; set; } = string.Empty;
            public string Tool { get; set; } = string.Empty;
            public string Status { get; set; } = "queued";
            public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
            public DateTime? CompletedUtc { get; set; }
            public long ElapsedMs { get; set; }
            public ToolResponse? Result { get; set; }
            public string? Error { get; set; }
        }

        private readonly ConcurrentDictionary<string, JobEntry> _jobs = new ConcurrentDictionary<string, JobEntry>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public JObject Submit(ServerToolRegistry registry, JObject args)
        {
            string tool = (args?.Value<string>("tool") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(tool))
                throw new ToolRefusalException("'tool' is required for scio_submit_job.");
            if (string.Equals(tool, "scio_submit_job", StringComparison.Ordinal))
                throw new ToolRefusalException("Recursive scio_submit_job is not permitted.");

            var toolArgs = args?["arguments"] as JObject ?? new JObject();
            string jobId = "job-" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var entry = new JobEntry { JobId = jobId, Tool = tool, Status = "queued", CreatedUtc = DateTime.UtcNow };
            _jobs[jobId] = entry;

            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    entry.Status = "running";
                    var req = new ToolRequest
                    {
                        RequestId = jobId,
                        Tool = tool,
                        Arguments = toolArgs,
                        TargetDocument = toolArgs.Value<string>("target_document"),
                        DryRun = toolArgs.Value<bool?>("dry_run")
                    };
                    var resp = await registry.DispatchAsync(req, CancellationToken.None).ConfigureAwait(false);
                    sw.Stop();
                    entry.ElapsedMs = sw.ElapsedMilliseconds;
                    entry.Result = resp;
                    entry.Status = resp.Ok ? "completed" : "failed";
                    entry.CompletedUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    entry.ElapsedMs = sw.ElapsedMilliseconds;
                    entry.Error = ex.Message;
                    entry.Status = "failed";
                    entry.CompletedUtc = DateTime.UtcNow;
                }
                finally
                {
                    _gate.Release();
                }
            });

            return new JObject
            {
                ["job_id"] = jobId,
                ["tool"] = tool,
                ["status"] = entry.Status,
                ["created_utc"] = entry.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)
            };
        }

        public JObject Status(JObject args)
        {
            string? jobId = args?.Value<string>("job_id")?.Trim();
            if (string.IsNullOrWhiteSpace(jobId))
            {
                var recent = _jobs.Values
                    .OrderByDescending(j => j.CreatedUtc)
                    .Take(25)
                    .Select(j => new JObject
                    {
                        ["job_id"] = j.JobId,
                        ["tool"] = j.Tool,
                        ["status"] = j.Status,
                        ["elapsed_ms"] = j.ElapsedMs
                    });
                return new JObject { ["count"] = _jobs.Count, ["jobs"] = new JArray(recent) };
            }

            if (!_jobs.TryGetValue(jobId!, out var entry))
                throw new ToolRefusalException($"Job '{jobId}' not found.");

            var obj = new JObject
            {
                ["job_id"] = entry.JobId,
                ["tool"] = entry.Tool,
                ["status"] = entry.Status,
                ["created_utc"] = entry.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
                ["completed_utc"] = entry.CompletedUtc?.ToString("O", CultureInfo.InvariantCulture),
                ["elapsed_ms"] = entry.ElapsedMs
            };
            if (entry.Result != null) obj["result"] = JObject.FromObject(entry.Result);
            if (!string.IsNullOrEmpty(entry.Error)) obj["error"] = entry.Error;
            return obj;
        }
    }
}
