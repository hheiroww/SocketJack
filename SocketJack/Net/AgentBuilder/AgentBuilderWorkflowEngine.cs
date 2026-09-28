using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.AgentBuilder {

    public sealed class AgentBuilderWorkflowEngine {
        private static readonly Regex TemplateToken = new Regex(@"\{\{\s*([A-Za-z0-9_.:-]+)\s*\}\}", RegexOptions.Compiled);

        public async Task<AgentBuilderExecutionResult> ExecuteAsync(AgentBuilderExecutionRequest request, CancellationToken cancellationToken = default) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            AgentBuilderWorkflow workflow = request.Workflow ?? new AgentBuilderWorkflow();
            workflow.Normalize();

            var started = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var result = new AgentBuilderExecutionResult {
                RunId = "run_" + Guid.NewGuid().ToString("N"),
                WorkflowId = workflow.Id,
                TriggerKind = string.IsNullOrWhiteSpace(request.TriggerKind) ? "manual" : request.TriggerKind,
                Inputs = request.Inputs ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                StartedUtc = started.ToString("O", CultureInfo.InvariantCulture),
                Status = "running"
            };

            var nodeResults = new Dictionary<string, AgentBuilderNodeResult>(StringComparer.OrdinalIgnoreCase);
            try {
                IReadOnlyList<AgentBuilderInputDefinition> inputDefinitions = GetInputDefinitions(workflow);
                List<AgentBuilderInputDefinition> missing = GetMissingRequiredInputs(inputDefinitions, result.Inputs);
                if (missing.Count > 0)
                    throw new InvalidOperationException("Missing required input(s): " + string.Join(", ", missing.Select(item => item.Key)));

                List<AgentBuilderNode> orderedNodes = TopologicalSort(workflow);
                foreach (AgentBuilderNode node in orderedNodes) {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (nodeResults.ContainsKey(node.Id))
                        continue;

                    if (IsParallelNode(node)) {
                        AgentBuilderNodeResult parallelMarker = CreateMarkerResult(node, "parallel", new {
                            message = "Parallel fan-out started.",
                            children = GetParallelChildren(workflow, node).Select(child => child.Id).ToArray()
                        });
                        nodeResults[node.Id] = parallelMarker;
                        result.NodeResults.Add(parallelMarker);

                        List<AgentBuilderNode> children = GetParallelChildren(workflow, node)
                            .Where(child => !nodeResults.ContainsKey(child.Id))
                            .ToList();
                        AgentBuilderNodeResult[] childResults = await Task.WhenAll(children.Select(child => ExecuteNodeAsync(workflow, child, result.Inputs, nodeResults, request, cancellationToken))).ConfigureAwait(false);
                        foreach (AgentBuilderNodeResult childResult in childResults) {
                            nodeResults[childResult.NodeId] = childResult;
                            result.NodeResults.Add(childResult);
                        }
                        AgentBuilderNodeResult failedChild = childResults.FirstOrDefault(item => !item.Ok);
                        if (failedChild != null)
                            throw new InvalidOperationException(FirstNonEmpty(failedChild.Error, failedChild.NodeName + " failed."));
                        continue;
                    }

                    AgentBuilderNodeResult nodeResult = await ExecuteNodeAsync(workflow, node, result.Inputs, nodeResults, request, cancellationToken).ConfigureAwait(false);
                    nodeResults[node.Id] = nodeResult;
                    result.NodeResults.Add(nodeResult);
                    if (!nodeResult.Ok)
                        throw new InvalidOperationException(FirstNonEmpty(nodeResult.Error, node.Name + " failed."));
                }

                AgentBuilderNodeResult returnResult = result.NodeResults.LastOrDefault(item => IsReturnNodeType(item.NodeType) && item.Ok && !item.Skipped)
                    ?? result.NodeResults.LastOrDefault(item => item.Ok && !item.Skipped);
                result.Output = returnResult?.Value;
                result.Ok = true;
                result.Status = "completed";
            } catch (Exception ex) {
                result.Ok = false;
                result.Status = "failed";
                result.Error = ex.Message;
            } finally {
                stopwatch.Stop();
                result.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                result.DurationMs = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }

        private async Task<AgentBuilderNodeResult> ExecuteNodeAsync(
            AgentBuilderWorkflow workflow,
            AgentBuilderNode node,
            IReadOnlyDictionary<string, object> inputs,
            IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults,
            AgentBuilderExecutionRequest request,
            CancellationToken cancellationToken) {

            var started = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var nodeResult = new AgentBuilderNodeResult {
                NodeId = node.Id,
                NodeType = node.Type,
                NodeName = node.Name,
                StartedUtc = started.ToString("O", CultureInfo.InvariantCulture),
                Status = "running"
            };

            try {
                if (ShouldSkipByGate(workflow, node, nodeResults)) {
                    nodeResult.Skipped = true;
                    nodeResult.Ok = true;
                    nodeResult.Status = "skipped";
                    nodeResult.Value = null;
                    nodeResult.Json = "null";
                    return nodeResult;
                }

                string type = NormalizeNodeType(node.Type);
                switch (type) {
                    case "input":
                        nodeResult.Value = ResolveInputNode(node, inputs);
                        break;
                    case "logicgate":
                    case "gate":
                        nodeResult.Value = EvaluateGate(node, inputs, nodeResults);
                        break;
                    case "agent":
                        nodeResult.Value = request.AgentRunner == null
                            ? BuildPreparedAgentResult(node, inputs, nodeResults)
                            : await request.AgentRunner.RunAgentAsync(node, inputs, nodeResults, HasTerminalIntent(node) ? ResolveTerminalPrompt(node, inputs, nodeResults) : ResolvePrompt(node, inputs, nodeResults), cancellationToken).ConfigureAwait(false);
                        break;
                    case "terminal":
                    case "terminalcommand":
                    case "command":
                        nodeResult.Value = request.AgentRunner == null
                            ? BuildPreparedAgentResult(node, inputs, nodeResults)
                            : await request.AgentRunner.RunAgentAsync(node, inputs, nodeResults, ResolveTerminalPrompt(node, inputs, nodeResults), cancellationToken).ConfigureAwait(false);
                        break;
                    case "reflectionobject":
                    case "reflectioncall":
                    case "reflection":
                        if (request.ReflectionExecutor == null)
                            throw new InvalidOperationException("Reflection executor is not configured.");
                        nodeResult.Value = await request.ReflectionExecutor.ExecuteAsync(node, inputs, nodeResults, cancellationToken).ConfigureAwait(false);
                        break;
                    case "url":
                    case "urlfetch":
                    case "webscrape":
                    case "scrape":
                    case "crawl":
                        if (request.UrlFetcher == null)
                            throw new InvalidOperationException("URL fetcher is not configured.");
                        string url = ResolveTemplate(FirstNonEmpty(Config(node, "url"), Config(node, "uri")), inputs, nodeResults);
                        if (string.IsNullOrWhiteSpace(url))
                            throw new InvalidOperationException("URL Fetch node needs a URL.");
                        nodeResult.Value = await request.UrlFetcher.FetchAsync(node, inputs, nodeResults, url, cancellationToken).ConfigureAwait(false);
                        break;
                    case "timer":
                    case "schedule":
                    case "timerschedule":
                        nodeResult.Value = BuildScheduleMarker(node);
                        break;
                    case "returnschema":
                    case "outputschema":
                    case "return":
                    case "output":
                        nodeResult.Value = ResolveReturnNode(node, inputs, nodeResults);
                        break;
                    default:
                        nodeResult.Value = ResolveConfiguredValue(node, "value", inputs, nodeResults);
                        break;
                }

                if (IsAgentLikeNodeType(type) && TryGetStructuredFailure(nodeResult.Value, out string structuredError)) {
                    nodeResult.Ok = false;
                    nodeResult.Status = "failed";
                    nodeResult.Error = structuredError;
                } else {
                    nodeResult.Ok = true;
                    nodeResult.Status = "completed";
                }
                int maxOutputChars = ParseInt(Config(node, "maxOutputChars"), 50000);
                nodeResult.Json = AgentBuilderJson.SafeSerialize(nodeResult.Value, Math.Max(1000, maxOutputChars));
            } catch (Exception ex) {
                nodeResult.Ok = false;
                nodeResult.Status = "failed";
                nodeResult.Error = ex.Message;
                nodeResult.Value = new { error = ex.Message };
                nodeResult.Json = AgentBuilderJson.SafeSerialize(nodeResult.Value);
            } finally {
                stopwatch.Stop();
                nodeResult.CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                nodeResult.DurationMs = stopwatch.ElapsedMilliseconds;
            }

            return nodeResult;
        }

        public static IReadOnlyList<AgentBuilderInputDefinition> GetInputDefinitions(AgentBuilderWorkflow workflow) {
            if (workflow == null)
                return Array.Empty<AgentBuilderInputDefinition>();
            workflow.Normalize();
            return workflow.Nodes
                .Where(node => NormalizeNodeType(node.Type) == "input")
                .Select(node => {
                    string key = FirstNonEmpty(Config(node, "key"), Config(node, "inputKey"), Config(node, "name"), node.Name, node.Id);
                    string type = FirstNonEmpty(Config(node, "inputType"), Config(node, "type"), "text");
                    bool isFile = IsFileInputType(type) || ParseBool(Config(node, "file"), false) || ParseBool(Config(node, "isFile"), false);
                    return new AgentBuilderInputDefinition {
                        Key = key,
                        Label = FirstNonEmpty(Config(node, "label"), node.Name, key),
                        Type = type,
                        Required = ParseBool(Config(node, "required"), false),
                        IsFileLike = isFile,
                        DefaultValue = Config(node, "default"),
                        SourceNodeId = node.Id
                    };
                })
                .ToList();
        }

        public static AgentBuilderWorkflowValidationResult ValidateWorkflow(AgentBuilderWorkflow workflow) {
            var result = new AgentBuilderWorkflowValidationResult();
            if (workflow == null) {
                AddError(result, "workflow_required", "Workflow is required.");
                return result;
            }

            workflow.Normalize();
            if (workflow.Nodes.Count == 0)
                AddError(result, "nodes_required", "Workflow must contain at least one node.");

            var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AgentBuilderNode node in workflow.Nodes) {
                if (string.IsNullOrWhiteSpace(node.Id)) {
                    AddError(result, "node_id_required", "Every node needs an id.", node.Id);
                    continue;
                }
                if (!nodeIds.Add(node.Id))
                    AddError(result, "duplicate_node_id", "Duplicate node id: " + node.Id, node.Id);

                string type = NormalizeNodeType(node.Type);
                if (type == "input") {
                    string key = FirstNonEmpty(Config(node, "key"), Config(node, "inputKey"), Config(node, "name"), node.Name);
                    if (string.IsNullOrWhiteSpace(key))
                        AddError(result, "input_key_required", "Input node needs a key.", node.Id);
                } else if (type == "agent") {
                    if (string.IsNullOrWhiteSpace(FirstNonEmpty(Config(node, "prompt"), Config(node, "message"), Config(node, "q"))))
                        AddWarning(result, "agent_prompt_empty", "Agent node has no prompt template.", node.Id);
                } else if (type == "reflectionobject") {
                    if (string.IsNullOrWhiteSpace(Config(node, "typeName")))
                        AddError(result, "reflection_type_required", "Reflection object node needs typeName.", node.Id);
                    if (string.IsNullOrWhiteSpace(FirstNonEmpty(Config(node, "objectName"), Config(node, "name"), node.Name)))
                        AddWarning(result, "reflection_object_name_empty", "Reflection object node should have an objectName.", node.Id);
                } else if (type == "reflectioncall" || type == "reflection") {
                    if (string.IsNullOrWhiteSpace(FirstNonEmpty(Config(node, "memberName"), Config(node, "method"), Config(node, "function"))))
                        AddError(result, "reflection_member_required", "Reflection call node needs memberName.", node.Id);
                    if (string.IsNullOrWhiteSpace(FirstNonEmpty(Config(node, "objectName"), Config(node, "typeName"))))
                        AddError(result, "reflection_target_required", "Reflection call node needs objectName or typeName.", node.Id);
                } else if (type == "logicgate" || type == "gate") {
                    if (string.IsNullOrWhiteSpace(FirstNonEmpty(Config(node, "left"), Config(node, "leftRef"), Config(node, "value"))))
                        AddWarning(result, "gate_left_empty", "Logic gate has no left value.", node.Id);
                } else if (type == "url" || type == "urlfetch" || type == "webscrape" || type == "scrape" || type == "crawl") {
                    if (string.IsNullOrWhiteSpace(FirstNonEmpty(Config(node, "url"), Config(node, "uri"))))
                        AddError(result, "url_required", "URL Fetch node needs a URL.", node.Id);
                }
            }

            foreach (AgentBuilderEdge edge in workflow.Edges) {
                if (!nodeIds.Contains(edge.SourceId))
                    AddError(result, "edge_source_missing", "Edge source node does not exist: " + edge.SourceId, edgeId: edge.Id);
                if (!nodeIds.Contains(edge.TargetId))
                    AddError(result, "edge_target_missing", "Edge target node does not exist: " + edge.TargetId, edgeId: edge.Id);
            }

            var inputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AgentBuilderInputDefinition input in GetInputDefinitions(workflow)) {
                if (!inputKeys.Add(input.Key))
                    AddError(result, "duplicate_input_key", "Duplicate input key: " + input.Key, input.SourceNodeId);
            }

            if (!workflow.Nodes.Any(node => IsReturnNodeType(node.Type)))
                AddWarning(result, "return_node_missing", "Workflow has no Return or Output Schema node.");

            foreach (AgentBuilderWorkflowValidationIssue issue in AgentBuilderApplicationValidator.Validate(workflow.Application))
                result.Errors.Add(issue);

            result.Ok = result.Errors.Count == 0;
            return result;
        }

        public static List<AgentBuilderInputDefinition> GetMissingRequiredInputs(IEnumerable<AgentBuilderInputDefinition> definitions, IReadOnlyDictionary<string, object> inputs) {
            var missing = new List<AgentBuilderInputDefinition>();
            inputs ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (AgentBuilderInputDefinition definition in definitions ?? Array.Empty<AgentBuilderInputDefinition>()) {
                if (!definition.Required)
                    continue;
                if (!inputs.TryGetValue(definition.Key, out object value) || IsBlank(value))
                    missing.Add(definition);
            }
            return missing;
        }

        public static AgentBuilderApiValidationResult ValidateApiName(string requestedName, IEnumerable<string> existingApiNames = null) {
            string slug = AgentBuilderSlug.Normalize(requestedName);
            if (string.IsNullOrWhiteSpace(slug))
                return AgentBuilderApiValidationResult.Invalid(slug, "API name is required.");
            if (!Regex.IsMatch(slug, "^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$"))
                return AgentBuilderApiValidationResult.Invalid(slug, "API name must be a URL-safe slug between 3 and 64 characters.");
            if (ReservedApiNames.Contains(slug))
                return AgentBuilderApiValidationResult.Invalid(slug, "API name is reserved by SocketJack.");
            if (existingApiNames != null && existingApiNames.Any(name => string.Equals(AgentBuilderSlug.Normalize(name), slug, StringComparison.OrdinalIgnoreCase)))
                return AgentBuilderApiValidationResult.Invalid(slug, "API name already exists.");
            return AgentBuilderApiValidationResult.Valid(slug);
        }

        public static DateTimeOffset CalculateNextRunUtc(AgentBuilderSchedule schedule, DateTimeOffset nowUtc) {
            if (schedule == null)
                return nowUtc.AddMinutes(5);

            string kind = (schedule.Kind ?? "interval").Trim().ToLowerInvariant();
            if (kind == "daily" || kind == "schedule") {
                if (TimeSpan.TryParse(schedule.TimeOfDay, CultureInfo.InvariantCulture, out TimeSpan time)) {
                    TimeZoneInfo zone = TimeZoneInfo.Utc;
                    if (!string.IsNullOrWhiteSpace(schedule.TimeZone)) {
                        try { zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone.Trim()); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
                    }
                    DateTime localNow = TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime;
                    DateTime localCandidate = DateTime.SpecifyKind(localNow.Date.Add(time), DateTimeKind.Unspecified);
                    if (localCandidate <= localNow)
                        localCandidate = localCandidate.AddDays(1);
                    while (zone.IsInvalidTime(localCandidate))
                        localCandidate = localCandidate.AddMinutes(30);
                    return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localCandidate, zone), TimeSpan.Zero);
                }
            }

            int intervalSeconds = schedule.IntervalSeconds <= 0 ? 300 : Math.Max(30, schedule.IntervalSeconds);
            return nowUtc.AddSeconds(intervalSeconds);
        }

        public static bool EvaluateScheduleCriteria(AgentBuilderSchedule schedule, IReadOnlyDictionary<string, object> values) {
            if (schedule == null || string.IsNullOrWhiteSpace(schedule.CriteriaJson))
                return true;

            try {
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(schedule.CriteriaJson);
                System.Text.Json.JsonElement root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return true;

                string left = JsonText(root, "left", JsonText(root, "leftRef", JsonText(root, "value", "")));
                string op = JsonText(root, "operator", JsonText(root, "op", "truthy"));
                string right = JsonText(root, "right", JsonText(root, "compare", ""));
                var node = new AgentBuilderNode {
                    Id = "schedule_criteria",
                    Type = "logicGate",
                    Config = {
                        ["left"] = left,
                        ["operator"] = op,
                        ["right"] = right
                    }
                };
                return EvaluateGate(node, values ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, AgentBuilderNodeResult>(StringComparer.OrdinalIgnoreCase));
            } catch {
                return false;
            }
        }

        public static string RenderOutputSchemaTemplate(AgentBuilderWorkflow workflow, AgentBuilderExecutionResult result) {
            if (workflow == null || result == null)
                return "";
            workflow.Normalize();
            AgentBuilderNode schema = workflow.Nodes.LastOrDefault(node => NormalizeNodeType(node.Type) == "outputschema" || NormalizeNodeType(node.Type) == "returnschema");
            if (schema == null)
                return "";
            string template = FirstNonEmpty(Config(schema, "template"), Config(schema, "body"), Config(schema, "display"));
            if (string.IsNullOrWhiteSpace(template))
                return "";
            var nodeResults = result.NodeResults.ToDictionary(item => item.NodeId, item => item, StringComparer.OrdinalIgnoreCase);
            var inputs = result.Inputs ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            return ResolveTemplate(template, inputs, nodeResults);
        }

        private static AgentBuilderNodeResult CreateMarkerResult(AgentBuilderNode node, string status, object value) {
            string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            return new AgentBuilderNodeResult {
                NodeId = node.Id,
                NodeType = node.Type,
                NodeName = node.Name,
                Ok = true,
                Status = status,
                Value = value,
                Json = AgentBuilderJson.SafeSerialize(value),
                StartedUtc = now,
                CompletedUtc = now
            };
        }

        private static void AddError(AgentBuilderWorkflowValidationResult result, string code, string message, string nodeId = "", string edgeId = "") {
            result.Errors.Add(new AgentBuilderWorkflowValidationIssue { Code = code, Message = message, NodeId = nodeId ?? "", EdgeId = edgeId ?? "" });
            result.Ok = false;
        }

        private static void AddWarning(AgentBuilderWorkflowValidationResult result, string code, string message, string nodeId = "", string edgeId = "") {
            result.Warnings.Add(new AgentBuilderWorkflowValidationIssue { Code = code, Message = message, NodeId = nodeId ?? "", EdgeId = edgeId ?? "" });
        }

        private static string JsonText(System.Text.Json.JsonElement element, string name, string fallback) {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object || !element.TryGetProperty(name, out System.Text.Json.JsonElement value))
                return fallback;
            if (value.ValueKind == System.Text.Json.JsonValueKind.String)
                return value.GetString() ?? fallback;
            return value.ToString();
        }

        private static List<AgentBuilderNode> TopologicalSort(AgentBuilderWorkflow workflow) {
            var nodes = workflow.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
            var incoming = nodes.Keys.ToDictionary(id => id, id => 0, StringComparer.OrdinalIgnoreCase);
            foreach (AgentBuilderEdge edge in workflow.Edges) {
                if (incoming.ContainsKey(edge.TargetId))
                    incoming[edge.TargetId]++;
            }

            var queue = new Queue<AgentBuilderNode>(workflow.Nodes.Where(node => incoming.TryGetValue(node.Id, out int count) && count == 0));
            var output = new List<AgentBuilderNode>();
            while (queue.Count > 0) {
                AgentBuilderNode node = queue.Dequeue();
                output.Add(node);
                foreach (AgentBuilderEdge edge in workflow.Edges.Where(edge => string.Equals(edge.SourceId, node.Id, StringComparison.OrdinalIgnoreCase))) {
                    if (!incoming.ContainsKey(edge.TargetId))
                        continue;
                    incoming[edge.TargetId]--;
                    if (incoming[edge.TargetId] == 0 && nodes.TryGetValue(edge.TargetId, out AgentBuilderNode next))
                        queue.Enqueue(next);
                }
            }

            if (output.Count != workflow.Nodes.Count)
                return workflow.Nodes;
            return output;
        }

        private static List<AgentBuilderNode> GetParallelChildren(AgentBuilderWorkflow workflow, AgentBuilderNode parallelNode) {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string configured = FirstNonEmpty(Config(parallelNode, "children"), Config(parallelNode, "nodeIds"), Config(parallelNode, "parallelNodeIds"));
            foreach (string part in (configured ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                ids.Add(part.Trim());
            foreach (AgentBuilderEdge edge in workflow.Edges.Where(edge => string.Equals(edge.SourceId, parallelNode.Id, StringComparison.OrdinalIgnoreCase)))
                ids.Add(edge.TargetId);
            return workflow.Nodes.Where(node => ids.Contains(node.Id)).ToList();
        }

        private static bool ShouldSkipByGate(AgentBuilderWorkflow workflow, AgentBuilderNode node, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            foreach (AgentBuilderEdge edge in workflow.Edges.Where(edge => string.Equals(edge.TargetId, node.Id, StringComparison.OrdinalIgnoreCase))) {
                if (!nodeResults.TryGetValue(edge.SourceId, out AgentBuilderNodeResult source))
                    continue;
                if (!IsGateNodeType(source.NodeType))
                    continue;
                bool gateValue = ToBool(source.Value);
                bool wantsFalse = string.Equals(edge.SourcePort, "false", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(edge.SourcePort, "no", StringComparison.OrdinalIgnoreCase);
                if (wantsFalse ? gateValue : !gateValue)
                    return true;
            }

            return false;
        }

        private static object ResolveInputNode(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs) {
            string key = FirstNonEmpty(Config(node, "key"), Config(node, "inputKey"), Config(node, "name"), node.Name, node.Id);
            if (inputs != null && inputs.TryGetValue(key, out object value) && !IsBlank(value))
                return value;
            return Config(node, "default");
        }

        private static object ResolveReturnNode(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            string template = FirstNonEmpty(Config(node, "template"), Config(node, "body"));
            object value;
            if (!string.IsNullOrWhiteSpace(template)) {
                value = IsSimpleExpressionTemplate(template)
                    ? ResolveExpression(template.Trim(), inputs, nodeResults)
                    : ResolveTemplate(template, inputs, nodeResults);
                if (IsBlank(value) && IsSelfReference(template, node.Id))
                    value = LastSuccessfulNodeValue(nodeResults);
            } else {
                string refText = FirstNonEmpty(Config(node, "valueRef"), Config(node, "source"), Config(node, "from"), Config(node, "value"));
                if (!string.IsNullOrWhiteSpace(refText)) {
                    value = ResolveExpression(refText, inputs, nodeResults);
                    if (IsBlank(value) && IsSelfReference(refText, node.Id))
                        value = LastSuccessfulNodeValue(nodeResults);
                } else {
                    value = LastSuccessfulNodeValue(nodeResults);
                }
            }

            string outputType = FirstNonEmpty(Config(node, "outputType"), Config(node, "returnType"), Config(node, "type"), "auto").Trim();
            if (string.IsNullOrWhiteSpace(outputType) || outputType.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return value;

            return new {
                outputType,
                type = outputType,
                mimeType = Config(node, "mimeType"),
                fileName = ResolveTemplate(Config(node, "fileName"), inputs, nodeResults),
                value
            };
        }

        private static object LastSuccessfulNodeValue(IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            AgentBuilderNodeResult last = nodeResults?.Values.LastOrDefault(item => item.Ok && !item.Skipped);
            return last?.Value;
        }

        private static bool IsSimpleExpressionTemplate(string value) =>
            Regex.IsMatch((value ?? "").Trim(), @"^\$[A-Za-z0-9_.:-]+$");

        private static bool IsSelfReference(string value, string nodeId) {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(nodeId))
                return false;
            string trimmed = value.Trim();
            return trimmed.Equals("$" + nodeId, StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("{{" + nodeId + "}}", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("{{ " + nodeId + " }}", StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolvePrompt(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            return ResolveTemplate(FirstNonEmpty(Config(node, "prompt"), Config(node, "message"), Config(node, "q")), inputs, nodeResults);
        }

        public static string ResolveTerminalPrompt(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            string prompt = ResolvePrompt(node, inputs, nodeResults);
            if (!string.IsNullOrWhiteSpace(prompt))
                return prompt;
            string command = ResolveTemplate(FirstNonEmpty(Config(node, "terminalCommand"), Config(node, "command")), inputs, nodeResults);
            return string.IsNullOrWhiteSpace(command)
                ? "Run the terminal command requested by this Builder node only if the selected SocketJack server grants terminal tool permission."
                : "Run this terminal command only if the selected SocketJack server grants terminal tool permission: " + command;
        }

        private static bool HasTerminalIntent(AgentBuilderNode node) {
            return ParseBool(FirstNonEmpty(Config(node, "allowTerminal"), Config(node, "allowTerminalCommands")), false) ||
                   !string.IsNullOrWhiteSpace(FirstNonEmpty(Config(node, "terminalCommand"), Config(node, "command")));
        }

        public static object ResolveConfiguredValue(AgentBuilderNode node, string key, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            return ResolveExpression(Config(node, key), inputs, nodeResults);
        }

        private static object BuildPreparedAgentResult(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            return new {
                ok = true,
                prepared = true,
                mode = FirstNonEmpty(Config(node, "mode"), "text"),
                model = FirstNonEmpty(Config(node, "model"), Config(node, "modelId"), Config(node, "model_id")),
                serverId = FirstNonEmpty(Config(node, "serverId"), Config(node, "server_id"), Config(node, "server")),
                prompt = NormalizeNodeType(node.Type) == "terminal" || NormalizeNodeType(node.Type) == "terminalcommand" || NormalizeNodeType(node.Type) == "command"
                    ? ResolveTerminalPrompt(node, inputs, nodeResults)
                    : ResolvePrompt(node, inputs, nodeResults),
                terminal = new {
                    requested = HasTerminalIntent(node),
                    command = ResolveTemplate(FirstNonEmpty(Config(node, "terminalCommand"), Config(node, "command")), inputs, nodeResults),
                    shell = FirstNonEmpty(Config(node, "terminalShell"), Config(node, "shell"), "powershell"),
                    workingDirectory = ResolveTemplate(FirstNonEmpty(Config(node, "terminalWorkingDirectory"), Config(node, "workingDirectory")), inputs, nodeResults)
                }
            };
        }

        private static object BuildScheduleMarker(AgentBuilderNode node) {
            return new {
                ok = true,
                kind = FirstNonEmpty(Config(node, "kind"), Config(node, "scheduleKind"), "interval"),
                intervalSeconds = ParseInt(Config(node, "intervalSeconds"), 300),
                timeOfDay = Config(node, "timeOfDay"),
                criteria = Config(node, "criteria")
            };
        }

        private static bool EvaluateGate(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            object left = ResolveExpression(FirstNonEmpty(Config(node, "leftRef"), Config(node, "left"), Config(node, "value")), inputs, nodeResults);
            object right = ResolveExpression(FirstNonEmpty(Config(node, "rightRef"), Config(node, "right"), Config(node, "compare")), inputs, nodeResults);
            string op = FirstNonEmpty(Config(node, "operator"), Config(node, "op"), "truthy").Trim().ToLowerInvariant();

            switch (op) {
                case "exists":
                case "truthy":
                    return !IsBlank(left) && ToBool(left);
                case "missing":
                case "empty":
                    return IsBlank(left);
                case "equals":
                case "==":
                case "eq":
                    return string.Equals(ValueText(left), ValueText(right), StringComparison.OrdinalIgnoreCase);
                case "not_equals":
                case "!=":
                case "neq":
                    return !string.Equals(ValueText(left), ValueText(right), StringComparison.OrdinalIgnoreCase);
                case "contains":
                    return ValueText(left).IndexOf(ValueText(right), StringComparison.OrdinalIgnoreCase) >= 0;
                case "greater":
                case ">":
                    return ToDecimal(left) > ToDecimal(right);
                case "greater_or_equal":
                case ">=":
                    return ToDecimal(left) >= ToDecimal(right);
                case "less":
                case "<":
                    return ToDecimal(left) < ToDecimal(right);
                case "less_or_equal":
                case "<=":
                    return ToDecimal(left) <= ToDecimal(right);
                case "and":
                    return ToBool(left) && ToBool(right);
                case "or":
                    return ToBool(left) || ToBool(right);
                default:
                    return ToBool(left);
            }
        }

        private static object ResolveExpression(string expression, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            expression ??= "";
            if (expression.IndexOf("{{", StringComparison.Ordinal) >= 0)
                return ResolveTemplate(expression, inputs, nodeResults);
            if (!expression.StartsWith("$", StringComparison.Ordinal))
                return expression;

            string path = expression.Substring(1);
            if (path.StartsWith("input.", StringComparison.OrdinalIgnoreCase)) {
                string key = path.Substring("input.".Length);
                return inputs != null && inputs.TryGetValue(key, out object inputValue) ? inputValue : null;
            }

            string nodeId = path;
            string member = "";
            int dot = path.IndexOf('.');
            if (dot > 0) {
                nodeId = path.Substring(0, dot);
                member = path.Substring(dot + 1);
            }

            if (nodeResults == null || !nodeResults.TryGetValue(nodeId, out AgentBuilderNodeResult result))
                return null;
            if (string.IsNullOrWhiteSpace(member) || member.Equals("value", StringComparison.OrdinalIgnoreCase))
                return result.Value;
            if (member.Equals("json", StringComparison.OrdinalIgnoreCase))
                return result.Json;
            if (member.Equals("error", StringComparison.OrdinalIgnoreCase))
                return result.Error;

            return result.Value;
        }

        private static string ResolveTemplate(string template, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            template ??= "";
            return TemplateToken.Replace(template, match => {
                string key = match.Groups[1].Value;
                object value = ResolveExpression("$" + (key.StartsWith("input.", StringComparison.OrdinalIgnoreCase) ? key : key), inputs, nodeResults);
                if (value == null && inputs != null && inputs.TryGetValue(key, out object inputValue))
                    value = inputValue;
                return ValueText(value);
            });
        }

        private static bool TryGetStructuredFailure(object value, out string error) {
            error = "";
            if (value == null)
                return false;

            bool failed = false;
            if (TryGetBoolMember(value, "ok", out bool ok))
                failed = !ok;
            else if (TryGetBoolMember(value, "success", out bool success))
                failed = !success;

            if (!failed)
                return false;

            TryGetMemberValue(value, "response", out object response);
            string status = FirstNonEmpty(TryGetTextMember(value, "statusCode"), TryGetTextMember(value, "status"));
            string reason = FirstNonEmpty(TryGetTextMember(value, "reason"), TryGetTextMember(response, "reason"));
            string message = FirstNonEmpty(
                TryGetTextMember(value, "error"),
                TryGetTextMember(response, "error"),
                TryGetTextMember(value, "message"),
                TryGetTextMember(response, "message"),
                reason);
            error = FirstNonEmpty(message, "Node returned ok=false.");
            if (!string.IsNullOrWhiteSpace(status) && !error.Contains(status, StringComparison.OrdinalIgnoreCase))
                error = status + (string.IsNullOrWhiteSpace(reason) || error.Contains(reason, StringComparison.OrdinalIgnoreCase) ? "" : " " + reason) + ": " + error;
            return true;
        }

        private static bool TryGetBoolMember(object value, string name, out bool result) {
            result = false;
            if (!TryGetMemberValue(value, name, out object member) || member == null)
                return false;
            if (member is bool b) {
                result = b;
                return true;
            }
            string text = TryGetText(member).Trim();
            if (bool.TryParse(text, out bool parsed)) {
                result = parsed;
                return true;
            }
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)) {
                result = number != 0m;
                return true;
            }
            return false;
        }

        private static string TryGetTextMember(object value, string name) =>
            TryGetMemberValue(value, name, out object member) ? TryGetText(member) : "";

        private static bool TryGetMemberValue(object value, string name, out object member) {
            member = null;
            if (value == null || string.IsNullOrWhiteSpace(name))
                return false;

            if (value is System.Text.Json.Nodes.JsonObject jsonObject) {
                foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode> pair in jsonObject) {
                    if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) {
                        member = pair.Value;
                        return true;
                    }
                }
                return false;
            }

            if (value is IReadOnlyDictionary<string, object> readOnlyDictionary) {
                foreach (KeyValuePair<string, object> pair in readOnlyDictionary) {
                    if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) {
                        member = pair.Value;
                        return true;
                    }
                }
                return false;
            }

            if (value is IDictionary<string, object> dictionary) {
                foreach (KeyValuePair<string, object> pair in dictionary) {
                    if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) {
                        member = pair.Value;
                        return true;
                    }
                }
                return false;
            }

            System.Reflection.PropertyInfo property = value.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase);
            if (property == null)
                return false;
            member = property.GetValue(value);
            return true;
        }

        private static string TryGetText(object value) {
            if (value == null)
                return "";
            if (value is string text)
                return text;
            if (value is System.Text.Json.Nodes.JsonValue jsonValue) {
                if (jsonValue.TryGetValue<string>(out string jsonText))
                    return jsonText ?? "";
                return jsonValue.ToString();
            }
            if (value is System.Text.Json.Nodes.JsonNode jsonNode)
                return jsonNode.ToJsonString();
            return ValueText(value);
        }

        private static string Config(AgentBuilderNode node, string key) {
            if (node?.Config != null && node.Config.TryGetValue(key, out string value))
                return value ?? "";
            return "";
        }

        private static string FirstNonEmpty(params string[] values) {
            foreach (string value in values) {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return "";
        }

        private static bool IsParallelNode(AgentBuilderNode node) => NormalizeNodeType(node.Type) == "parallel";
        private static bool IsAgentLikeNodeType(string normalizedType) =>
            normalizedType == "agent" ||
            normalizedType == "terminal" ||
            normalizedType == "terminalcommand" ||
            normalizedType == "command";
        private static bool IsGateNodeType(string type) => NormalizeNodeType(type) == "logicgate" || NormalizeNodeType(type) == "gate";
        private static bool IsReturnNodeType(string type) {
            string normalized = NormalizeNodeType(type);
            return normalized == "return" || normalized == "output" || normalized == "outputschema" || normalized == "returnschema";
        }

        private static string NormalizeNodeType(string value) => Regex.Replace((value ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "");

        private static bool IsFileInputType(string type) {
            type = (type ?? "").Trim().ToLowerInvariant();
            return type == "file" || type == "upload" || type == "binary" || type == "bytes" || type.Contains("file");
        }

        private static bool ParseBool(string value, bool fallback) {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            if (bool.TryParse(value, out bool parsed))
                return parsed;
            value = value.Trim();
            return value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseInt(string value, int fallback) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

        private static bool IsBlank(object value) {
            if (value == null)
                return true;
            if (value is string text)
                return string.IsNullOrWhiteSpace(text);
            if (value is Array array)
                return array.Length == 0;
            return false;
        }

        private static bool ToBool(object value) {
            if (value == null)
                return false;
            if (value is bool b)
                return b;
            string text = ValueText(value).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (bool.TryParse(text, out bool parsed))
                return parsed;
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
                return number != 0m;
            return !text.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                   !text.Equals("no", StringComparison.OrdinalIgnoreCase) &&
                   !text.Equals("off", StringComparison.OrdinalIgnoreCase);
        }

        private static decimal ToDecimal(object value) {
            if (value == null)
                return 0m;
            if (value is IConvertible convertible) {
                try {
                    return convertible.ToDecimal(CultureInfo.InvariantCulture);
                } catch {
                }
            }
            return decimal.TryParse(ValueText(value), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed) ? parsed : 0m;
        }

        private static string ValueText(object value) {
            if (value == null)
                return "";
            if (value is string text)
                return text;
            if (value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString() ?? "";
        }

        public static readonly HashSet<string> ReservedApiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "agentbuilder",
            "web-auth",
            "socketjack",
            "auto",
            "heirowllm",
            "heirowllm",
            "issues",
            "shell",
            "socketjack-com",
            "healthz",
            "sql",
            "download",
            "update"
        };
    }

    public sealed class AgentBuilderApiValidationResult {
        public bool Ok { get; set; }
        public string Slug { get; set; } = "";
        public string Error { get; set; } = "";

        public static AgentBuilderApiValidationResult Valid(string slug) => new AgentBuilderApiValidationResult { Ok = true, Slug = slug };
        public static AgentBuilderApiValidationResult Invalid(string slug, string error) => new AgentBuilderApiValidationResult { Ok = false, Slug = slug, Error = error };
    }
}
