using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SocketJack.Net.AgentBuilder {

    public sealed class AgentBuilderWorkflow {
        public int SchemaVersion { get; set; } = 2;
        public int Revision { get; set; } = 1;
        public string Id { get; set; } = "";
        public string OwnerUserName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string ApiName { get; set; } = "";
        public bool ApiEnabled { get; set; }
        public bool Enabled { get; set; } = true;
        public List<AgentBuilderNode> Nodes { get; set; } = new List<AgentBuilderNode>();
        public List<AgentBuilderEdge> Edges { get; set; } = new List<AgentBuilderEdge>();
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string PresetId { get; set; } = "";
        public int PresetVersion { get; set; }
        public string BasePresetHash { get; set; } = "";
        public AgentBuilderApplicationDefinition Application { get; set; } = new AgentBuilderApplicationDefinition();
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";

        public void Normalize() {
            if (string.IsNullOrWhiteSpace(Id))
                Id = "workflow_" + Guid.NewGuid().ToString("N");
            Name = string.IsNullOrWhiteSpace(Name) ? "Untitled Builder Workflow" : Name.Trim();
            ApiName = AgentBuilderSlug.Normalize(ApiName);
            Nodes ??= new List<AgentBuilderNode>();
            Edges ??= new List<AgentBuilderEdge>();
            Variables ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SchemaVersion = Math.Max(2, SchemaVersion);
            Revision = Math.Max(1, Revision);
            Application ??= new AgentBuilderApplicationDefinition();
            Application.Normalize();
            foreach (AgentBuilderNode node in Nodes)
                node.Normalize();
            foreach (AgentBuilderEdge edge in Edges)
                edge.Normalize();
        }
    }

    public sealed class AgentBuilderNode {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "input";
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public Dictionary<string, string> Config { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void Normalize() {
            if (string.IsNullOrWhiteSpace(Id))
                Id = "node_" + Guid.NewGuid().ToString("N");
            Type = string.IsNullOrWhiteSpace(Type) ? "input" : Type.Trim();
            Name = string.IsNullOrWhiteSpace(Name) ? Type : Name.Trim();
            Config ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class AgentBuilderEdge {
        public string Id { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string SourcePort { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string TargetPort { get; set; } = "";

        public void Normalize() {
            if (string.IsNullOrWhiteSpace(Id))
                Id = "edge_" + Guid.NewGuid().ToString("N");
            SourceId = (SourceId ?? "").Trim();
            TargetId = (TargetId ?? "").Trim();
            SourcePort = (SourcePort ?? "").Trim();
            TargetPort = (TargetPort ?? "").Trim();
        }
    }

    public sealed class AgentBuilderInputDefinition {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string Type { get; set; } = "text";
        public bool Required { get; set; }
        public bool IsFileLike { get; set; }
        public string DefaultValue { get; set; } = "";
        public string SourceNodeId { get; set; } = "";
    }

    public sealed class AgentBuilderApiDefinition {
        public string Id { get; set; } = "";
        public string WorkflowId { get; set; } = "";
        public string OwnerUserName { get; set; } = "";
        public string ApiName { get; set; } = "";
        public string Route { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public bool RequireAuthentication { get; set; } = true;
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    public sealed class AgentBuilderRun {
        public string Id { get; set; } = "";
        public string WorkflowId { get; set; } = "";
        public string ApiName { get; set; } = "";
        public string OwnerUserName { get; set; } = "";
        public string TriggerKind { get; set; } = "manual";
        public string Status { get; set; } = "created";
        public string InputJson { get; set; } = "";
        public string OutputJson { get; set; } = "";
        public string Error { get; set; } = "";
        public string NodeResultsJson { get; set; } = "";
        public long DurationMs { get; set; }
        public string StartedUtc { get; set; } = "";
        public string CompletedUtc { get; set; } = "";
    }

    public sealed class AgentBuilderSchedule {
        public string Id { get; set; } = "";
        public string WorkflowId { get; set; } = "";
        public string OwnerUserName { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public bool PreventOverlap { get; set; } = true;
        public string Kind { get; set; } = "interval";
        public int IntervalSeconds { get; set; } = 300;
        public string TimeOfDay { get; set; } = "";
        public string TimeZone { get; set; } = "UTC";
        public string CriteriaJson { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string InputJson { get; set; } = "";
        public string LogFilePath { get; set; } = "";
        public bool RunOnce { get; set; }
        public string LastStatus { get; set; } = "";
        public string LastError { get; set; } = "";
        public string LastRunUtc { get; set; } = "";
        public string NextRunUtc { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    public sealed class AgentBuilderAuditEvent {
        public string Id { get; set; } = "";
        public string WorkflowId { get; set; } = "";
        public string RunId { get; set; } = "";
        public string OwnerUserName { get; set; } = "";
        public string EventType { get; set; } = "";
        public string Message { get; set; } = "";
        public string PayloadJson { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
    }

    public sealed class AgentBuilderWorkflowValidationResult {
        public bool Ok { get; set; } = true;
        public List<AgentBuilderWorkflowValidationIssue> Errors { get; set; } = new List<AgentBuilderWorkflowValidationIssue>();
        public List<AgentBuilderWorkflowValidationIssue> Warnings { get; set; } = new List<AgentBuilderWorkflowValidationIssue>();
    }

    public sealed class AgentBuilderWorkflowValidationIssue {
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public string NodeId { get; set; } = "";
        public string EdgeId { get; set; } = "";
    }

    public sealed class AgentBuilderExecutionRequest {
        public AgentBuilderWorkflow Workflow { get; set; } = new AgentBuilderWorkflow();
        public Dictionary<string, object> Inputs { get; set; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public string TriggerKind { get; set; } = "manual";
        public string UserName { get; set; } = "";
        public IAgentBuilderAgentRunner AgentRunner { get; set; }
        public IAgentBuilderReflectionExecutor ReflectionExecutor { get; set; }
        public IAgentBuilderUrlFetcher UrlFetcher { get; set; }
    }

    public sealed class AgentBuilderExecutionResult {
        public bool Ok { get; set; }
        public string RunId { get; set; } = "";
        public string WorkflowId { get; set; } = "";
        public string Status { get; set; } = "created";
        public string TriggerKind { get; set; } = "manual";
        public string Error { get; set; } = "";
        public object Output { get; set; }
        public Dictionary<string, object> Inputs { get; set; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public List<AgentBuilderNodeResult> NodeResults { get; set; } = new List<AgentBuilderNodeResult>();
        public string StartedUtc { get; set; } = "";
        public string CompletedUtc { get; set; } = "";
        public long DurationMs { get; set; }
    }

    public sealed class AgentBuilderNodeResult {
        public string NodeId { get; set; } = "";
        public string NodeType { get; set; } = "";
        public string NodeName { get; set; } = "";
        public bool Ok { get; set; } = true;
        public bool Skipped { get; set; }
        public string Status { get; set; } = "completed";
        public string Error { get; set; } = "";
        public object Value { get; set; }
        public string Json { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public string CompletedUtc { get; set; } = "";
        public long DurationMs { get; set; }
    }

    public interface IAgentBuilderAgentRunner {
        System.Threading.Tasks.Task<object> RunAgentAsync(
            AgentBuilderNode node,
            IReadOnlyDictionary<string, object> inputs,
            IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults,
            string prompt,
            System.Threading.CancellationToken cancellationToken);
    }

    public interface IAgentBuilderReflectionExecutor {
        System.Threading.Tasks.Task<object> ExecuteAsync(
            AgentBuilderNode node,
            IReadOnlyDictionary<string, object> inputs,
            IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults,
            System.Threading.CancellationToken cancellationToken);

        object GetCatalog(int take = 200);
    }

    public interface IAgentBuilderUrlFetcher {
        System.Threading.Tasks.Task<object> FetchAsync(
            AgentBuilderNode node,
            IReadOnlyDictionary<string, object> inputs,
            IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults,
            string url,
            System.Threading.CancellationToken cancellationToken);
    }

    public static class AgentBuilderSlug {
        private static readonly Regex UnsafeCharacters = new Regex("[^a-z0-9-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RepeatedDashes = new Regex("-{2,}", RegexOptions.Compiled);

        public static string Normalize(string value) {
            value = (value ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value))
                return "";
            value = UnsafeCharacters.Replace(value, "-");
            value = RepeatedDashes.Replace(value, "-");
            return value.Trim('-');
        }
    }
}
