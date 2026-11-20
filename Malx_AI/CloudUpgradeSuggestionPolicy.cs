using System;

namespace Malx_AI
{
    internal enum CloudUpgradeTriggerKind
    {
        None,
        SlowLocalGeneration,
        ByokQuotaExhausted,
        LocalVisionUnavailable
    }

    internal sealed record CloudUpgradeSuggestion(
        CloudUpgradeTriggerKind Kind,
        string Headline,
        string Detail,
        string ActionLabel);

    /// <summary>
    /// Evaluates when to show a non-intrusive, dismissible Axiom Cloud suggestion banner
    /// (slow local generation, exhausted BYOK key quota, or image/Computer Use without a local vision projector).
    /// </summary>
    internal static class CloudUpgradeSuggestionPolicy
    {
        internal const double SlowLocalTokensPerSecondThreshold = 8.0;
        internal const int MinimumTokensForSlowCheck = 25;

        public static CloudUpgradeSuggestion? TryEvaluateSlowLocal(
            bool isCloudMode,
            bool hasPaidAxiomSubscription,
            bool alreadyDismissed,
            GenerationStatsSnapshot? stats)
        {
            if (isCloudMode || hasPaidAxiomSubscription || alreadyDismissed || stats is null)
                return null;

            if (stats.OutputTokens < MinimumTokensForSlowCheck)
                return null;

            if (stats.TokensPerSecond <= 0 || double.IsNaN(stats.TokensPerSecond) || double.IsInfinity(stats.TokensPerSecond))
                return null;

            if (stats.TokensPerSecond >= SlowLocalTokensPerSecondThreshold)
                return null;

            return new CloudUpgradeSuggestion(
                CloudUpgradeTriggerKind.SlowLocalGeneration,
                "Running slow on local hardware",
                FormattableString.Invariant($"Last reply ran at {stats.TokensPerSecond:0.0} tok/s. DeepSeek 4.1F-1017 Cloud (~85 tok/s, 1M context) is $9/mo."),
                "Explore Axiom Cloud");
        }

        public static CloudUpgradeSuggestion? TryEvaluateByokExhausted(
            bool isCustomEndpoint,
            bool hasPaidAxiomSubscription,
            bool alreadyDismissed,
            bool isDailyLimit)
        {
            if (isCustomEndpoint || hasPaidAxiomSubscription || alreadyDismissed)
                return null;

            string detail = isDailyLimit
                ? "Your free OpenRouter daily cap is full. Switch to DeepSeek 4.1F-1017 Cloud for 1M context, vision, and paced $9/mo usage."
                : "Your OpenRouter key is out of credits. Switch to Axiom Cloud (DeepSeek 4.1F-1017) for $9/mo with no key setup.";

            return new CloudUpgradeSuggestion(
                CloudUpgradeTriggerKind.ByokQuotaExhausted,
                "OpenRouter quota reached",
                detail,
                "Switch to Axiom Cloud");
        }

        public static CloudUpgradeSuggestion? TryEvaluateLocalVisionMissing(
            bool isCloudMode,
            bool hasPaidAxiomSubscription,
            bool alreadyDismissed,
            bool hasLocalVisionProjector)
        {
            if (isCloudMode || hasPaidAxiomSubscription || alreadyDismissed || hasLocalVisionProjector)
                return null;

            return new CloudUpgradeSuggestion(
                CloudUpgradeTriggerKind.LocalVisionUnavailable,
                "This local model doesn't have image vision loaded",
                "Use Axiom Cloud (DeepSeek 4.1F-1017, native vision + tools) for $9/mo, or pair an mmproj projector locally.",
                "View Axiom Cloud");
        }
    }
}
