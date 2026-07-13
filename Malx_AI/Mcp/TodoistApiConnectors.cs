using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Mcp
{
    /// <summary>
    /// Todoist REST API v1 tools for cloud agents.
    /// Base: https://api.todoist.com/api/v1
    /// </summary>
    internal static class TodoistApiConnectors
    {
        private const string Api = "https://api.todoist.com/api/v1";
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Axiom-MCP/1.0");
            return client;
        }

        public static IReadOnlyList<McpToolDefinition> BuildTools(string connectorId)
        {
            JsonObject S(string d) => new() { ["type"] = "string", ["description"] = d };
            JsonObject I(string d) => new() { ["type"] = "integer", ["description"] = d };

            McpToolDefinition T(string name, string desc, JsonObject props, params string[] req)
            {
                var schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["additionalProperties"] = false
                };
                if (req.Length > 0)
                {
                    var arr = new JsonArray();
                    foreach (string r in req) arr.Add(r);
                    schema["required"] = arr;
                }
                return new McpToolDefinition
                {
                    Name = name,
                    ConnectorId = connectorId,
                    Description = desc,
                    ParametersSchema = schema
                };
            }

            JsonObject P(params (string k, JsonObject v)[] items)
            {
                var o = new JsonObject();
                foreach (var (k, v) in items) o[k] = v;
                return o;
            }

            return
            [
                T("todoist_whoami", "Get the authenticated Todoist user (name, email, plan).", P()),
                T("todoist_list_projects", "List all projects.", P()),
                T("todoist_get_project", "Get one project by id.",
                    P(("project_id", S("Project id"))), "project_id"),
                T("todoist_create_project", "Create a project.",
                    P(("name", S("Project name")), ("color", S("Optional color name e.g. berry_red")),
                      ("is_favorite", S("true/false"))), "name"),
                T("todoist_update_project", "Update a project name/color/favorite.",
                    P(("project_id", S("Project id")), ("name", S("Optional new name")),
                      ("color", S("Optional color")), ("is_favorite", S("true/false"))), "project_id"),
                T("todoist_delete_project", "Delete a project.",
                    P(("project_id", S("Project id"))), "project_id"),
                T("todoist_list_tasks", "List active tasks. Optional project_id or filter (Todoist filter syntax, e.g. today, overdue, p1).",
                    P(("project_id", S("Optional project id")),
                      ("filter", S("Optional filter string e.g. today | overdue | #Work")),
                      ("max_results", I("1-50, default 30")))),
                T("todoist_get_task", "Get one task by id.",
                    P(("task_id", S("Task id"))), "task_id"),
                T("todoist_create_task", "Create a task. Prefer natural due_string (e.g. tomorrow 5pm, next Monday).",
                    P(("content", S("Task title/text")),
                      ("description", S("Optional description")),
                      ("project_id", S("Optional project id (default Inbox)")),
                      ("section_id", S("Optional section id")),
                      ("due_string", S("Natural language due date")),
                      ("due_date", S("Optional YYYY-MM-DD")),
                      ("priority", S("1-4 where 4 is highest urgency in Todoist API")),
                      ("labels", S("Optional comma-separated label names"))), "content"),
                T("todoist_quick_add", "Quick-add a task using Todoist natural language (supports #project @label p1 due phrases).",
                    P(("text", S("Full quick-add string e.g. Buy milk tomorrow #Shopping p1"))), "text"),
                T("todoist_update_task", "Update a task's content, description, due date, priority, or labels.",
                    P(("task_id", S("Task id")),
                      ("content", S("Optional new title")),
                      ("description", S("Optional description")),
                      ("due_string", S("Optional natural due date")),
                      ("due_date", S("Optional YYYY-MM-DD")),
                      ("priority", S("1-4")),
                      ("labels", S("Optional comma-separated labels"))), "task_id"),
                T("todoist_complete_task", "Mark a task complete (close).",
                    P(("task_id", S("Task id"))), "task_id"),
                T("todoist_reopen_task", "Reopen a completed task.",
                    P(("task_id", S("Task id"))), "task_id"),
                T("todoist_delete_task", "Delete a task permanently.",
                    P(("task_id", S("Task id"))), "task_id"),
                T("todoist_list_sections", "List sections in a project.",
                    P(("project_id", S("Project id"))), "project_id"),
                T("todoist_create_section", "Create a section in a project.",
                    P(("project_id", S("Project id")), ("name", S("Section name"))), "project_id", "name"),
                T("todoist_list_labels", "List personal labels.", P()),
                T("todoist_create_label", "Create a personal label.",
                    P(("name", S("Label name")), ("color", S("Optional color"))), "name"),
                T("todoist_list_comments", "List comments on a task or project.",
                    P(("task_id", S("Task id (or leave empty if using project_id)")),
                      ("project_id", S("Project id (or leave empty if using task_id)")))),
                T("todoist_add_comment", "Add a comment on a task or project.",
                    P(("content", S("Comment text")),
                      ("task_id", S("Task id XOR project_id")),
                      ("project_id", S("Project id XOR task_id"))), "content")
            ];
        }

        public static async Task<string> ExecuteAsync(string toolName, JsonElement args, string accessToken, CancellationToken token)
        {
            string name = toolName.Trim();

            if (Eq(name, "todoist_whoami"))
            {
                using JsonDocument doc = await GetAsync("/user", accessToken, token).ConfigureAwait(false);
                return $"full_name={Prop(doc, "full_name")} email={Prop(doc, "email")} id={Prop(doc, "id")} karma={Prop(doc, "karma")}";
            }

            if (Eq(name, "todoist_list_projects"))
            {
                using JsonDocument doc = await GetAsync("/projects", accessToken, token).ConfigureAwait(false);
                return FormatProjects(doc.RootElement);
            }

            if (Eq(name, "todoist_get_project"))
            {
                string id = Req(args, "project_id", out string? err); if (err != null) return err;
                using JsonDocument doc = await GetAsync("/projects/" + Uri.EscapeDataString(id), accessToken, token).ConfigureAwait(false);
                return FormatProject(doc.RootElement);
            }

            if (Eq(name, "todoist_create_project"))
            {
                string projectName = Req(args, "name", out string? err); if (err != null) return err;
                var body = new JsonObject { ["name"] = projectName };
                if (!string.IsNullOrWhiteSpace(G(args, "color"))) body["color"] = G(args, "color");
                if (IsTruthy(G(args, "is_favorite"))) body["is_favorite"] = true;
                using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, "/projects", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return "Project created.\n" + FormatProject(doc.RootElement);
            }

            if (Eq(name, "todoist_update_project"))
            {
                string id = Req(args, "project_id", out string? err); if (err != null) return err;
                var body = new JsonObject();
                if (!string.IsNullOrWhiteSpace(G(args, "name"))) body["name"] = G(args, "name");
                if (!string.IsNullOrWhiteSpace(G(args, "color"))) body["color"] = G(args, "color");
                if (!string.IsNullOrWhiteSpace(G(args, "is_favorite"))) body["is_favorite"] = IsTruthy(G(args, "is_favorite"));
                using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, "/projects/" + Uri.EscapeDataString(id), accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return "Project updated.\n" + FormatProject(doc.RootElement);
            }

            if (Eq(name, "todoist_delete_project"))
            {
                string id = Req(args, "project_id", out string? err); if (err != null) return err;
                await SendJsonAsync(HttpMethod.Delete, "/projects/" + Uri.EscapeDataString(id), accessToken, null, token, allowEmpty: true).ConfigureAwait(false);
                return $"Project {id} deleted.";
            }

            if (Eq(name, "todoist_list_tasks"))
            {
                int max = Clamp(Gi(args, "max_results", 30), 1, 50);
                string filter = G(args, "filter");
                string projectId = G(args, "project_id");
                string path;
                if (!string.IsNullOrWhiteSpace(filter))
                    path = "/tasks/filter?query=" + Uri.EscapeDataString(filter);
                else if (!string.IsNullOrWhiteSpace(projectId))
                    path = "/tasks?project_id=" + Uri.EscapeDataString(projectId);
                else
                    path = "/tasks";

                using JsonDocument doc = await GetAsync(path, accessToken, token).ConfigureAwait(false);
                return FormatTasks(doc.RootElement, max);
            }

            if (Eq(name, "todoist_get_task"))
            {
                string id = Req(args, "task_id", out string? err); if (err != null) return err;
                using JsonDocument doc = await GetAsync("/tasks/" + Uri.EscapeDataString(id), accessToken, token).ConfigureAwait(false);
                return FormatTask(doc.RootElement);
            }

            if (Eq(name, "todoist_create_task"))
            {
                string content = Req(args, "content", out string? err); if (err != null) return err;
                var body = new JsonObject { ["content"] = content };
                if (!string.IsNullOrWhiteSpace(G(args, "description"))) body["description"] = G(args, "description");
                if (!string.IsNullOrWhiteSpace(G(args, "project_id"))) body["project_id"] = G(args, "project_id");
                if (!string.IsNullOrWhiteSpace(G(args, "section_id"))) body["section_id"] = G(args, "section_id");
                if (!string.IsNullOrWhiteSpace(G(args, "due_string"))) body["due_string"] = G(args, "due_string");
                if (!string.IsNullOrWhiteSpace(G(args, "due_date"))) body["due_date"] = G(args, "due_date");
                if (int.TryParse(G(args, "priority"), out int pr) && pr is >= 1 and <= 4) body["priority"] = pr;
                AddLabels(body, G(args, "labels"));
                using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, "/tasks", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return "Task created.\n" + FormatTask(doc.RootElement);
            }

            if (Eq(name, "todoist_quick_add"))
            {
                string text = Req(args, "text", out string? err); if (err != null) return err;
                var body = new JsonObject { ["text"] = text };
                using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, "/tasks/quick", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return "Quick-added.\n" + FormatTask(doc.RootElement);
            }

            if (Eq(name, "todoist_update_task"))
            {
                string id = Req(args, "task_id", out string? err); if (err != null) return err;
                var body = new JsonObject();
                if (!string.IsNullOrWhiteSpace(G(args, "content"))) body["content"] = G(args, "content");
                if (!string.IsNullOrWhiteSpace(G(args, "description"))) body["description"] = G(args, "description");
                if (!string.IsNullOrWhiteSpace(G(args, "due_string"))) body["due_string"] = G(args, "due_string");
                if (!string.IsNullOrWhiteSpace(G(args, "due_date"))) body["due_date"] = G(args, "due_date");
                if (int.TryParse(G(args, "priority"), out int pr) && pr is >= 1 and <= 4) body["priority"] = pr;
                if (!string.IsNullOrWhiteSpace(G(args, "labels"))) AddLabels(body, G(args, "labels"));
                using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, "/tasks/" + Uri.EscapeDataString(id), accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return "Task updated.\n" + FormatTask(doc.RootElement);
            }

            if (Eq(name, "todoist_complete_task"))
            {
                string id = Req(args, "task_id", out string? err); if (err != null) return err;
                await SendJsonAsync(HttpMethod.Post, "/tasks/" + Uri.EscapeDataString(id) + "/close", accessToken, "{}", token, allowEmpty: true).ConfigureAwait(false);
                return $"Task {id} completed.";
            }

            if (Eq(name, "todoist_reopen_task"))
            {
                string id = Req(args, "task_id", out string? err); if (err != null) return err;
                await SendJsonAsync(HttpMethod.Post, "/tasks/" + Uri.EscapeDataString(id) + "/reopen", accessToken, "{}", token, allowEmpty: true).ConfigureAwait(false);
                return $"Task {id} reopened.";
            }

            if (Eq(name, "todoist_delete_task"))
            {
                string id = Req(args, "task_id", out string? err); if (err != null) return err;
                await SendJsonAsync(HttpMethod.Delete, "/tasks/" + Uri.EscapeDataString(id), accessToken, null, token, allowEmpty: true).ConfigureAwait(false);
                return $"Task {id} deleted.";
            }

            if (Eq(name, "todoist_list_sections"))
            {
                string projectId = Req(args, "project_id", out string? err); if (err != null) return err;
                using JsonDocument doc = await GetAsync("/sections?project_id=" + Uri.EscapeDataString(projectId), accessToken, token).ConfigureAwait(false);
                return FormatSections(doc.RootElement);
            }

            if (Eq(name, "todoist_create_section"))
            {
                string projectId = Req(args, "project_id", out string? err); if (err != null) return err;
                string sectionName = Req(args, "name", out err); if (err != null) return err;
                var body = new JsonObject { ["project_id"] = projectId, ["name"] = sectionName };
                using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, "/sections", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return $"Section created id={Prop(doc, "id")} name={Prop(doc, "name")}";
            }

            if (Eq(name, "todoist_list_labels"))
            {
                using JsonDocument doc = await GetAsync("/labels", accessToken, token).ConfigureAwait(false);
                return FormatLabels(doc.RootElement);
            }

            if (Eq(name, "todoist_create_label"))
            {
                string labelName = Req(args, "name", out string? err); if (err != null) return err;
                var body = new JsonObject { ["name"] = labelName };
                if (!string.IsNullOrWhiteSpace(G(args, "color"))) body["color"] = G(args, "color");
                using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, "/labels", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return $"Label created id={Prop(doc, "id")} name={Prop(doc, "name")}";
            }

            if (Eq(name, "todoist_list_comments"))
            {
                string taskId = G(args, "task_id");
                string projectId = G(args, "project_id");
                string path;
                if (!string.IsNullOrWhiteSpace(taskId))
                    path = "/comments?task_id=" + Uri.EscapeDataString(taskId);
                else if (!string.IsNullOrWhiteSpace(projectId))
                    path = "/comments?project_id=" + Uri.EscapeDataString(projectId);
                else
                    return "Provide task_id or project_id.";
                using JsonDocument doc = await GetAsync(path, accessToken, token).ConfigureAwait(false);
                return FormatComments(doc.RootElement);
            }

            if (Eq(name, "todoist_add_comment"))
            {
                string content = Req(args, "content", out string? err); if (err != null) return err;
                string taskId = G(args, "task_id");
                string projectId = G(args, "project_id");
                var body = new JsonObject { ["content"] = content };
                if (!string.IsNullOrWhiteSpace(taskId)) body["task_id"] = taskId;
                else if (!string.IsNullOrWhiteSpace(projectId)) body["project_id"] = projectId;
                else return "Provide task_id or project_id.";
                using JsonDocument doc = await SendJsonAsync(HttpMethod.Post, "/comments", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return $"Comment added id={Prop(doc, "id")}";
            }

            return $"Unsupported Todoist tool: {toolName}";
        }

        // ── HTTP ────────────────────────────────────────────────────────────

        private static async Task<JsonDocument> GetAsync(string path, string accessToken, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
            Auth(request, accessToken);
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Todoist API {(int)response.StatusCode}: {Truncate(body, 500)}");
            return JsonDocument.Parse(NormalizeArrayOrObject(body));
        }

        private static async Task<JsonDocument> SendJsonAsync(
            HttpMethod method,
            string path,
            string accessToken,
            string? json,
            CancellationToken token,
            bool allowEmpty = false)
        {
            using var request = new HttpRequestMessage(method, Api + path);
            Auth(request, accessToken);
            if (json != null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Todoist API {(int)response.StatusCode}: {Truncate(body, 500)}");
            if (allowEmpty && string.IsNullOrWhiteSpace(body))
                return JsonDocument.Parse("{}");
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : NormalizeArrayOrObject(body));
        }

        private static void Auth(HttpRequestMessage request, string accessToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        /// <summary>Some list endpoints return a bare array; wrap for uniform Prop helpers when needed.</summary>
        private static string NormalizeArrayOrObject(string body)
        {
            string trimmed = body.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
                return "{\"items\":" + body + "}";
            return body;
        }

        private static void AddLabels(JsonObject body, string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return;
            var arr = new JsonArray();
            foreach (string part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                arr.Add(part);
            body["labels"] = arr;
        }

        // ── Format ──────────────────────────────────────────────────────────

        private static IEnumerable<JsonElement> Items(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Array)
                return root.EnumerateArray();
            if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                return items.EnumerateArray();
            if (root.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
                return results.EnumerateArray();
            return Array.Empty<JsonElement>();
        }

        private static string FormatProjects(JsonElement root)
        {
            var sb = new StringBuilder("Projects:");
            int i = 0;
            foreach (JsonElement p in Items(root))
            {
                i++;
                sb.AppendLine();
                sb.AppendLine($"{i}. {FormatProject(p)}");
            }
            return i == 0 ? "No projects." : sb.ToString();
        }

        private static string FormatProject(JsonElement p)
            => $"id={Prop(p, "id")} name={Prop(p, "name")} favorite={Prop(p, "is_favorite")} color={Prop(p, "color")} url={Prop(p, "url")}";

        private static string FormatTasks(JsonElement root, int max)
        {
            var sb = new StringBuilder("Tasks:");
            int i = 0;
            foreach (JsonElement t in Items(root))
            {
                if (i >= max) break;
                i++;
                sb.AppendLine();
                sb.AppendLine($"{i}. {FormatTask(t)}");
            }
            return i == 0 ? "No tasks." : Bound(sb.ToString(), 14000);
        }

        private static string FormatTask(JsonElement t)
        {
            string due = "";
            if (t.TryGetProperty("due", out JsonElement d) && d.ValueKind == JsonValueKind.Object)
                due = $" due={Prop(d, "string") ?? Prop(d, "date")}";
            string labels = "";
            if (t.TryGetProperty("labels", out JsonElement labs) && labs.ValueKind == JsonValueKind.Array)
                labels = " labels=[" + string.Join(",", labs.EnumerateArray().Select(x => x.GetString())) + "]";
            return $"id={Prop(t, "id")} [{(Prop(t, "is_completed") == "true" ? "done" : "open")}] p{Prop(t, "priority")} {Prop(t, "content")}{due}{labels} project={Prop(t, "project_id")}";
        }

        private static string FormatSections(JsonElement root)
        {
            var sb = new StringBuilder("Sections:");
            int i = 0;
            foreach (JsonElement s in Items(root))
            {
                i++;
                sb.AppendLine();
                sb.AppendLine($"{i}. id={Prop(s, "id")} name={Prop(s, "name")} project={Prop(s, "project_id")}");
            }
            return i == 0 ? "No sections." : sb.ToString();
        }

        private static string FormatLabels(JsonElement root)
        {
            var sb = new StringBuilder("Labels:");
            int i = 0;
            foreach (JsonElement l in Items(root))
            {
                i++;
                sb.AppendLine();
                sb.AppendLine($"{i}. id={Prop(l, "id")} name={Prop(l, "name")} color={Prop(l, "color")}");
            }
            return i == 0 ? "No labels." : sb.ToString();
        }

        private static string FormatComments(JsonElement root)
        {
            var sb = new StringBuilder("Comments:");
            int i = 0;
            foreach (JsonElement c in Items(root))
            {
                i++;
                sb.AppendLine();
                sb.AppendLine($"{i}. id={Prop(c, "id")} {Bound(Prop(c, "content") ?? "", 200)}");
            }
            return i == 0 ? "No comments." : sb.ToString();
        }

        private static string? Prop(JsonDocument doc, string name) => Prop(doc.RootElement, name);

        private static string? Prop(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out JsonElement p))
                return null;
            return p.ValueKind switch
            {
                JsonValueKind.String => p.GetString(),
                JsonValueKind.Number => p.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => p.ToString()
            };
        }

        private static string G(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement el))
                return "";
            return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString();
        }

        private static int Gi(JsonElement args, string name, int def)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement el)) return def;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n)) return n;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int p)) return p;
            return def;
        }

        private static string Req(JsonElement args, string name, out string? error)
        {
            string v = G(args, name).Trim();
            if (string.IsNullOrWhiteSpace(v)) { error = $"{name} is required."; return ""; }
            error = null;
            return v;
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool IsTruthy(string v) => v is "true" or "1" or "yes" or "True";
        private static int Clamp(int v, int min, int max) => Math.Min(max, Math.Max(min, v));
        private static string Bound(string t, int max) => string.IsNullOrEmpty(t) || t.Length <= max ? t ?? "" : t[..max] + "\n…[truncated]";
        private static string Truncate(string t, int max) => string.IsNullOrEmpty(t) || t.Length <= max ? t ?? "" : t[..max] + "…";
    }
}
