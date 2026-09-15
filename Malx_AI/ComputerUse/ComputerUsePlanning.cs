using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.ComputerUse;

internal static class ComputerUsePlanning
{
    internal sealed record Result(ComputerUseTaskContract? Contract, string Error, int Attempts, string RawPlan, string Warning = "");

    public static async Task<Result> CreateAsync(string request, string navigationContext,
        Func<string, string, CancellationToken, Task<string>> inferAsync, CancellationToken token,
        Func<int, string, string, Task>? onRejected = null)
    {
        string error = "", raw = "";
        const int attempts = 3;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            string payload = JsonSerializer.Serialize(new
            {
                request,
                navigation_context = navigationContext,
                validation_error = error,
                previous_plan = raw.Length <= 16000 ? raw : raw[..16000],
                instruction = attempt == 1 ? "Define every requested outcome."
                    : "Correct the validation error and return the complete plan. Preserve all requested outcomes; do not execute desktop actions."
            });
            // The correction has to lead, not sit in a payload field. The observed failure was a
            // deterministic local model returning three byte-identical plans, because nothing it
            // weighted heavily had changed between attempts.
            string systemPrompt = attempt == 1
                ? ComputerUseTaskContract.SystemPrompt
                : ComputerUseTaskContract.SystemPrompt
                  + "\n\nPLAN CORRECTION REQUIRED. Your previous plan was rejected:\n"
                  + error
                  + "\nReturn the corrected plan now. Change exactly what that error names and keep every"
                  + " other outcome identical. Returning the same plan again will fail the same way.";
            // In production inference and status callbacks own WPF state. Keep the caller's context.
            raw = await inferAsync(systemPrompt, payload, token).ConfigureAwait(true);
            if (ComputerUseTaskContract.TryParse(raw, request, out var contract, out error, out string advisory, strictCitations: true))
                return new(contract, "", attempt, raw, advisory);
            if (onRejected != null)
                await onRejected(attempt, error, raw).ConfigureAwait(true);
        }

        // Retries are spent. If what is left fails only on how the request was quoted, run it: a
        // plan with real destinations and no invented instructions is worth far more than ending
        // the session having done nothing, and every destination, application and tab is still
        // verified from the screen before anything counts as complete. The shortfall is reported
        // rather than hidden, so a genuinely incomplete plan is visible to the user.
        if (ComputerUseTaskContract.TryParse(raw, request, out var salvaged, out _, out string salvageAdvisory, strictCitations: false))
        {
            string warning = "The plan could not be fully validated after " + attempts + " attempts, so Computer Use is proceeding with the closest usable plan. "
                + salvageAdvisory;
            return new(salvaged, "", attempts, raw, warning.Trim());
        }

        return new(null, error, attempts, raw);
    }
}
