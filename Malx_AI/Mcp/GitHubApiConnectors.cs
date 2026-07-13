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
    /// Full practical GitHub REST tool surface for cloud / council agents.
    /// </summary>
    internal static class GitHubApiConnectors
    {
        private const string Api = "https://api.github.com";
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Axiom-MCP/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
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

            JsonObject Props(params (string k, JsonObject v)[] items)
            {
                var o = new JsonObject();
                foreach (var (k, v) in items) o[k] = v;
                return o;
            }

            return
            [
                T("github_whoami", "Get the authenticated GitHub user (login, name, email, plan).", Props()),
                T("github_list_repos", "List repositories for the authenticated user (or a specified owner/org).",
                    Props(("owner", S("Optional owner/org login; default = authenticated user")),
                          ("type", S("all|owner|member|public|private — default all")),
                          ("max_results", I("1-50, default 20")))),
                T("github_get_repo", "Get repository metadata (default branch, description, visibility, topics).",
                    Props(("owner", S("Repo owner")), ("repo", S("Repo name"))), "owner", "repo"),
                T("github_list_branches", "List branches for a repository.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("max_results", I("1-50, default 30"))), "owner", "repo"),
                T("github_get_file", "Get a file's decoded text content and sha from a repo path (branch/ref optional).",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("path", S("File path in repo")),
                          ("ref", S("Branch, tag, or commit SHA — default default branch"))), "owner", "repo", "path"),
                T("github_list_directory", "List files and folders at a path in the repo.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("path", S("Directory path; empty = root")),
                          ("ref", S("Optional ref"))), "owner", "repo"),
                T("github_search_code", "Search code across GitHub (or within a repo via repo:owner/name in query).",
                    Props(("query", S("GitHub code search query")), ("max_results", I("1-30, default 15"))), "query"),
                T("github_search_issues", "Search issues and PRs (is:issue, is:pr, repo:owner/name, author:, etc.).",
                    Props(("query", S("GitHub issues search query")), ("max_results", I("1-30, default 15"))), "query"),
                T("github_search_repos", "Search repositories by name/topic/language.",
                    Props(("query", S("Repo search query")), ("max_results", I("1-30, default 15"))), "query"),
                T("github_list_issues", "List issues in a repo (state: open|closed|all).",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("state", S("open|closed|all")),
                          ("labels", S("Optional comma-separated labels")), ("max_results", I("1-50, default 20"))), "owner", "repo"),
                T("github_get_issue", "Get one issue or PR by number (title, body, labels, state, comments count).",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("number", I("Issue or PR number"))), "owner", "repo", "number"),
                T("github_create_issue", "Create an issue.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("title", S("Title")), ("body", S("Markdown body")),
                          ("labels", S("Optional comma-separated labels")), ("assignees", S("Optional comma-separated logins"))), "owner", "repo", "title"),
                T("github_update_issue", "Update an issue (title, body, state open/closed, labels).",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("number", I("Issue number")),
                          ("title", S("Optional new title")), ("body", S("Optional new body")),
                          ("state", S("open or closed")), ("labels", S("Optional comma-separated labels replacing set"))), "owner", "repo", "number"),
                T("github_add_issue_comment", "Comment on an issue or PR.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("number", I("Issue/PR number")), ("body", S("Comment markdown"))), "owner", "repo", "number", "body"),
                T("github_list_pulls", "List pull requests (state: open|closed|all).",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("state", S("open|closed|all")), ("max_results", I("1-50, default 20"))), "owner", "repo"),
                T("github_get_pull", "Get a pull request by number.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("number", I("PR number"))), "owner", "repo", "number"),
                T("github_list_pull_files", "List files changed in a pull request.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("number", I("PR number")), ("max_results", I("1-100, default 40"))), "owner", "repo", "number"),
                T("github_create_pull", "Create a pull request from head branch into base branch.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("title", S("PR title")), ("head", S("Head branch (or user:branch)")),
                          ("base", S("Base branch")), ("body", S("Optional markdown body")), ("draft", S("true/false"))), "owner", "repo", "title", "head", "base"),
                T("github_merge_pull", "Merge a pull request (merge|squash|rebase).",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("number", I("PR number")),
                          ("merge_method", S("merge|squash|rebase")), ("commit_title", S("Optional"))), "owner", "repo", "number"),
                T("github_list_commits", "List commits on a branch/path.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("sha", S("Branch or SHA")), ("path", S("Optional path filter")),
                          ("max_results", I("1-50, default 20"))), "owner", "repo"),
                T("github_get_commit", "Get a commit with message and file list.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("ref", S("Commit SHA"))), "owner", "repo", "ref"),
                T("github_compare", "Compare two commits/branches (base...head).",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("base", S("Base ref")), ("head", S("Head ref"))), "owner", "repo", "base", "head"),
                T("github_create_branch", "Create a branch from an existing sha or branch name.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("branch", S("New branch name")),
                          ("from_ref", S("Source branch or SHA (default: default branch)"))), "owner", "repo", "branch"),
                T("github_create_or_update_file", "Create or update a text file via the Contents API (commits to branch).",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("path", S("File path")), ("content", S("Full file text")),
                          ("message", S("Commit message")), ("branch", S("Target branch")),
                          ("sha", S("Required when updating existing file — from github_get_file"))), "owner", "repo", "path", "content", "message"),
                T("github_delete_file", "Delete a file from a branch (requires sha from github_get_file).",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("path", S("File path")), ("message", S("Commit message")),
                          ("sha", S("Blob sha")), ("branch", S("Branch"))), "owner", "repo", "path", "message", "sha"),
                T("github_list_releases", "List releases for a repo.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("max_results", I("1-30, default 10"))), "owner", "repo"),
                T("github_list_workflows", "List GitHub Actions workflows.",
                    Props(("owner", S("Owner")), ("repo", S("Repo"))), "owner", "repo"),
                T("github_list_workflow_runs", "List recent workflow runs.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("workflow_id", S("Optional workflow id or filename")),
                          ("max_results", I("1-30, default 10"))), "owner", "repo"),
                T("github_trigger_workflow", "Dispatch a workflow_dispatch workflow by id/filename and ref.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("workflow_id", S("Workflow file name e.g. ci.yml or numeric id")),
                          ("ref", S("Git ref to run on")), ("inputs_json", S("Optional JSON object of inputs"))), "owner", "repo", "workflow_id", "ref"),
                T("github_create_gist", "Create a gist.",
                    Props(("filename", S("File name")), ("content", S("File content")), ("description", S("Optional")),
                          ("public", S("true/false, default false"))), "filename", "content"),
                T("github_list_notifications", "List unread notifications for the authenticated user.",
                    Props(("max_results", I("1-50, default 20")), ("all", S("true to include read")))),
                T("github_star_repo", "Star a repository.",
                    Props(("owner", S("Owner")), ("repo", S("Repo"))), "owner", "repo"),
                T("github_unstar_repo", "Unstar a repository.",
                    Props(("owner", S("Owner")), ("repo", S("Repo"))), "owner", "repo"),
                T("github_fork_repo", "Fork a repository to the authenticated user.",
                    Props(("owner", S("Owner")), ("repo", S("Repo")), ("organization", S("Optional org login to fork into"))), "owner", "repo"),
                T("github_get_authenticated_clone_url", "Return an HTTPS clone URL using the connected token for private repo clone (x-access-token). Prefer for Workplace clone helpers.",
                    Props(("owner", S("Owner")), ("repo", S("Repo"))), "owner", "repo")
            ];
        }

        public static async Task<string> ExecuteAsync(string toolName, JsonElement args, string accessToken, CancellationToken token)
        {
            string name = toolName.Trim();

            if (Eq(name, "github_whoami"))
            {
                using JsonDocument doc = await GetAsync("/user", accessToken, token).ConfigureAwait(false);
                return $"login={P(doc, "login")} name={P(doc, "name")} email={P(doc, "email")} html_url={P(doc, "html_url")} public_repos={P(doc, "public_repos")}";
            }

            if (Eq(name, "github_list_repos"))
            {
                string owner = G(args, "owner");
                int max = Clamp(Gi(args, "max_results", 20), 1, 50);
                string type = string.IsNullOrWhiteSpace(G(args, "type")) ? "all" : G(args, "type");
                string path = string.IsNullOrWhiteSpace(owner)
                    ? $"/user/repos?per_page={max}&sort=updated&affiliation=owner,collaborator,organization_member"
                    : $"/users/{Uri.EscapeDataString(owner)}/repos?per_page={max}&sort=updated&type={Uri.EscapeDataString(type)}";
                using JsonDocument doc = await GetAsync(path, accessToken, token).ConfigureAwait(false);
                return FormatRepoList(doc.RootElement, max);
            }

            if (Eq(name, "github_get_repo"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                using JsonDocument doc = await GetAsync($"/repos/{E(owner)}/{E(repo)}", accessToken, token).ConfigureAwait(false);
                return FormatRepo(doc.RootElement);
            }

            if (Eq(name, "github_list_branches"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                int max = Clamp(Gi(args, "max_results", 30), 1, 50);
                using JsonDocument doc = await GetAsync($"/repos/{E(owner)}/{E(repo)}/branches?per_page={max}", accessToken, token).ConfigureAwait(false);
                var sb = new StringBuilder("Branches:");
                int i = 0;
                foreach (JsonElement b in EnumerateArray(doc.RootElement))
                {
                    i++;
                    sb.AppendLine();
                    sb.AppendLine($"{i}. {P(b, "name")} protected={P(b, "protected")} sha={P(b.GetProperty("commit"), "sha")}");
                }
                return i == 0 ? "No branches." : sb.ToString();
            }

            if (Eq(name, "github_get_file"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string path = Req(args, "path", out err); if (err != null) return err;
                string r = G(args, "ref");
                string url = $"/repos/{E(owner)}/{E(repo)}/contents/{EscapePath(path)}";
                if (!string.IsNullOrWhiteSpace(r)) url += "?ref=" + Uri.EscapeDataString(r);
                using JsonDocument doc = await GetAsync(url, accessToken, token).ConfigureAwait(false);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    return "Path is a directory. Use github_list_directory.";
                string encoding = P(doc, "encoding") ?? "";
                string content = P(doc, "content") ?? "";
                string text = string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "", StringComparison.Ordinal)))
                    : content;
                return $"path={P(doc, "path")} sha={P(doc, "sha")} size={P(doc, "size")}\n\n{Bound(text, 14000)}";
            }

            if (Eq(name, "github_list_directory"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string path = G(args, "path");
                string r = G(args, "ref");
                string url = $"/repos/{E(owner)}/{E(repo)}/contents/{EscapePath(path)}";
                if (!string.IsNullOrWhiteSpace(r)) url += "?ref=" + Uri.EscapeDataString(r);
                using JsonDocument doc = await GetAsync(url, accessToken, token).ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return "Path is a file. Use github_get_file.";
                var sb = new StringBuilder($"Directory {path}:");
                foreach (JsonElement item in EnumerateArray(doc.RootElement))
                {
                    sb.AppendLine();
                    sb.AppendLine($"- [{P(item, "type")}] {P(item, "path")} size={P(item, "size")} sha={P(item, "sha")}");
                }
                return sb.ToString();
            }

            if (Eq(name, "github_search_code"))
            {
                string query = Req(args, "query", out string? err); if (err != null) return err;
                int max = Clamp(Gi(args, "max_results", 15), 1, 30);
                using JsonDocument doc = await GetAsync($"/search/code?q={Uri.EscapeDataString(query)}&per_page={max}", accessToken, token).ConfigureAwait(false);
                return FormatSearchCode(doc.RootElement);
            }

            if (Eq(name, "github_search_issues"))
            {
                string query = Req(args, "query", out string? err); if (err != null) return err;
                int max = Clamp(Gi(args, "max_results", 15), 1, 30);
                using JsonDocument doc = await GetAsync($"/search/issues?q={Uri.EscapeDataString(query)}&per_page={max}", accessToken, token).ConfigureAwait(false);
                return FormatSearchIssues(doc.RootElement);
            }

            if (Eq(name, "github_search_repos"))
            {
                string query = Req(args, "query", out string? err); if (err != null) return err;
                int max = Clamp(Gi(args, "max_results", 15), 1, 30);
                using JsonDocument doc = await GetAsync($"/search/repositories?q={Uri.EscapeDataString(query)}&per_page={max}", accessToken, token).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("items", out JsonElement items)) return "No repos.";
                return FormatRepoList(items, max);
            }

            if (Eq(name, "github_list_issues"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string state = string.IsNullOrWhiteSpace(G(args, "state")) ? "open" : G(args, "state");
                int max = Clamp(Gi(args, "max_results", 20), 1, 50);
                string url = $"/repos/{E(owner)}/{E(repo)}/issues?state={Uri.EscapeDataString(state)}&per_page={max}";
                string labels = G(args, "labels");
                if (!string.IsNullOrWhiteSpace(labels)) url += "&labels=" + Uri.EscapeDataString(labels);
                using JsonDocument doc = await GetAsync(url, accessToken, token).ConfigureAwait(false);
                return FormatIssues(doc.RootElement);
            }

            if (Eq(name, "github_get_issue"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                int number = Gi(args, "number", 0);
                if (number <= 0) return "number is required.";
                using JsonDocument doc = await GetAsync($"/repos/{E(owner)}/{E(repo)}/issues/{number}", accessToken, token).ConfigureAwait(false);
                return FormatIssue(doc.RootElement);
            }

            if (Eq(name, "github_create_issue"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string title = Req(args, "title", out err); if (err != null) return err;
                var body = new JsonObject { ["title"] = title, ["body"] = G(args, "body") };
                AddStringArray(body, "labels", G(args, "labels"));
                AddStringArray(body, "assignees", G(args, "assignees"));
                using JsonDocument doc = await SendAsync(HttpMethod.Post, $"/repos/{E(owner)}/{E(repo)}/issues", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return "Issue created.\n" + FormatIssue(doc.RootElement);
            }

            if (Eq(name, "github_update_issue"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                int number = Gi(args, "number", 0);
                if (number <= 0) return "number is required.";
                var body = new JsonObject();
                if (!string.IsNullOrWhiteSpace(G(args, "title"))) body["title"] = G(args, "title");
                if (!string.IsNullOrWhiteSpace(G(args, "body"))) body["body"] = G(args, "body");
                if (!string.IsNullOrWhiteSpace(G(args, "state"))) body["state"] = G(args, "state");
                if (!string.IsNullOrWhiteSpace(G(args, "labels"))) AddStringArray(body, "labels", G(args, "labels"));
                using JsonDocument doc = await SendAsync(HttpMethod.Patch, $"/repos/{E(owner)}/{E(repo)}/issues/{number}", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return "Issue updated.\n" + FormatIssue(doc.RootElement);
            }

            if (Eq(name, "github_add_issue_comment"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                int number = Gi(args, "number", 0);
                string bodyText = Req(args, "body", out err); if (err != null) return err;
                if (number <= 0) return "number is required.";
                using JsonDocument doc = await SendAsync(HttpMethod.Post, $"/repos/{E(owner)}/{E(repo)}/issues/{number}/comments", accessToken,
                    new JsonObject { ["body"] = bodyText }.ToJsonString(), token).ConfigureAwait(false);
                return $"Comment added id={P(doc, "id")} url={P(doc, "html_url")}";
            }

            if (Eq(name, "github_list_pulls"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string state = string.IsNullOrWhiteSpace(G(args, "state")) ? "open" : G(args, "state");
                int max = Clamp(Gi(args, "max_results", 20), 1, 50);
                using JsonDocument doc = await GetAsync($"/repos/{E(owner)}/{E(repo)}/pulls?state={Uri.EscapeDataString(state)}&per_page={max}", accessToken, token).ConfigureAwait(false);
                return FormatPulls(doc.RootElement);
            }

            if (Eq(name, "github_get_pull"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                int number = Gi(args, "number", 0);
                if (number <= 0) return "number is required.";
                using JsonDocument doc = await GetAsync($"/repos/{E(owner)}/{E(repo)}/pulls/{number}", accessToken, token).ConfigureAwait(false);
                return FormatPull(doc.RootElement);
            }

            if (Eq(name, "github_list_pull_files"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                int number = Gi(args, "number", 0);
                int max = Clamp(Gi(args, "max_results", 40), 1, 100);
                if (number <= 0) return "number is required.";
                using JsonDocument doc = await GetAsync($"/repos/{E(owner)}/{E(repo)}/pulls/{number}/files?per_page={max}", accessToken, token).ConfigureAwait(false);
                var sb = new StringBuilder($"PR #{number} files:");
                foreach (JsonElement f in EnumerateArray(doc.RootElement))
                {
                    sb.AppendLine();
                    sb.AppendLine($"- {P(f, "filename")} status={P(f, "status")} +{P(f, "additions")}/-{P(f, "deletions")}");
                }
                return sb.ToString();
            }

            if (Eq(name, "github_create_pull"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string title = Req(args, "title", out err); if (err != null) return err;
                string head = Req(args, "head", out err); if (err != null) return err;
                string bas = Req(args, "base", out err); if (err != null) return err;
                var body = new JsonObject
                {
                    ["title"] = title,
                    ["head"] = head,
                    ["base"] = bas,
                    ["body"] = G(args, "body"),
                    ["draft"] = IsTruthy(G(args, "draft"))
                };
                using JsonDocument doc = await SendAsync(HttpMethod.Post, $"/repos/{E(owner)}/{E(repo)}/pulls", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return "Pull request created.\n" + FormatPull(doc.RootElement);
            }

            if (Eq(name, "github_merge_pull"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                int number = Gi(args, "number", 0);
                if (number <= 0) return "number is required.";
                string method = string.IsNullOrWhiteSpace(G(args, "merge_method")) ? "merge" : G(args, "merge_method");
                var body = new JsonObject { ["merge_method"] = method };
                if (!string.IsNullOrWhiteSpace(G(args, "commit_title"))) body["commit_title"] = G(args, "commit_title");
                using JsonDocument doc = await SendAsync(HttpMethod.Put, $"/repos/{E(owner)}/{E(repo)}/pulls/{number}/merge", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return $"Merge result: merged={P(doc, "merged")} message={P(doc, "message")} sha={P(doc, "sha")}";
            }

            if (Eq(name, "github_list_commits"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                int max = Clamp(Gi(args, "max_results", 20), 1, 50);
                string url = $"/repos/{E(owner)}/{E(repo)}/commits?per_page={max}";
                if (!string.IsNullOrWhiteSpace(G(args, "sha"))) url += "&sha=" + Uri.EscapeDataString(G(args, "sha"));
                if (!string.IsNullOrWhiteSpace(G(args, "path"))) url += "&path=" + Uri.EscapeDataString(G(args, "path"));
                using JsonDocument doc = await GetAsync(url, accessToken, token).ConfigureAwait(false);
                var sb = new StringBuilder("Commits:");
                int i = 0;
                foreach (JsonElement c in EnumerateArray(doc.RootElement))
                {
                    i++;
                    string sha = P(c, "sha") ?? "";
                    string msg = c.TryGetProperty("commit", out JsonElement cm) ? P(cm, "message") ?? "" : "";
                    string first = msg.Split('\n')[0];
                    sb.AppendLine();
                    sb.AppendLine($"{i}. {sha[..Math.Min(7, sha.Length)]} {first}");
                }
                return i == 0 ? "No commits." : sb.ToString();
            }

            if (Eq(name, "github_get_commit"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string refr = Req(args, "ref", out err); if (err != null) return err;
                using JsonDocument doc = await GetAsync($"/repos/{E(owner)}/{E(repo)}/commits/{E(refr)}", accessToken, token).ConfigureAwait(false);
                string msg = doc.RootElement.TryGetProperty("commit", out JsonElement cm) ? P(cm, "message") ?? "" : "";
                var sb = new StringBuilder($"Commit {P(doc, "sha")}\n{msg}\nFiles:");
                if (doc.RootElement.TryGetProperty("files", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement f in files.EnumerateArray().Take(80))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"- {P(f, "filename")} +{P(f, "additions")}/-{P(f, "deletions")}");
                    }
                }
                return sb.ToString();
            }

            if (Eq(name, "github_compare"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string bas = Req(args, "base", out err); if (err != null) return err;
                string head = Req(args, "head", out err); if (err != null) return err;
                using JsonDocument doc = await GetAsync($"/repos/{E(owner)}/{E(repo)}/compare/{E(bas)}...{E(head)}", accessToken, token).ConfigureAwait(false);
                return $"status={P(doc, "status")} ahead={P(doc, "ahead_by")} behind={P(doc, "behind_by")} total_commits={P(doc, "total_commits")} html_url={P(doc, "html_url")}";
            }

            if (Eq(name, "github_create_branch"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string branch = Req(args, "branch", out err); if (err != null) return err;
                string fromRef = G(args, "from_ref");
                if (string.IsNullOrWhiteSpace(fromRef))
                {
                    using JsonDocument repoDoc = await GetAsync($"/repos/{E(owner)}/{E(repo)}", accessToken, token).ConfigureAwait(false);
                    fromRef = P(repoDoc, "default_branch") ?? "main";
                }
                using JsonDocument refDoc = await GetAsync($"/repos/{E(owner)}/{E(repo)}/git/ref/heads/{E(fromRef)}", accessToken, token).ConfigureAwait(false);
                string sha = refDoc.RootElement.TryGetProperty("object", out JsonElement obj) ? P(obj, "sha") ?? "" : "";
                if (string.IsNullOrWhiteSpace(sha))
                {
                    // from_ref might already be a sha
                    sha = fromRef;
                }
                using JsonDocument created = await SendAsync(HttpMethod.Post, $"/repos/{E(owner)}/{E(repo)}/git/refs", accessToken,
                    new JsonObject { ["ref"] = "refs/heads/" + branch, ["sha"] = sha }.ToJsonString(), token).ConfigureAwait(false);
                return $"Branch created: {branch} from {sha[..Math.Min(7, sha.Length)]} ref={P(created, "ref")}";
            }

            if (Eq(name, "github_create_or_update_file"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string path = Req(args, "path", out err); if (err != null) return err;
                string content = G(args, "content");
                string message = Req(args, "message", out err); if (err != null) return err;
                var body = new JsonObject
                {
                    ["message"] = message,
                    ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content ?? ""))
                };
                if (!string.IsNullOrWhiteSpace(G(args, "branch"))) body["branch"] = G(args, "branch");
                if (!string.IsNullOrWhiteSpace(G(args, "sha"))) body["sha"] = G(args, "sha");
                using JsonDocument doc = await SendAsync(HttpMethod.Put, $"/repos/{E(owner)}/{E(repo)}/contents/{EscapePath(path)}", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                string commitSha = doc.RootElement.TryGetProperty("commit", out JsonElement c) ? P(c, "sha") ?? "" : "";
                string contentSha = doc.RootElement.TryGetProperty("content", out JsonElement ct) ? P(ct, "sha") ?? "" : "";
                return $"File saved path={path} content_sha={contentSha} commit={commitSha}";
            }

            if (Eq(name, "github_delete_file"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string path = Req(args, "path", out err); if (err != null) return err;
                string message = Req(args, "message", out err); if (err != null) return err;
                string sha = Req(args, "sha", out err); if (err != null) return err;
                var body = new JsonObject { ["message"] = message, ["sha"] = sha };
                if (!string.IsNullOrWhiteSpace(G(args, "branch"))) body["branch"] = G(args, "branch");
                using JsonDocument doc = await SendAsync(HttpMethod.Delete, $"/repos/{E(owner)}/{E(repo)}/contents/{EscapePath(path)}", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return $"File deleted path={path} commit={ (doc.RootElement.TryGetProperty("commit", out JsonElement c) ? P(c, "sha") : "") }";
            }

            if (Eq(name, "github_list_releases"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                int max = Clamp(Gi(args, "max_results", 10), 1, 30);
                using JsonDocument doc = await GetAsync($"/repos/{E(owner)}/{E(repo)}/releases?per_page={max}", accessToken, token).ConfigureAwait(false);
                var sb = new StringBuilder("Releases:");
                int i = 0;
                foreach (JsonElement r in EnumerateArray(doc.RootElement))
                {
                    i++;
                    sb.AppendLine();
                    sb.AppendLine($"{i}. {P(r, "tag_name")} {P(r, "name")} draft={P(r, "draft")} prerelease={P(r, "prerelease")} url={P(r, "html_url")}");
                }
                return i == 0 ? "No releases." : sb.ToString();
            }

            if (Eq(name, "github_list_workflows"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                using JsonDocument doc = await GetAsync($"/repos/{E(owner)}/{E(repo)}/actions/workflows", accessToken, token).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("workflows", out JsonElement wfs)) return "No workflows.";
                var sb = new StringBuilder("Workflows:");
                foreach (JsonElement w in EnumerateArray(wfs))
                {
                    sb.AppendLine();
                    sb.AppendLine($"- id={P(w, "id")} name={P(w, "name")} path={P(w, "path")} state={P(w, "state")}");
                }
                return sb.ToString();
            }

            if (Eq(name, "github_list_workflow_runs"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                int max = Clamp(Gi(args, "max_results", 10), 1, 30);
                string wf = G(args, "workflow_id");
                string url = string.IsNullOrWhiteSpace(wf)
                    ? $"/repos/{E(owner)}/{E(repo)}/actions/runs?per_page={max}"
                    : $"/repos/{E(owner)}/{E(repo)}/actions/workflows/{E(wf)}/runs?per_page={max}";
                using JsonDocument doc = await GetAsync(url, accessToken, token).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("workflow_runs", out JsonElement runs)) return "No runs.";
                var sb = new StringBuilder("Workflow runs:");
                foreach (JsonElement r in EnumerateArray(runs))
                {
                    sb.AppendLine();
                    sb.AppendLine($"- id={P(r, "id")} {P(r, "name")} status={P(r, "status")} conclusion={P(r, "conclusion")} branch={P(r, "head_branch")} url={P(r, "html_url")}");
                }
                return sb.ToString();
            }

            if (Eq(name, "github_trigger_workflow"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string wf = Req(args, "workflow_id", out err); if (err != null) return err;
                string refr = Req(args, "ref", out err); if (err != null) return err;
                var body = new JsonObject { ["ref"] = refr };
                string inputsJson = G(args, "inputs_json");
                if (!string.IsNullOrWhiteSpace(inputsJson))
                {
                    try
                    {
                        body["inputs"] = JsonNode.Parse(inputsJson);
                    }
                    catch
                    {
                        return "inputs_json must be a valid JSON object.";
                    }
                }
                await SendAsync(HttpMethod.Post, $"/repos/{E(owner)}/{E(repo)}/actions/workflows/{E(wf)}/dispatches", accessToken, body.ToJsonString(), token, allowEmpty: true).ConfigureAwait(false);
                return $"Workflow dispatch accepted for {wf} on ref {refr}.";
            }

            if (Eq(name, "github_create_gist"))
            {
                string filename = Req(args, "filename", out string? err); if (err != null) return err;
                string content = Req(args, "content", out err); if (err != null) return err;
                var files = new JsonObject { [filename] = new JsonObject { ["content"] = content } };
                var body = new JsonObject
                {
                    ["description"] = G(args, "description"),
                    ["public"] = IsTruthy(G(args, "public")),
                    ["files"] = files
                };
                using JsonDocument doc = await SendAsync(HttpMethod.Post, "/gists", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return $"Gist created id={P(doc, "id")} url={P(doc, "html_url")}";
            }

            if (Eq(name, "github_list_notifications"))
            {
                int max = Clamp(Gi(args, "max_results", 20), 1, 50);
                bool all = IsTruthy(G(args, "all"));
                using JsonDocument doc = await GetAsync($"/notifications?all={(all ? "true" : "false")}&per_page={max}", accessToken, token).ConfigureAwait(false);
                var sb = new StringBuilder("Notifications:");
                int i = 0;
                foreach (JsonElement n in EnumerateArray(doc.RootElement))
                {
                    i++;
                    string repoName = n.TryGetProperty("repository", out JsonElement r) ? P(r, "full_name") ?? "" : "";
                    string subject = n.TryGetProperty("subject", out JsonElement s) ? $"{P(s, "type")}: {P(s, "title")}" : "";
                    sb.AppendLine();
                    sb.AppendLine($"{i}. [{repoName}] {subject} unread={!IsTruthy(P(n, "unread") == "False" ? "false" : P(n, "unread") ?? "true")}");
                }
                return i == 0 ? "No notifications." : sb.ToString();
            }

            if (Eq(name, "github_star_repo") || Eq(name, "github_unstar_repo"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                var method = Eq(name, "github_star_repo") ? HttpMethod.Put : HttpMethod.Delete;
                await SendAsync(method, $"/user/starred/{E(owner)}/{E(repo)}", accessToken, null, token, allowEmpty: true).ConfigureAwait(false);
                return Eq(name, "github_star_repo") ? $"Starred {owner}/{repo}." : $"Unstarred {owner}/{repo}.";
            }

            if (Eq(name, "github_fork_repo"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                var body = new JsonObject();
                if (!string.IsNullOrWhiteSpace(G(args, "organization")))
                    body["organization"] = G(args, "organization");
                using JsonDocument doc = await SendAsync(HttpMethod.Post, $"/repos/{E(owner)}/{E(repo)}/forks", accessToken, body.ToJsonString(), token).ConfigureAwait(false);
                return "Fork created.\n" + FormatRepo(doc.RootElement);
            }

            if (Eq(name, "github_get_authenticated_clone_url"))
            {
                string owner = Req(args, "owner", out string? err); if (err != null) return err;
                string repo = Req(args, "repo", out err); if (err != null) return err;
                string cloneUrl = BuildAuthenticatedCloneUrl(owner, repo, accessToken);
                return $"Authenticated clone URL ready for git clone (token embedded).\nowner={owner} repo={repo}\nclone_url={cloneUrl}\nUse this only for local clone operations; do not paste the raw URL into chat replies.";
            }

            return $"Unsupported GitHub tool: {toolName}";
        }

        /// <summary>Build clone URL with embedded token for private repos.</summary>
        public static string BuildAuthenticatedCloneUrl(string owner, string repo, string accessToken)
        {
            return $"https://x-access-token:{accessToken}@github.com/{owner.Trim()}/{repo.Trim().TrimEnd('.')}.git"
                .Replace(".git.git", ".git", StringComparison.OrdinalIgnoreCase);
        }

        public static string InjectTokenIntoGitHubUrl(string repositoryUrl, string accessToken)
        {
            if (string.IsNullOrWhiteSpace(repositoryUrl) || string.IsNullOrWhiteSpace(accessToken))
                return repositoryUrl;
            if (!Uri.TryCreate(repositoryUrl.Trim(), UriKind.Absolute, out Uri? uri))
                return repositoryUrl;
            if (!uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                return repositoryUrl;
            // https://github.com/owner/repo(.git)
            string path = uri.AbsolutePath.Trim('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path[..^4];
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return repositoryUrl;
            return BuildAuthenticatedCloneUrl(parts[0], parts[1], accessToken);
        }

        // ── HTTP ────────────────────────────────────────────────────────────

        private static async Task<JsonDocument> GetAsync(string path, string token, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
            Auth(request, token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"GitHub API {(int)response.StatusCode}: {Truncate(body, 500)}");
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }

        private static async Task<JsonDocument> SendAsync(HttpMethod method, string path, string token, string? json, CancellationToken ct, bool allowEmpty = false)
        {
            using var request = new HttpRequestMessage(method, Api + path);
            Auth(request, token);
            if (json != null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"GitHub API {(int)response.StatusCode}: {Truncate(body, 500)}");
            if (allowEmpty && string.IsNullOrWhiteSpace(body))
                return JsonDocument.Parse("{}");
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }

        private static void Auth(HttpRequestMessage request, string token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("Axiom-MCP/1.0");
        }

        // ── Format helpers ──────────────────────────────────────────────────

        private static string FormatRepoList(JsonElement root, int max)
        {
            IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array
                ? EnumerateArray(root)
                : root.TryGetProperty("items", out JsonElement it) ? EnumerateArray(it) : Array.Empty<JsonElement>();
            var sb = new StringBuilder("Repositories:");
            int i = 0;
            foreach (JsonElement r in items)
            {
                if (i >= max) break;
                i++;
                sb.AppendLine();
                sb.AppendLine($"{i}. {FormatRepo(r)}");
            }
            return i == 0 ? "No repositories." : sb.ToString();
        }

        private static string FormatRepo(JsonElement r)
            => $"full_name={P(r, "full_name")} private={P(r, "private")} default_branch={P(r, "default_branch")} stars={P(r, "stargazers_count")} language={P(r, "language")} url={P(r, "html_url")} description={P(r, "description")}";

        private static string FormatIssues(JsonElement root)
        {
            var sb = new StringBuilder("Issues:");
            int i = 0;
            foreach (JsonElement issue in EnumerateArray(root))
            {
                // list issues endpoint includes PRs — skip pure PR objects if pull_request present? Keep all.
                i++;
                sb.AppendLine();
                sb.AppendLine($"{i}. #{P(issue, "number")} [{P(issue, "state")}] {P(issue, "title")} by { (issue.TryGetProperty("user", out JsonElement u) ? P(u, "login") : "") }");
            }
            return i == 0 ? "No issues." : sb.ToString();
        }

        private static string FormatIssue(JsonElement issue)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"#{P(issue, "number")} [{P(issue, "state")}] {P(issue, "title")}");
            sb.AppendLine($"url={P(issue, "html_url")} user={(issue.TryGetProperty("user", out JsonElement u) ? P(u, "login") : "")}");
            if (issue.TryGetProperty("labels", out JsonElement labels) && labels.ValueKind == JsonValueKind.Array)
                sb.AppendLine("labels=" + string.Join(",", labels.EnumerateArray().Select(l => P(l, "name"))));
            sb.AppendLine();
            sb.AppendLine(Bound(P(issue, "body") ?? "", 8000));
            return sb.ToString().TrimEnd();
        }

        private static string FormatPulls(JsonElement root)
        {
            var sb = new StringBuilder("Pull requests:");
            int i = 0;
            foreach (JsonElement pr in EnumerateArray(root))
            {
                i++;
                sb.AppendLine();
                sb.AppendLine($"{i}. #{P(pr, "number")} [{P(pr, "state")}] {P(pr, "title")} {P(pr, "head")?.Length} head={(pr.TryGetProperty("head", out JsonElement h) ? P(h, "ref") : "")} -> base={(pr.TryGetProperty("base", out JsonElement b) ? P(b, "ref") : "")}");
            }
            return i == 0 ? "No pull requests." : sb.ToString();
        }

        private static string FormatPull(JsonElement pr)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"PR #{P(pr, "number")} [{P(pr, "state")}] {P(pr, "title")}");
            sb.AppendLine($"draft={P(pr, "draft")} mergeable={P(pr, "mergeable")} url={P(pr, "html_url")}");
            sb.AppendLine($"head={(pr.TryGetProperty("head", out JsonElement h) ? P(h, "ref") : "")} base={(pr.TryGetProperty("base", out JsonElement b) ? P(b, "ref") : "")}");
            sb.AppendLine();
            sb.AppendLine(Bound(P(pr, "body") ?? "", 8000));
            return sb.ToString().TrimEnd();
        }

        private static string FormatSearchCode(JsonElement root)
        {
            if (!root.TryGetProperty("items", out JsonElement items)) return "No code results.";
            var sb = new StringBuilder($"Code search total={P(root, "total_count")}:");
            int i = 0;
            foreach (JsonElement item in EnumerateArray(items))
            {
                i++;
                string repo = item.TryGetProperty("repository", out JsonElement r) ? P(r, "full_name") ?? "" : "";
                sb.AppendLine();
                sb.AppendLine($"{i}. {repo}/{P(item, "path")} url={P(item, "html_url")}");
            }
            return i == 0 ? "No code results." : sb.ToString();
        }

        private static string FormatSearchIssues(JsonElement root)
        {
            if (!root.TryGetProperty("items", out JsonElement items)) return "No issue results.";
            var sb = new StringBuilder($"Issue search total={P(root, "total_count")}:");
            int i = 0;
            foreach (JsonElement item in EnumerateArray(items))
            {
                i++;
                sb.AppendLine();
                sb.AppendLine($"{i}. #{P(item, "number")} [{P(item, "state")}] {P(item, "title")} url={P(item, "html_url")}");
            }
            return i == 0 ? "No issue results." : sb.ToString();
        }

        private static void AddStringArray(JsonObject body, string key, string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return;
            var arr = new JsonArray();
            foreach (string part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                arr.Add(part);
            body[key] = arr;
        }

        private static IEnumerable<JsonElement> EnumerateArray(JsonElement el)
            => el.ValueKind == JsonValueKind.Array ? el.EnumerateArray() : Array.Empty<JsonElement>();

        private static string? P(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out JsonElement p))
                return null;
            return p.ValueKind switch
            {
                JsonValueKind.String => p.GetString(),
                JsonValueKind.Number => p.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => p.ToString()
            };
        }

        private static string? P(JsonDocument doc, string name) => P(doc.RootElement, name);

        private static string G(JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement el))
                return "";
            return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString();
        }

        private static int Gi(JsonElement args, string name, int def)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement el))
                return def;
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
        private static bool IsTruthy(string v) => v is "true" or "1" or "yes" or "True" or "TRUE";
        private static int Clamp(int v, int min, int max) => Math.Min(max, Math.Max(min, v));
        private static string E(string s) => Uri.EscapeDataString(s.Trim());
        private static string EscapePath(string path) => string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        private static string Bound(string t, int max) => string.IsNullOrEmpty(t) || t.Length <= max ? t ?? "" : t[..max] + "\n…[truncated]";
        private static string Truncate(string t, int max) => string.IsNullOrEmpty(t) || t.Length <= max ? t ?? "" : t[..max] + "…";
    }
}
