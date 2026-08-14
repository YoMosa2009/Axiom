namespace Malx_AI.Mcp
{
    /// <summary>
    /// Optional release-time OAuth app credentials.
    /// Defaults are empty in public source. Publish can generate
    /// <c>McpOAuthBuiltIns.Generated.cs</c> (gitignored) so shipped builds
    /// include Desktop OAuth client id/secret pairs without committing them to git.
    /// </summary>
    internal static partial class McpOAuthBuiltIns
    {
        public static string GoogleClientId
        {
            get
            {
                string value = string.Empty;
                TryGetGoogleClientId(ref value);
                return value ?? string.Empty;
            }
        }

        public static string GoogleClientSecret
        {
            get
            {
                string value = string.Empty;
                TryGetGoogleClientSecret(ref value);
                return value ?? string.Empty;
            }
        }

        public static string GitHubClientId
        {
            get
            {
                string value = string.Empty;
                TryGetGitHubClientId(ref value);
                return value ?? string.Empty;
            }
        }

        public static string GitHubClientSecret
        {
            get
            {
                string value = string.Empty;
                TryGetGitHubClientSecret(ref value);
                return value ?? string.Empty;
            }
        }

        public static string TodoistClientId
        {
            get
            {
                string value = string.Empty;
                TryGetTodoistClientId(ref value);
                return value ?? string.Empty;
            }
        }

        public static string TodoistClientSecret
        {
            get
            {
                string value = string.Empty;
                TryGetTodoistClientSecret(ref value);
                return value ?? string.Empty;
            }
        }

        static partial void TryGetGoogleClientId(ref string value);
        static partial void TryGetGoogleClientSecret(ref string value);
        static partial void TryGetGitHubClientId(ref string value);
        static partial void TryGetGitHubClientSecret(ref string value);
        static partial void TryGetTodoistClientId(ref string value);
        static partial void TryGetTodoistClientSecret(ref string value);
    }
}
