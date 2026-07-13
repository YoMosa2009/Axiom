using System;
using System.IO;

namespace Malx_AI.Mcp
{
    /// <summary>
    /// Resolves Google OAuth Desktop credentials used for seamless browser sign-in.
    /// End users never paste these — same model as Claude/ChatGPT shipping their own OAuth app.
    /// <para>
    /// Google Desktop clients are issued both a client id and a client secret. The secret is not
    /// truly private for installed apps (it ships in the binary), but the token endpoint still
    /// requires it for many client types — without it exchange fails with "client_secret is missing".
    /// </para>
    /// </summary>
    internal static class McpOAuthConfig
    {
        /// <summary>
        /// Optional release-time Desktop OAuth client id from Google Cloud Console → Credentials.
        /// Leave empty in public source; set via env / AppData override files for local dev,
        /// or fill only in private release builds that never hit a public remote.
        /// </summary>
        public const string BuiltInGoogleDesktopClientId = "";

        /// <summary>
        /// Optional Desktop OAuth client secret (required by Google's token endpoint for many client types).
        /// Do not commit real secrets — use AXIOM_GOOGLE_OAUTH_CLIENT_SECRET or
        /// %LocalAppData%\Axiom\google_oauth_client_secret.txt.
        /// </summary>
        public const string BuiltInGoogleDesktopClientSecret = "";

        /// <summary>
        /// Optional GitHub OAuth App client id (Settings → Developer settings → OAuth Apps).
        /// Enable Device Flow. Prefer env / AppData overrides over committing credentials.
        /// </summary>
        public const string BuiltInGitHubClientId = "";

        /// <summary>
        /// Optional GitHub OAuth App client secret for device-flow token polling.
        /// Prefer AXIOM_GITHUB_OAUTH_CLIENT_SECRET or github_oauth_client_secret.txt under AppData.
        /// </summary>
        public const string BuiltInGitHubClientSecret = "";

        /// <summary>
        /// Optional Todoist app Client ID. Redirect URI must be exactly:
        /// http://127.0.0.1:17466/oauth2/callback/
        /// Prefer env / AppData overrides over committing credentials.
        /// </summary>
        public const string BuiltInTodoistClientId = "";

        /// <summary>
        /// Optional Todoist app Client Secret. Prefer AXIOM_TODOIST_CLIENT_SECRET or
        /// todoist_client_secret.txt under AppData.
        /// </summary>
        public const string BuiltInTodoistClientSecret = "";

        public const string ClientIdEnvironmentVariable = "AXIOM_GOOGLE_OAUTH_CLIENT_ID";
        public const string ClientSecretEnvironmentVariable = "AXIOM_GOOGLE_OAUTH_CLIENT_SECRET";
        public const string GitHubClientIdEnvironmentVariable = "AXIOM_GITHUB_OAUTH_CLIENT_ID";
        public const string GitHubClientSecretEnvironmentVariable = "AXIOM_GITHUB_OAUTH_CLIENT_SECRET";
        public const string TodoistClientIdEnvironmentVariable = "AXIOM_TODOIST_CLIENT_ID";
        public const string TodoistClientSecretEnvironmentVariable = "AXIOM_TODOIST_CLIENT_SECRET";

        public static string ClientIdOverrideFilePath =>
            Path.Combine(AppDataPaths.Root, "google_oauth_client_id.txt");

        public static string ClientSecretOverrideFilePath =>
            Path.Combine(AppDataPaths.Root, "google_oauth_client_secret.txt");

        public static string GitHubClientIdOverrideFilePath =>
            Path.Combine(AppDataPaths.Root, "github_oauth_client_id.txt");

        public static string GitHubClientSecretOverrideFilePath =>
            Path.Combine(AppDataPaths.Root, "github_oauth_client_secret.txt");

        public static string TodoistClientIdOverrideFilePath =>
            Path.Combine(AppDataPaths.Root, "todoist_client_id.txt");

        public static string TodoistClientSecretOverrideFilePath =>
            Path.Combine(AppDataPaths.Root, "todoist_client_secret.txt");

        public static string ResolveGoogleClientId(string? settingsOverride = null)
        {
            string fromEnv = Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv.Trim();

            try
            {
                if (File.Exists(ClientIdOverrideFilePath))
                {
                    string fromFile = File.ReadAllText(ClientIdOverrideFilePath).Trim();
                    if (!string.IsNullOrWhiteSpace(fromFile))
                        return fromFile;
                }
            }
            catch
            {
                // ignore — fall through
            }

            if (!string.IsNullOrWhiteSpace(settingsOverride))
                return settingsOverride.Trim();

            if (!string.IsNullOrWhiteSpace(BuiltInGoogleDesktopClientId))
                return BuiltInGoogleDesktopClientId.Trim();

            return string.Empty;
        }

        public static string ResolveGoogleClientSecret()
        {
            string fromEnv = Environment.GetEnvironmentVariable(ClientSecretEnvironmentVariable) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv.Trim();

            try
            {
                if (File.Exists(ClientSecretOverrideFilePath))
                {
                    string fromFile = File.ReadAllText(ClientSecretOverrideFilePath).Trim();
                    if (!string.IsNullOrWhiteSpace(fromFile))
                        return fromFile;
                }
            }
            catch
            {
                // ignore
            }

            if (!string.IsNullOrWhiteSpace(BuiltInGoogleDesktopClientSecret))
                return BuiltInGoogleDesktopClientSecret.Trim();

            return string.Empty;
        }

        public static bool HasResolvableClientId(string? settingsOverride = null)
            => !string.IsNullOrWhiteSpace(ResolveGoogleClientId(settingsOverride));

        public static string ResolveGitHubClientId()
        {
            string fromEnv = Environment.GetEnvironmentVariable(GitHubClientIdEnvironmentVariable) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv.Trim();
            try
            {
                if (File.Exists(GitHubClientIdOverrideFilePath))
                {
                    string fromFile = File.ReadAllText(GitHubClientIdOverrideFilePath).Trim();
                    if (!string.IsNullOrWhiteSpace(fromFile))
                        return fromFile;
                }
            }
            catch { /* ignore */ }

            return string.IsNullOrWhiteSpace(BuiltInGitHubClientId) ? string.Empty : BuiltInGitHubClientId.Trim();
        }

        public static string ResolveGitHubClientSecret()
        {
            string fromEnv = Environment.GetEnvironmentVariable(GitHubClientSecretEnvironmentVariable) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv.Trim();
            try
            {
                if (File.Exists(GitHubClientSecretOverrideFilePath))
                {
                    string fromFile = File.ReadAllText(GitHubClientSecretOverrideFilePath).Trim();
                    if (!string.IsNullOrWhiteSpace(fromFile))
                        return fromFile;
                }
            }
            catch { /* ignore */ }

            return string.IsNullOrWhiteSpace(BuiltInGitHubClientSecret) ? string.Empty : BuiltInGitHubClientSecret.Trim();
        }

        public static string ResolveTodoistClientId()
        {
            string fromEnv = Environment.GetEnvironmentVariable(TodoistClientIdEnvironmentVariable) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv.Trim();
            try
            {
                if (File.Exists(TodoistClientIdOverrideFilePath))
                {
                    string fromFile = File.ReadAllText(TodoistClientIdOverrideFilePath).Trim();
                    if (!string.IsNullOrWhiteSpace(fromFile))
                        return fromFile;
                }
            }
            catch { /* ignore */ }

            return string.IsNullOrWhiteSpace(BuiltInTodoistClientId) ? string.Empty : BuiltInTodoistClientId.Trim();
        }

        public static string ResolveTodoistClientSecret()
        {
            string fromEnv = Environment.GetEnvironmentVariable(TodoistClientSecretEnvironmentVariable) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv.Trim();
            try
            {
                if (File.Exists(TodoistClientSecretOverrideFilePath))
                {
                    string fromFile = File.ReadAllText(TodoistClientSecretOverrideFilePath).Trim();
                    if (!string.IsNullOrWhiteSpace(fromFile))
                        return fromFile;
                }
            }
            catch { /* ignore */ }

            return string.IsNullOrWhiteSpace(BuiltInTodoistClientSecret) ? string.Empty : BuiltInTodoistClientSecret.Trim();
        }
    }
}
