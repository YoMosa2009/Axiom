using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Mcp
{
    /// <summary>
    /// Host-side MCP connector registry for Axiom. Built-in connectors (Gmail, Google Drive)
    /// use Google OAuth + REST and are exposed to cloud models as OpenRouter tools. Connectors
    /// are only intended for Cloud Mode tool loops.
    /// </summary>
    internal sealed class McpConnectorService
    {
        public const string GmailId = "gmail";
        public const string GoogleDriveId = "google_drive";
        public const string GitHubId = "github";
        public const string TodoistId = "todoist";

        // gmail.modify: full mailbox read/write except permanent delete (search, labels, trash, drafts, send).
        private static readonly string[] GmailScopes =
        [
            "openid",
            "email",
            "https://www.googleapis.com/auth/gmail.modify"
        ];

        // drive: full user Drive access (create/edit/share/trash).
        private static readonly string[] DriveScopes =
        [
            "openid",
            "email",
            "https://www.googleapis.com/auth/drive"
        ];

        /// <summary>One Google sign-in unlocks both Gmail and Drive (Claude-style account connect).</summary>
        private static readonly string[] GoogleAccountScopes =
        [
            "openid",
            "email",
            "https://www.googleapis.com/auth/gmail.modify",
            "https://www.googleapis.com/auth/drive"
        ];

        private readonly McpSecureStore _store;
        private readonly GoogleOAuthService _oauth = new();
        private readonly GitHubOAuthService _gitHubOAuth = new();
        private readonly TodoistOAuthService _todoistOAuth = new();
        private readonly object _gate = new();
        private McpConnectorStateFile _state;
        private readonly List<McpConnectorInfo> _connectors;

        public event Action? Changed;

        public McpConnectorService(DatabaseService? database)
        {
            _store = new McpSecureStore(database);
            _state = _store.Load();
            _connectors =
            [
                new McpConnectorInfo
                {
                    Id = GmailId,
                    Handle = "Gmail",
                    DisplayName = "Gmail",
                    Description = "Full Gmail: search, read, draft, send, reply, labels, archive, trash, spam, attachments.",
                    Kind = McpConnectorKind.Gmail,
                    LogoGlyph = "✉"
                },
                new McpConnectorInfo
                {
                    Id = GoogleDriveId,
                    Handle = "GoogleDrive",
                    DisplayName = "Google Drive",
                    Description = "Full Drive: search, read/export, create, edit, move, copy, share, trash, star.",
                    Kind = McpConnectorKind.GoogleDrive,
                    LogoGlyph = "📁"
                },
                new McpConnectorInfo
                {
                    Id = GitHubId,
                    Handle = "GitHub",
                    DisplayName = "GitHub",
                    Description = "Full GitHub: repos, files, issues, PRs, commits, Actions, gists, search.",
                    Kind = McpConnectorKind.GitHub,
                    LogoGlyph = "◆"
                },
                new McpConnectorInfo
                {
                    Id = TodoistId,
                    Handle = "Todoist",
                    DisplayName = "Todoist",
                    Description = "Tasks & projects: list, create, complete, update, labels, comments.",
                    Kind = McpConnectorKind.Todoist,
                    LogoGlyph = "☑"
                }
            ];

            RefreshConnectionFlagsFromState();
        }

        /// <summary>Optional advanced override only — normal users never set this.</summary>
        public string GoogleOAuthClientIdOverride
        {
            get
            {
                lock (_gate)
                    return _state.GoogleOAuthClientId ?? string.Empty;
            }
            set
            {
                lock (_gate)
                {
                    _state.GoogleOAuthClientId = (value ?? string.Empty).Trim();
                    _store.Save(_state);
                }
                Changed?.Invoke();
            }
        }

        public string ResolveGoogleClientId()
        {
            string? settingsOverride;
            lock (_gate)
                settingsOverride = _state.GoogleOAuthClientId;
            return McpOAuthConfig.ResolveGoogleClientId(settingsOverride);
        }

        public string ResolveGoogleClientSecret() => McpOAuthConfig.ResolveGoogleClientSecret();

        public bool IsGoogleSignInConfigured =>
            !string.IsNullOrWhiteSpace(ResolveGoogleClientId())
            && !string.IsNullOrWhiteSpace(ResolveGoogleClientSecret());

        public IReadOnlyList<McpConnectorInfo> GetConnectors()
        {
            lock (_gate)
            {
                return _connectors
                    .Select(CloneInfo)
                    .ToList();
            }
        }

        public IReadOnlyList<string> GetKnownHandles()
        {
            lock (_gate)
                return _connectors.Select(c => c.Handle).ToList();
        }

        public IReadOnlyList<McpConnectorInfo> GetConnectedConnectors()
        {
            return GetConnectors().Where(c => c.IsConnected).ToList();
        }

        public bool IsAnyConnected()
        {
            lock (_gate)
                return _connectors.Any(c => c.IsConnected);
        }

        /// <summary>
        /// One-click Google account connect: browser sign-in, then Gmail + Drive both connected.
        /// </summary>
        public async Task<string> ConnectGoogleAccountAsync(
            CancellationToken token,
            IProgress<string>? progress = null)
        {
            string clientId = ResolveGoogleClientId();
            string clientSecret = ResolveGoogleClientSecret();
            EnsureClientIdConfigured(clientId);

            McpTokenBundle tokens = await _oauth.AuthorizeAsync(
                    clientId,
                    clientSecret,
                    GoogleAccountScopes,
                    token,
                    progress,
                    forceConsent: true)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(tokens.AccessToken))
                throw new InvalidOperationException("Google did not return an access token.");

            lock (_gate)
            {
                StoreGoogleTokens(tokens);
            }

            Changed?.Invoke();
            return string.IsNullOrWhiteSpace(tokens.AccountEmail) ? "Google account" : tokens.AccountEmail;
        }

        public async Task ConnectAsync(
            string connectorId,
            CancellationToken token,
            IProgress<string>? progress = null)
        {
            McpConnectorInfo connector = RequireConnector(connectorId);
            string clientId = ResolveGoogleClientId();
            string clientSecret = ResolveGoogleClientSecret();
            EnsureClientIdConfigured(clientId);

            // If the other Google connector is already connected with a refresh token that
            // includes broad Google scopes, re-auth once still opens the browser (scopes differ).
            if (connector.Kind == McpConnectorKind.GitHub)
            {
                await ConnectGitHubAsync(token, progress).ConfigureAwait(false);
                return;
            }

            if (connector.Kind == McpConnectorKind.Todoist)
            {
                await ConnectTodoistAsync(token, progress).ConfigureAwait(false);
                return;
            }

            string[] scopes = connector.Kind switch
            {
                McpConnectorKind.Gmail => GmailScopes,
                McpConnectorKind.GoogleDrive => DriveScopes,
                _ => throw new InvalidOperationException("Unknown connector.")
            };

            McpTokenBundle tokens = await _oauth.AuthorizeAsync(
                    clientId,
                    clientSecret,
                    scopes,
                    token,
                    progress,
                    forceConsent: true)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(tokens.AccessToken))
                throw new InvalidOperationException("Google did not return an access token.");

            lock (_gate)
            {
                if (connector.Kind is McpConnectorKind.Gmail or McpConnectorKind.GoogleDrive)
                {
                    StoreGoogleTokens(tokens);
                }
                else
                {
                    _state.Tokens[connector.Id] = tokens;
                    ApplyTokenToConnector(connector, tokens);
                    _store.Save(_state);
                }
            }

            Changed?.Invoke();
        }

        public void Disconnect(string connectorId)
        {
            lock (_gate)
            {
                McpConnectorInfo connector = RequireConnector(connectorId);
                _state.Tokens.Remove(connector.Id);
                connector.IsConnected = false;
                connector.AccountLabel = null;
                connector.ConnectedAtUtc = null;
                _store.Save(_state);
            }

            Changed?.Invoke();
        }

        public void DisconnectAllGoogle()
        {
            Disconnect(GmailId);
            Disconnect(GoogleDriveId);
        }

        public bool IsGitHubConnected
        {
            get
            {
                lock (_gate)
                    return _connectors.Any(c => c.Id == GitHubId && c.IsConnected);
            }
        }

        public string? GetGitHubAccessToken()
        {
            lock (_gate)
            {
                if (_state.Tokens.TryGetValue(GitHubId, out McpTokenBundle? bundle)
                    && bundle != null
                    && !string.IsNullOrWhiteSpace(bundle.AccessToken))
                {
                    return bundle.AccessToken;
                }
            }
            return null;
        }

        public string? GetGitHubAccountLabel()
        {
            lock (_gate)
            {
                McpConnectorInfo? gh = _connectors.FirstOrDefault(c => c.Id == GitHubId);
                return gh is { IsConnected: true } ? gh.AccountLabel : null;
            }
        }

        /// <summary>Browser device-flow connect for GitHub (code in browser → app polls token).</summary>
        public async Task<string> ConnectGitHubAsync(CancellationToken token, IProgress<string>? progress = null)
        {
            string clientId = McpOAuthConfig.ResolveGitHubClientId();
            string clientSecret = McpOAuthConfig.ResolveGitHubClientSecret();
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new InvalidOperationException(
                    "GitHub OAuth is not configured for this build.\n\n" +
                    "1) GitHub → Settings → Developer settings → OAuth Apps → New OAuth App\n" +
                    "2) Homepage: http://127.0.0.1  Callback: http://127.0.0.1\n" +
                    "3) Enable Device Flow on the app\n" +
                    "4) Put Client ID (and Secret) in McpOAuthConfig.BuiltInGitHubClientId/Secret,\n" +
                    "   or %LocalAppData%\\Axiom\\github_oauth_client_id.txt and github_oauth_client_secret.txt");
            }

            McpTokenBundle tokens = await _gitHubOAuth.AuthorizeDeviceAsync(clientId, clientSecret, token, progress)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(tokens.AccessToken))
                throw new InvalidOperationException("GitHub did not return an access token.");

            lock (_gate)
            {
                _state.Tokens[GitHubId] = tokens;
                ApplyTokenToConnector(RequireConnector(GitHubId), tokens);
                _store.Save(_state);
            }

            Changed?.Invoke();
            return string.IsNullOrWhiteSpace(tokens.AccountEmail) ? "GitHub" : tokens.AccountEmail;
        }

        public void DisconnectGitHub() => Disconnect(GitHubId);

        public bool IsTodoistConnected
        {
            get
            {
                lock (_gate)
                    return _connectors.Any(c => c.Id == TodoistId && c.IsConnected);
            }
        }

        public async Task<string> ConnectTodoistAsync(CancellationToken token, IProgress<string>? progress = null)
        {
            string clientId = McpOAuthConfig.ResolveTodoistClientId();
            string clientSecret = McpOAuthConfig.ResolveTodoistClientSecret();
            McpTokenBundle tokens = await _todoistOAuth.AuthorizeAsync(clientId, clientSecret, token, progress)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(tokens.AccessToken))
                throw new InvalidOperationException("Todoist did not return an access token.");

            lock (_gate)
            {
                _state.Tokens[TodoistId] = tokens;
                ApplyTokenToConnector(RequireConnector(TodoistId), tokens);
                _store.Save(_state);
            }

            Changed?.Invoke();
            return string.IsNullOrWhiteSpace(tokens.AccountEmail) ? "Todoist" : tokens.AccountEmail;
        }

        public void DisconnectTodoist() => Disconnect(TodoistId);

        /// <summary>Rewrite a github.com HTTPS URL to use the connected token (private clone).</summary>
        public string? TryGetAuthenticatedGitHubCloneUrl(string repositoryUrl)
        {
            string? token = GetGitHubAccessToken();
            if (string.IsNullOrWhiteSpace(token))
                return null;
            return GitHubApiConnectors.InjectTokenIntoGitHubUrl(repositoryUrl, token);
        }

        private static void EnsureClientIdConfigured(string clientId)
        {
            if (!string.IsNullOrWhiteSpace(clientId))
                return;

            throw new InvalidOperationException(
                "Google sign-in is not set up for this build yet.\n\n" +
                "Axiom needs a Google Cloud Desktop OAuth client id + client secret " +
                "so users can Connect with a single browser login.\n\n" +
                "Put them in McpOAuthConfig.BuiltInGoogleDesktopClientId / BuiltInGoogleDesktopClientSecret, " +
                "or in %LocalAppData%\\Axiom\\google_oauth_client_id.txt and google_oauth_client_secret.txt.");
        }

        private static McpTokenBundle CloneToken(McpTokenBundle source)
        {
            return new McpTokenBundle
            {
                AccessToken = source.AccessToken,
                RefreshToken = source.RefreshToken,
                ExpiresAtUtc = source.ExpiresAtUtc,
                AccountEmail = source.AccountEmail,
                Scope = source.Scope
            };
        }

        /// <summary>
        /// Tools for the cloud tool loop. When <paramref name="mentionedHandles"/> is non-empty,
        /// only tools from those connected connectors are included (manual @ initiation).
        /// When empty, all connected connector tools are available for seamless use.
        /// </summary>
        public IReadOnlyList<McpToolDefinition> GetActiveTools(IReadOnlyCollection<string>? mentionedHandles)
        {
            lock (_gate)
            {
                IEnumerable<McpConnectorInfo> active = _connectors.Where(c => c.IsConnected);
                if (mentionedHandles != null && mentionedHandles.Count > 0)
                {
                    var set = new HashSet<string>(mentionedHandles, StringComparer.OrdinalIgnoreCase);
                    active = active.Where(c => set.Contains(c.Handle));
                }

                var tools = new List<McpToolDefinition>();
                foreach (McpConnectorInfo connector in active)
                {
                    tools.AddRange(connector.Kind switch
                    {
                        McpConnectorKind.Gmail => GoogleApiConnectors.BuildGmailTools(connector.Id),
                        McpConnectorKind.GoogleDrive => GoogleApiConnectors.BuildDriveTools(connector.Id),
                        McpConnectorKind.GitHub => GitHubApiConnectors.BuildTools(connector.Id),
                        McpConnectorKind.Todoist => TodoistApiConnectors.BuildTools(connector.Id),
                        _ => Array.Empty<McpToolDefinition>()
                    });
                }

                return tools;
            }
        }

        public bool TryResolveTool(string toolName, out McpConnectorInfo? connector)
        {
            connector = null;
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            string name = toolName.Trim();
            lock (_gate)
            {
                if (name.StartsWith("gmail_", StringComparison.OrdinalIgnoreCase))
                {
                    connector = CloneInfo(_connectors.First(c => c.Id == GmailId));
                    return connector.IsConnected;
                }

                if (name.StartsWith("drive_", StringComparison.OrdinalIgnoreCase))
                {
                    connector = CloneInfo(_connectors.First(c => c.Id == GoogleDriveId));
                    return connector.IsConnected;
                }

                if (name.StartsWith("github_", StringComparison.OrdinalIgnoreCase))
                {
                    connector = CloneInfo(_connectors.First(c => c.Id == GitHubId));
                    return connector.IsConnected;
                }

                if (name.StartsWith("todoist_", StringComparison.OrdinalIgnoreCase))
                {
                    connector = CloneInfo(_connectors.First(c => c.Id == TodoistId));
                    return connector.IsConnected;
                }
            }

            return false;
        }

        public async Task<McpToolExecutionResult> ExecuteToolAsync(
            string toolName,
            string argumentsJson,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return new McpToolExecutionResult { Name = toolName ?? "", Result = "Tool name was empty.", Success = false };

            string normalized = toolName.Trim();
            McpConnectorInfo? connectorInfo;
            if (!TryResolveTool(normalized, out connectorInfo) || connectorInfo == null || !connectorInfo.IsConnected)
            {
                return new McpToolExecutionResult
                {
                    Name = normalized,
                    Result = $"Connector for '{normalized}' is not connected. Connect it under Settings → Cloud Connectors (Cloud Mode only).",
                    Success = false
                };
            }

            try
            {
                string accessToken = await EnsureValidAccessTokenAsync(connectorInfo.Id, token).ConfigureAwait(false);
                using JsonDocument argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                JsonElement args = argsDoc.RootElement;

                string result = connectorInfo.Kind switch
                {
                    McpConnectorKind.Gmail => await GoogleApiConnectors.ExecuteGmailAsync(normalized, args, accessToken, token)
                        .ConfigureAwait(false),
                    McpConnectorKind.GoogleDrive => await GoogleApiConnectors.ExecuteDriveAsync(normalized, args, accessToken, token)
                        .ConfigureAwait(false),
                    McpConnectorKind.GitHub => await GitHubApiConnectors.ExecuteAsync(normalized, args, accessToken, token)
                        .ConfigureAwait(false),
                    McpConnectorKind.Todoist => await TodoistApiConnectors.ExecuteAsync(normalized, args, accessToken, token)
                        .ConfigureAwait(false),
                    _ => $"Unsupported connector kind for {normalized}."
                };

                return new McpToolExecutionResult { Name = normalized, Result = result, Success = true };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new McpToolExecutionResult
                {
                    Name = normalized,
                    Result = $"MCP connector tool failed: {ex.Message}",
                    Success = false
                };
            }
        }

        public string BuildSystemInstruction(
            IReadOnlyCollection<string> mentionedHandles,
            bool cloudModeActive)
        {
            if (!cloudModeActive)
                return string.Empty;

            IReadOnlyList<McpConnectorInfo> connected = GetConnectedConnectors();
            if (connected.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[CLOUD CONNECTORS / MCP]");
            sb.AppendLine("The user may use connected external services (MCP connectors). These tools are only available in Cloud Mode.");

            if (mentionedHandles != null && mentionedHandles.Count > 0)
            {
                sb.AppendLine("The user manually initiated connector(s) with @mentions in this message:");
                foreach (string handle in mentionedHandles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    McpConnectorInfo? match = connected.FirstOrDefault(c =>
                        string.Equals(c.Handle, handle, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        sb.AppendLine($"- @{match.Handle} ({match.DisplayName}) — prioritize this connector's tools for this request.");
                    else
                        sb.AppendLine($"- @{handle} — not connected or unknown; tell the user to connect it in Settings if needed.");
                }
            }
            else
            {
                sb.AppendLine("Connected connectors (use when relevant; the user can also type @Handle to focus one):");
                foreach (McpConnectorInfo c in connected)
                {
                    string account = string.IsNullOrWhiteSpace(c.AccountLabel) ? "" : $" as {c.AccountLabel}";
                    sb.AppendLine($"- @{c.Handle} — {c.DisplayName}{account}: {c.Description}");
                }
            }

            sb.AppendLine("You have a large tool catalog for connected services. NEVER say you lack Gmail/Drive capability if the connector is connected — pick the closest matching tool and call it.");
            sb.AppendLine("Never invent message_id, thread_id, draft_id, file_id, label_id, or permission_id. Search/list first, then act.");
            sb.AppendLine("Do not claim a write action succeeded without a successful tool result.");
            if (connected.Any(c => c.Id == GmailId && c.IsConnected))
            {
                sb.AppendLine("Gmail capability map (call these tools):");
                sb.AppendLine("- Find: gmail_search, gmail_get_message, gmail_get_thread, gmail_list_attachments, gmail_get_attachment_text");
                sb.AppendLine("- Write: gmail_create_draft, gmail_update_draft, gmail_list_drafts, gmail_get_draft, gmail_delete_draft, gmail_send_draft, gmail_send, gmail_reply, gmail_forward");
                sb.AppendLine("- Organize: gmail_list_labels, gmail_create_label, gmail_delete_label, gmail_modify_labels, gmail_mark_read/unread, gmail_star/unstar, gmail_archive, gmail_move_to_inbox, gmail_trash/untrash, gmail_spam/unspam");
            }
            if (connected.Any(c => c.Id == GoogleDriveId && c.IsConnected))
            {
                sb.AppendLine("Google Drive capability map (call these tools):");
                sb.AppendLine("- Find/read: drive_search, drive_list_folder, drive_get_file, drive_export");
                sb.AppendLine("- Create/edit: drive_create_folder, drive_create_text_file, drive_create_google_doc, drive_update_text_file, drive_rename, drive_set_description");
                sb.AppendLine("- Organize: drive_move, drive_copy, drive_trash/untrash, drive_delete_forever, drive_star/unstar");
                sb.AppendLine("- Share: drive_share, drive_list_permissions, drive_remove_permission");
            }
            if (connected.Any(c => c.Id == GitHubId && c.IsConnected))
            {
                sb.AppendLine("GitHub capability map (call github_* tools — never claim you lack GitHub access):");
                sb.AppendLine("- Identity/repos: github_whoami, github_list_repos, github_get_repo, github_list_branches, github_star_repo, github_fork_repo");
                sb.AppendLine("- Code: github_get_file, github_list_directory, github_search_code, github_create_or_update_file, github_delete_file, github_create_branch");
                sb.AppendLine("- Issues/PRs: github_list_issues, github_get_issue, github_create_issue, github_update_issue, github_add_issue_comment, github_list_pulls, github_get_pull, github_create_pull, github_merge_pull, github_list_pull_files");
                sb.AppendLine("- History/CI: github_list_commits, github_get_commit, github_compare, github_list_releases, github_list_workflows, github_list_workflow_runs, github_trigger_workflow");
                sb.AppendLine("- Other: github_search_issues, github_search_repos, github_create_gist, github_list_notifications, github_get_authenticated_clone_url");
                sb.AppendLine("For Workplace codebase work: use local read_file/search_codebase when a folder is connected; use GitHub tools for remote issues/PRs/files/API actions.");
            }
            if (connected.Any(c => c.Id == TodoistId && c.IsConnected))
            {
                sb.AppendLine("Todoist capability map (call todoist_* tools — never claim you lack Todoist access):");
                sb.AppendLine("- Identity/projects: todoist_whoami, todoist_list_projects, todoist_get_project, todoist_create_project, todoist_update_project, todoist_delete_project");
                sb.AppendLine("- Tasks: todoist_list_tasks, todoist_get_task, todoist_create_task, todoist_quick_add, todoist_update_task, todoist_complete_task, todoist_reopen_task, todoist_delete_task");
                sb.AppendLine("- Sections/labels/comments: todoist_list_sections, todoist_create_section, todoist_list_labels, todoist_create_label, todoist_list_comments, todoist_add_comment");
                sb.AppendLine("Prefer todoist_quick_add for natural-language capture (supports #project @label due phrases). Do not claim a task was created without a successful tool result.");
            }
            sb.AppendLine("If a tool returns a permission/scope error, tell the user to Disconnect and Connect the provider again in Settings → Connectors.");
            return sb.ToString().TrimEnd();
        }

        /// <summary>Access token validity: GitHub OAuth tokens don't refresh; treat as always valid until 401.</summary>
        public bool TryGetConnectorAccessToken(string connectorId, out string? accessToken)
        {
            accessToken = null;
            lock (_gate)
            {
                if (_state.Tokens.TryGetValue(connectorId, out McpTokenBundle? bundle)
                    && bundle != null
                    && !string.IsNullOrWhiteSpace(bundle.AccessToken))
                {
                    accessToken = bundle.AccessToken;
                    return true;
                }
            }
            return false;
        }

        private async Task<string> EnsureValidAccessTokenAsync(string connectorId, CancellationToken token)
        {
            string clientId = ResolveGoogleClientId();
            string clientSecret = ResolveGoogleClientSecret();
            McpTokenBundle? bundle;
            lock (_gate)
            {
                _state.Tokens.TryGetValue(connectorId, out bundle);
            }

            if (bundle == null || !HasUsableSession(bundle))
                throw new InvalidOperationException("Connector is not connected.");

            // GitHub classic OAuth tokens do not use refresh_token.
            if (string.Equals(connectorId, GitHubId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(bundle.AccessToken))
                    throw new InvalidOperationException("GitHub session missing. Connect GitHub again.");
                return bundle.AccessToken;
            }

            // Still valid for a bit — use as-is.
            if (!string.IsNullOrWhiteSpace(bundle.AccessToken)
                && bundle.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return bundle.AccessToken;
            }

            // Todoist refresh
            if (string.Equals(connectorId, TodoistId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(bundle.RefreshToken))
                {
                    if (!string.IsNullOrWhiteSpace(bundle.AccessToken))
                        return bundle.AccessToken;
                    throw new InvalidOperationException("Todoist session expired. Disconnect and Connect Todoist again.");
                }

                McpTokenBundle todoistRefreshed = await _todoistOAuth.RefreshAsync(
                        McpOAuthConfig.ResolveTodoistClientId(),
                        McpOAuthConfig.ResolveTodoistClientSecret(),
                        bundle,
                        token)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(todoistRefreshed.AccountEmail))
                    todoistRefreshed.AccountEmail = bundle.AccountEmail;
                lock (_gate)
                {
                    _state.Tokens[connectorId] = todoistRefreshed;
                    ApplyTokenToConnector(RequireConnector(connectorId), todoistRefreshed);
                    _store.Save(_state);
                }
                Changed?.Invoke();
                return todoistRefreshed.AccessToken;
            }

            // Google (Gmail / Drive)
            if (string.IsNullOrWhiteSpace(bundle.RefreshToken))
            {
                // Last resort: still try the access token if present (may work until Google rejects it).
                if (!string.IsNullOrWhiteSpace(bundle.AccessToken))
                    return bundle.AccessToken;
                throw new InvalidOperationException("Google session expired. Disconnect and Connect Google again in Settings.");
            }

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                throw new InvalidOperationException("Google OAuth client is not configured; cannot refresh the session.");

            McpTokenBundle refreshed = await _oauth.RefreshAsync(clientId, clientSecret, bundle, token)
                .ConfigureAwait(false);
            // Keep AccountEmail from the old bundle if Google refresh omits it.
            if (string.IsNullOrWhiteSpace(refreshed.AccountEmail))
                refreshed.AccountEmail = bundle.AccountEmail;
            if (string.IsNullOrWhiteSpace(refreshed.Scope))
                refreshed.Scope = bundle.Scope;

            lock (_gate)
            {
                // Always keep Gmail + Drive tokens in sync (same Google account session).
                if (string.Equals(connectorId, GmailId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(connectorId, GoogleDriveId, StringComparison.OrdinalIgnoreCase))
                {
                    StoreGoogleTokens(refreshed);
                }
                else
                {
                    _state.Tokens[connectorId] = refreshed;
                    ApplyTokenToConnector(RequireConnector(connectorId), refreshed);
                    _store.Save(_state);
                }
            }

            Changed?.Invoke();
            return refreshed.AccessToken;
        }

        private void RefreshConnectionFlagsFromState()
        {
            lock (_gate)
            {
                foreach (McpConnectorInfo connector in _connectors)
                {
                    if (_state.Tokens.TryGetValue(connector.Id, out McpTokenBundle? bundle)
                        && bundle != null
                        && HasUsableSession(bundle))
                    {
                        ApplyTokenToConnector(connector, bundle);
                    }
                    else
                    {
                        connector.IsConnected = false;
                        connector.AccountLabel = null;
                        connector.ConnectedAtUtc = null;
                    }
                }
            }
        }

        /// <summary>Connected if we have an access token and/or a refresh token to renew it.</summary>
        private static bool HasUsableSession(McpTokenBundle tokens)
        {
            if (tokens == null)
                return false;
            return !string.IsNullOrWhiteSpace(tokens.AccessToken)
                || !string.IsNullOrWhiteSpace(tokens.RefreshToken);
        }

        private static void ApplyTokenToConnector(McpConnectorInfo connector, McpTokenBundle tokens)
        {
            connector.IsConnected = HasUsableSession(tokens);
            connector.AccountLabel = string.IsNullOrWhiteSpace(tokens.AccountEmail) ? "Connected" : tokens.AccountEmail;
            // Preserve original connect time when reloading; only stamp if missing.
            if (connector.ConnectedAtUtc == null)
                connector.ConnectedAtUtc = DateTime.UtcNow;
        }

        /// <summary>Google access tokens are shared across Gmail + Drive — refresh both slots together.</summary>
        private void StoreGoogleTokens(McpTokenBundle tokens)
        {
            _state.Tokens[GmailId] = CloneToken(tokens);
            _state.Tokens[GoogleDriveId] = CloneToken(tokens);
            ApplyTokenToConnector(RequireConnector(GmailId), tokens);
            ApplyTokenToConnector(RequireConnector(GoogleDriveId), tokens);
            _store.Save(_state);
        }

        private McpConnectorInfo RequireConnector(string connectorId)
        {
            McpConnectorInfo? connector = _connectors.FirstOrDefault(c =>
                string.Equals(c.Id, connectorId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Handle, connectorId, StringComparison.OrdinalIgnoreCase));
            if (connector == null)
                throw new InvalidOperationException("Unknown connector: " + connectorId);
            return connector;
        }

        private static McpConnectorInfo CloneInfo(McpConnectorInfo source)
        {
            return new McpConnectorInfo
            {
                Id = source.Id,
                Handle = source.Handle,
                DisplayName = source.DisplayName,
                Description = source.Description,
                Kind = source.Kind,
                LogoGlyph = source.LogoGlyph,
                IsConnected = source.IsConnected,
                AccountLabel = source.AccountLabel,
                ConnectedAtUtc = source.ConnectedAtUtc
            };
        }
    }
}
