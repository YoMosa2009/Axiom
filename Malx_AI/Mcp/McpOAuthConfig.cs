using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Malx_AI.Mcp
{
    /// <summary>
    /// Resolves OAuth <b>app</b> credentials (client id/secret) used for browser sign-in.
    /// End users never paste these in normal flow — same model as shipping a Desktop OAuth app.
    /// <para>
    /// Resolution order:
    /// environment variables → active profile folder → machine SharedOAuth folder →
    /// legacy Axiom / Axiom-Dev folders → optional release built-ins.
    /// </para>
    /// App credentials are machine-shared (not chat data). User connection tokens stay
    /// per-profile in the DPAPI connector store.
    /// </summary>
    internal static class McpOAuthConfig
    {
        public const string ClientIdEnvironmentVariable = "AXIOM_GOOGLE_OAUTH_CLIENT_ID";
        public const string ClientSecretEnvironmentVariable = "AXIOM_GOOGLE_OAUTH_CLIENT_SECRET";
        public const string GitHubClientIdEnvironmentVariable = "AXIOM_GITHUB_OAUTH_CLIENT_ID";
        public const string GitHubClientSecretEnvironmentVariable = "AXIOM_GITHUB_OAUTH_CLIENT_SECRET";
        public const string TodoistClientIdEnvironmentVariable = "AXIOM_TODOIST_CLIENT_ID";
        public const string TodoistClientSecretEnvironmentVariable = "AXIOM_TODOIST_CLIENT_SECRET";

        // Backward-compatible names used by older guidance / messages.
        public static string BuiltInGoogleDesktopClientId => McpOAuthBuiltIns.GoogleClientId;
        public static string BuiltInGoogleDesktopClientSecret => McpOAuthBuiltIns.GoogleClientSecret;
        public static string BuiltInGitHubClientId => McpOAuthBuiltIns.GitHubClientId;
        public static string BuiltInGitHubClientSecret => McpOAuthBuiltIns.GitHubClientSecret;
        public static string BuiltInTodoistClientId => McpOAuthBuiltIns.TodoistClientId;
        public static string BuiltInTodoistClientSecret => McpOAuthBuiltIns.TodoistClientSecret;

        private const string GoogleIdFile = "google_oauth_client_id.txt";
        private const string GoogleSecretFile = "google_oauth_client_secret.txt";
        private const string GitHubIdFile = "github_oauth_client_id.txt";
        private const string GitHubSecretFile = "github_oauth_client_secret.txt";
        private const string TodoistIdFile = "todoist_client_id.txt";
        private const string TodoistSecretFile = "todoist_client_secret.txt";

        private static readonly object HydrateGate = new();
        private static bool _hydrated;

        /// <summary>
        /// Machine-wide OAuth app credential folder shared by Debug (Axiom-Dev) and Release (Axiom).
        /// </summary>
        public static string SharedOAuthRoot
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "Axiom", "SharedOAuth");
            }
        }

        public static string ClientIdOverrideFilePath => Path.Combine(AppDataPaths.Root, GoogleIdFile);
        public static string ClientSecretOverrideFilePath => Path.Combine(AppDataPaths.Root, GoogleSecretFile);
        public static string GitHubClientIdOverrideFilePath => Path.Combine(AppDataPaths.Root, GitHubIdFile);
        public static string GitHubClientSecretOverrideFilePath => Path.Combine(AppDataPaths.Root, GitHubSecretFile);
        public static string TodoistClientIdOverrideFilePath => Path.Combine(AppDataPaths.Root, TodoistIdFile);
        public static string TodoistClientSecretOverrideFilePath => Path.Combine(AppDataPaths.Root, TodoistSecretFile);

        public static string SharedGoogleClientIdPath => Path.Combine(SharedOAuthRoot, GoogleIdFile);
        public static string SharedGoogleClientSecretPath => Path.Combine(SharedOAuthRoot, GoogleSecretFile);
        public static string SharedGitHubClientIdPath => Path.Combine(SharedOAuthRoot, GitHubIdFile);
        public static string SharedGitHubClientSecretPath => Path.Combine(SharedOAuthRoot, GitHubSecretFile);
        public static string SharedTodoistClientIdPath => Path.Combine(SharedOAuthRoot, TodoistIdFile);
        public static string SharedTodoistClientSecretPath => Path.Combine(SharedOAuthRoot, TodoistSecretFile);

        /// <summary>
        /// Pulls existing OAuth app credentials from legacy profile folders into SharedOAuth
        /// so Release/Debug/published builds all see the same app setup.
        /// Safe to call repeatedly.
        /// </summary>
        public static void EnsureSharedCredentialsHydrated()
        {
            if (_hydrated)
                return;

            lock (HydrateGate)
            {
                if (_hydrated)
                    return;

                try
                {
                    Directory.CreateDirectory(SharedOAuthRoot);

                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var sources = new List<string>
                    {
                        AppDataPaths.Root,
                        Path.Combine(local, "Axiom"),
                        Path.Combine(local, "Axiom-Dev"),
                    };

                    string[] fileNames =
                    {
                        GoogleIdFile, GoogleSecretFile,
                        GitHubIdFile, GitHubSecretFile,
                        TodoistIdFile, TodoistSecretFile
                    };

                    foreach (string fileName in fileNames)
                    {
                        string sharedPath = Path.Combine(SharedOAuthRoot, fileName);
                        if (FileHasContent(sharedPath))
                            continue;

                        foreach (string sourceDir in sources.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                                continue;

                            string candidate = Path.Combine(sourceDir, fileName);
                            if (!FileHasContent(candidate))
                                continue;

                            try
                            {
                                File.Copy(candidate, sharedPath, overwrite: false);
                                break;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"OAuth credential migrate {fileName}: {ex.Message}");
                            }
                        }
                    }

                    // Keep a copy in the active profile too (older docs / tooling look there).
                    MirrorSharedIntoDirectory(AppDataPaths.Root);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OAuth shared hydrate error: {ex.Message}");
                }
                finally
                {
                    _hydrated = true;
                }
            }
        }

        public static string ResolveGoogleClientId(string? settingsOverride = null)
            => ResolveCredential(
                ClientIdEnvironmentVariable,
                GoogleIdFile,
                settingsOverride,
                BuiltInGoogleDesktopClientId);

        public static string ResolveGoogleClientSecret()
            => ResolveCredential(
                ClientSecretEnvironmentVariable,
                GoogleSecretFile,
                settingsOverride: null,
                BuiltInGoogleDesktopClientSecret);

        public static bool HasResolvableClientId(string? settingsOverride = null)
            => !string.IsNullOrWhiteSpace(ResolveGoogleClientId(settingsOverride));

        public static string ResolveGitHubClientId()
            => ResolveCredential(
                GitHubClientIdEnvironmentVariable,
                GitHubIdFile,
                settingsOverride: null,
                BuiltInGitHubClientId);

        public static string ResolveGitHubClientSecret()
            => ResolveCredential(
                GitHubClientSecretEnvironmentVariable,
                GitHubSecretFile,
                settingsOverride: null,
                BuiltInGitHubClientSecret);

        public static string ResolveTodoistClientId()
            => ResolveCredential(
                TodoistClientIdEnvironmentVariable,
                TodoistIdFile,
                settingsOverride: null,
                BuiltInTodoistClientId);

        public static string ResolveTodoistClientSecret()
            => ResolveCredential(
                TodoistClientSecretEnvironmentVariable,
                TodoistSecretFile,
                settingsOverride: null,
                BuiltInTodoistClientSecret);

        public static bool IsGoogleAppConfigured()
            => !string.IsNullOrWhiteSpace(ResolveGoogleClientId())
               && !string.IsNullOrWhiteSpace(ResolveGoogleClientSecret());

        public static bool IsGitHubAppConfigured()
            => !string.IsNullOrWhiteSpace(ResolveGitHubClientId());

        public static bool IsTodoistAppConfigured()
            => !string.IsNullOrWhiteSpace(ResolveTodoistClientId())
               && !string.IsNullOrWhiteSpace(ResolveTodoistClientSecret());

        public static string BuildGoogleSetupMessage()
            => "Google sign-in is not set up yet.\n\n" +
               "Axiom needs a Google Cloud Desktop OAuth client id + secret " +
               "so Connect can open a single browser login.\n\n" +
               "Place them here (one value per file):\n" +
               $"  {SharedGoogleClientIdPath}\n" +
               $"  {SharedGoogleClientSecretPath}\n\n" +
               "Or use environment variables AXIOM_GOOGLE_OAUTH_CLIENT_ID / AXIOM_GOOGLE_OAUTH_CLIENT_SECRET.\n" +
               "Settings → Connectors → Open OAuth app folder.";

        public static string BuildGitHubSetupMessage()
            => "GitHub OAuth is not configured yet.\n\n" +
               "1) GitHub → Settings → Developer settings → OAuth Apps → New OAuth App\n" +
               "2) Homepage: http://127.0.0.1   Callback: http://127.0.0.1\n" +
               "3) Enable Device Flow on the app\n" +
               "4) Save Client ID (and Secret) as:\n" +
               $"  {SharedGitHubClientIdPath}\n" +
               $"  {SharedGitHubClientSecretPath}\n\n" +
               "Or set AXIOM_GITHUB_OAUTH_CLIENT_ID / AXIOM_GITHUB_OAUTH_CLIENT_SECRET.\n" +
               "Settings → Connectors → Open OAuth app folder.";

        public static string BuildTodoistSetupMessage()
            => "Todoist Client ID/Secret are not configured.\n\n" +
               "1) Open https://app.todoist.com/app/settings/integrations/app-management\n" +
               "2) Create an app and set Redirect URI exactly:\n" +
               "   http://127.0.0.1:17466/oauth2/callback/\n" +
               "3) Save Client ID and Secret as:\n" +
               $"  {SharedTodoistClientIdPath}\n" +
               $"  {SharedTodoistClientSecretPath}\n\n" +
               "Or set AXIOM_TODOIST_CLIENT_ID / AXIOM_TODOIST_CLIENT_SECRET.\n" +
               "Settings → Connectors → Open OAuth app folder.";

        /// <summary>Writes a credential into SharedOAuth + active profile (does not log the value).</summary>
        public static void SaveSharedCredential(string fileName, string value)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("File name is required.", nameof(fileName));

            string normalized = (value ?? string.Empty).Trim();
            Directory.CreateDirectory(SharedOAuthRoot);
            Directory.CreateDirectory(AppDataPaths.Root);

            string sharedPath = Path.Combine(SharedOAuthRoot, fileName);
            string profilePath = Path.Combine(AppDataPaths.Root, fileName);
            File.WriteAllText(sharedPath, normalized, Encoding.UTF8);
            File.WriteAllText(profilePath, normalized, Encoding.UTF8);
            _hydrated = true;
        }

        private static string ResolveCredential(
            string environmentVariable,
            string fileName,
            string? settingsOverride,
            string builtIn)
        {
            EnsureSharedCredentialsHydrated();

            string fromEnv = Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv.Trim();

            foreach (string directory in EnumerateCredentialDirectories())
            {
                string path = Path.Combine(directory, fileName);
                if (TryReadFile(path, out string fromFile))
                    return fromFile;
            }

            if (!string.IsNullOrWhiteSpace(settingsOverride))
                return settingsOverride.Trim();

            if (!string.IsNullOrWhiteSpace(builtIn))
                return builtIn.Trim();

            return string.Empty;
        }

        private static IEnumerable<string> EnumerateCredentialDirectories()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return AppDataPaths.Root;
            yield return SharedOAuthRoot;
            yield return Path.Combine(local, "Axiom");
            yield return Path.Combine(local, "Axiom-Dev");
        }

        private static void MirrorSharedIntoDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            try
            {
                Directory.CreateDirectory(directory);
                if (!Directory.Exists(SharedOAuthRoot))
                    return;

                foreach (string source in Directory.GetFiles(SharedOAuthRoot, "*.txt"))
                {
                    string name = Path.GetFileName(source);
                    string dest = Path.Combine(directory, name);
                    if (FileHasContent(dest))
                        continue;
                    if (!FileHasContent(source))
                        continue;
                    File.Copy(source, dest, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OAuth mirror error: {ex.Message}");
            }
        }

        private static bool TryReadFile(string path, out string value)
        {
            value = string.Empty;
            try
            {
                if (!File.Exists(path))
                    return false;
                string text = File.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return false;
                value = text;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool FileHasContent(string path)
        {
            try
            {
                return File.Exists(path) && !string.IsNullOrWhiteSpace(File.ReadAllText(path));
            }
            catch
            {
                return false;
            }
        }
    }
}
