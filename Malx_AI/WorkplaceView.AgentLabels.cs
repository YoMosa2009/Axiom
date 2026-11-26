namespace Malx_AI
{
    public partial class WorkplaceView
    {
        /// <summary>
        /// The model name an agent card shows in its header: the cloud or Hybrid Local model in
        /// cloud mode, otherwise the local model acting as Builder / single agent.
        /// </summary>
        private string GetAgentCardModelName()
        {
            try
            {
                if (_isCloudModeEnabled)
                    return WorkplaceCloudRoleDisplayName;
                string name = GetEffectiveRoleConfig(CouncilRole.Builder).DisplayName;
                return string.IsNullOrWhiteSpace(name) || name == "No model selected" ? "Agent" : name;
            }
            catch (System.Exception)
            {
                return "Agent"; // A label must never break a run.
            }
        }
    }
}
