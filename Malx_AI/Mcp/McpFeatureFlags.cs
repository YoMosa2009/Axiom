namespace Malx_AI.Mcp
{
    /// <summary>
    /// Feature gates for connectors that are built or pending external approval.
    /// Flip these to true when a connector is ready for users.
    /// </summary>
    internal static class McpFeatureFlags
    {
        /// <summary>
        /// Dropbox is pending Dropbox App Console review. Keep hidden until approved.
        /// Set to true when Dropbox OAuth is ready to ship.
        /// </summary>
        public const bool DropboxConnectorEnabled = false;
    }
}
