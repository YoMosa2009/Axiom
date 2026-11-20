namespace Malx_AI
{
    /// <summary>
    /// Optional release-time Axiom Cloud subscription gateway URL.
    /// Defaults to empty in public source. Release packaging can generate
    /// <c>AxiomAccountBuiltIns.Generated.cs</c> (gitignored) from build secrets
    /// without committing deployment URLs to git.
    /// </summary>
    internal static partial class AxiomAccountBuiltIns
    {
        public static string ManagedGatewayUrl
        {
            get
            {
                string value = string.Empty;
                TryGetManagedGatewayUrl(ref value);
                return value ?? string.Empty;
            }
        }

        static partial void TryGetManagedGatewayUrl(ref string value);
    }
}
