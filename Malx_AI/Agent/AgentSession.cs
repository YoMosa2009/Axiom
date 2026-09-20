using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
    /// <summary>Asks the model for its next move. The transcript already carries the history.</summary>
    public delegate Task<string> AgentModelInvoker(string systemPrompt, string transcript, CancellationToken token);

    /// <summary>Asks the user to approve one call. Returns the user's decision.</summary>
    public delegate Task<AgentApprovalOutcome> AgentApprovalRequest(AgentToolCall call, string reason, CancellationToken token);

    /// <summary>Reports the running step to the UI. A null label means "nothing is running".</summary>
    public delegate void AgentActivityReporter(string? label);

    public enum AgentApprovalOutcome
    {
        Approve,
        ApproveAlways,
        Deny
    }

    public sealed record AgentStep(AgentToolCall Call, AgentPermission Permission, AgentToolResult? Result, string Note);

    public sealed record AgentRunResult(
        string FinalMessage,
        IReadOnlyList<AgentStep> Steps,
        bool StoppedOnStepLimit,
        bool Cancelled);

    /// <summary>
    /// The agent loop: ask the model, run what it asks for, feed the result back, repeat.
    /// </summary>
    /// <remarks>
    /// The model, the approval prompt, and the activity reporter all arrive as delegates so the
    /// loop itself has no dependency on WPF or on any particular inference path. That is what lets
    /// Local, Hybrid Local, and Cloud share one implementation, and it is what makes the loop
    /// testable without a model.
    /// </remarks>
    public sealed class AgentSession
    {
        private readonly AgentToolExecutor _executor;
        private readonly AgentScope _scope;
        private readonly AgentTier _tier;
        private readonly List<string> _sessionAllowList = new();

        public AgentSession(AgentScope scope, AgentTier tier, AgentToolExecutor? executor = null)
        {
            _scope = scope ?? AgentScope.WholeComputer();
            _tier = tier;
            _executor = executor ?? new AgentToolExecutor(_scope);
        }

        public async Task<AgentRunResult> RunAsync(
            string goal,
            AgentApprovalMode mode,
            AgentModelInvoker invokeModel,
            AgentApprovalRequest requestApproval,
            AgentActivityReporter reportActivity,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(invokeModel);
            ArgumentNullException.ThrowIfNull(requestApproval);

            string systemPrompt = AgentPromptBuilder.Build(_tier, _scope, mode);
            int maxSteps = AgentPromptBuilder.MaxStepsFor(_tier);

            var steps = new List<AgentStep>();
            var transcript = new StringBuilder();
            transcript.Append("Task: ").AppendLine(goal);

            try
            {
                for (int step = 0; step < maxSteps; step++)
                {
                    token.ThrowIfCancellationRequested();

                    reportActivity?.Invoke("Thinking");
                    string reply = await invokeModel(systemPrompt, transcript.ToString(), token);
                    reportActivity?.Invoke(null);

                    if (!AgentToolCallParser.TryParse(reply, out AgentToolCall? call, out string? parseError))
                    {
                        if (parseError == null)
                            return new AgentRunResult(reply.Trim(), steps, false, false);

                        // A malformed call is recoverable: tell the model what was wrong and retry.
                        transcript.AppendLine().Append("Assistant: ").AppendLine(reply.Trim());
                        transcript.Append("Tool error: ").AppendLine(parseError);
                        continue;
                    }

                    if (string.Equals(call!.Tool, AgentToolNames.Finish, StringComparison.OrdinalIgnoreCase))
                    {
                        string summary = call.Arg("summary");
                        steps.Add(new AgentStep(call, AgentPermission.Allow, AgentToolResult.Ok("done"), "finished"));
                        return new AgentRunResult(
                            string.IsNullOrWhiteSpace(summary) ? "Done." : summary,
                            steps,
                            false,
                            false);
                    }

                    AgentPermissionDecision decision =
                        AgentPermissionPolicy.Evaluate(call, mode, _sessionAllowList);

                    if (decision.Permission == AgentPermission.Deny)
                    {
                        steps.Add(new AgentStep(call, AgentPermission.Deny, null, decision.Reason));
                        transcript.AppendLine().Append("Assistant called: ").AppendLine(call.DescribeShort());
                        transcript.Append("Refused: ").AppendLine(decision.Reason);
                        continue;
                    }

                    if (decision.Permission == AgentPermission.Ask)
                    {
                        reportActivity?.Invoke("Waiting for approval");
                        AgentApprovalOutcome outcome = await requestApproval(call, decision.Reason, token);
                        reportActivity?.Invoke(null);

                        if (outcome == AgentApprovalOutcome.Deny)
                        {
                            steps.Add(new AgentStep(call, AgentPermission.Ask, null, "denied by the user"));
                            transcript.AppendLine().Append("Assistant called: ").AppendLine(call.DescribeShort());
                            transcript.AppendLine("The user declined this step. Do not retry it; either work around it or explain what you need.");
                            continue;
                        }

                        if (outcome == AgentApprovalOutcome.ApproveAlways
                            && string.Equals(call.Tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase))
                        {
                            string key = AgentPermissionPolicy.AllowListKeyFor(call.Arg("command"));
                            if (key.Length > 0 && !_sessionAllowList.Contains(key))
                                _sessionAllowList.Add(key);
                        }
                    }

                    reportActivity?.Invoke(call.DescribeShort());
                    AgentToolResult result;
                    try
                    {
                        result = await _executor.ExecuteAsync(call, token);
                    }
                    finally
                    {
                        reportActivity?.Invoke(null);
                    }

                    steps.Add(new AgentStep(call, decision.Permission, result, decision.Reason));
                    transcript.AppendLine().Append("Assistant called: ").AppendLine(call.DescribeShort());
                    transcript.Append("Result: ").AppendLine(result.ToObservation(ObservationBudgetFor(_tier)));
                }
            }
            catch (OperationCanceledException)
            {
                reportActivity?.Invoke(null);
                return new AgentRunResult("Stopped.", steps, false, true);
            }

            reportActivity?.Invoke(null);
            return new AgentRunResult(
                $"I reached the {maxSteps}-step limit for this model before finishing. Here is where I got to — ask me to continue if that looks right.",
                steps,
                true,
                false);
        }

        /// <summary>Small models drown in long tool output, so they see less of it.</summary>
        private static int ObservationBudgetFor(AgentTier tier) => tier switch
        {
            AgentTier.Micro => 1200,
            AgentTier.Compact => 3000,
            _ => 6000
        };

        /// <summary>Commands the user chose to stop being asked about, for persistence in the UI layer.</summary>
        public IReadOnlyList<string> SessionAllowList => _sessionAllowList;
    }
}
