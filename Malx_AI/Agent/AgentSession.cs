using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
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
    /// The model arrives as an <see cref="IAgentModel"/> and the approval prompt and activity
    /// reporter as delegates, so the loop has no dependency on WPF or on any particular inference
    /// backend. Local, Hybrid Local, and Cloud share this one implementation, and it is testable
    /// without a model.
    /// </remarks>
    public sealed class AgentSession
    {
        private readonly AgentToolExecutor _executor;
        private readonly AgentScope _scope;
        private readonly int _maxSteps;
        private readonly List<string> _sessionAllowList = new();

        /// <summary>How many unusable turns in a row before the run gives up and says so.</summary>
        private const int MaxConsecutiveProtocolErrors = 3;

        public AgentSession(AgentScope scope, int maxSteps, AgentToolExecutor? executor = null)
        {
            _scope = scope ?? AgentScope.WholeComputer();
            _maxSteps = Math.Max(1, maxSteps);
            _executor = executor ?? new AgentToolExecutor(_scope);
        }

        public async Task<AgentRunResult> RunAsync(
            string goal,
            AgentApprovalMode mode,
            IAgentModel model,
            AgentApprovalRequest requestApproval,
            AgentActivityReporter reportActivity,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(requestApproval);

            if (!string.IsNullOrWhiteSpace(model.Unavailable))
                return new AgentRunResult(model.Unavailable!, [], false, false);

            var steps = new List<AgentStep>();
            var history = new List<AgentExchange>();
            int consecutiveProtocolErrors = 0;

            try
            {
                for (int step = 0; step < _maxSteps; step++)
                {
                    token.ThrowIfCancellationRequested();

                    reportActivity?.Invoke("Thinking");
                    AgentModelReply reply;
                    try
                    {
                        reply = await model.NextAsync(goal, history, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A dropped connection mid-run is not a reason to abandon work already
                        // done. Free-tier providers drop bodies and time out often enough that a
                        // single hiccup must not leave the machine half-changed with no summary.
                        if (++consecutiveProtocolErrors >= MaxConsecutiveProtocolErrors)
                        {
                            return new AgentRunResult(
                                $"The connection to the model kept failing ({ex.Message}). "
                                + "Anything already done is listed above; try again when the provider settles.",
                                steps,
                                false,
                                false);
                        }

                        history.Add(new AgentExchange(
                            new AgentToolCall("(transport)", new Dictionary<string, string>()),
                            "The previous request failed to reach the model. Continue from what is already done."));
                        continue;
                    }
                    finally
                    {
                        reportActivity?.Invoke(null);
                    }

                    if (reply.ProtocolError != null)
                    {
                        // Recoverable: tell the model what was wrong and let it try again. Bounded,
                        // so a model that keeps returning nothing usable reports that honestly
                        // instead of burning every step and claiming success.
                        if (++consecutiveProtocolErrors >= MaxConsecutiveProtocolErrors)
                        {
                            return new AgentRunResult(
                                "The model did not return anything usable after several attempts. "
                                + "That is usually a transient problem with the provider - try again, "
                                + "or switch model in Settings if it keeps happening.",
                                steps,
                                false,
                                false);
                        }

                        history.Add(new AgentExchange(
                            new AgentToolCall("(invalid)", new Dictionary<string, string>()),
                            "Tool error: " + reply.ProtocolError));
                        continue;
                    }

                    consecutiveProtocolErrors = 0;

                    if (reply.Call == null)
                    {
                        string answer = reply.FinalText ?? string.Empty;
                        return new AgentRunResult(
                            string.IsNullOrWhiteSpace(answer) ? "Done." : answer.Trim(),
                            steps,
                            false,
                            false);
                    }

                    AgentToolCall call = reply.Call;
                    if (string.Equals(call.Tool, AgentToolNames.Finish, StringComparison.OrdinalIgnoreCase))
                    {
                        string summary = call.Arg("summary");
                        steps.Add(new AgentStep(call, AgentPermission.Allow, AgentToolResult.Ok("done"), "finished"));
                        return new AgentRunResult(
                            string.IsNullOrWhiteSpace(summary) ? "Done." : summary,
                            steps,
                            false,
                            false);
                    }

                    AgentPermissionDecision decision = AgentPermissionPolicy.Evaluate(call, mode, _sessionAllowList);

                    if (decision.Permission == AgentPermission.Deny)
                    {
                        steps.Add(new AgentStep(call, AgentPermission.Deny, null, decision.Reason));
                        history.Add(new AgentExchange(call, "Refused: " + decision.Reason));
                        continue;
                    }

                    if (decision.Permission == AgentPermission.Ask)
                    {
                        reportActivity?.Invoke("Waiting for approval");
                        AgentApprovalOutcome outcome;
                        try
                        {
                            outcome = await requestApproval(call, decision.Reason, token).ConfigureAwait(false);
                        }
                        finally
                        {
                            reportActivity?.Invoke(null);
                        }

                        if (outcome == AgentApprovalOutcome.Deny)
                        {
                            steps.Add(new AgentStep(call, AgentPermission.Ask, null, "denied by the user"));
                            history.Add(new AgentExchange(
                                call,
                                "The user declined this step. Do not retry it; work around it or explain what you need."));
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
                        result = await _executor.ExecuteAsync(call, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        reportActivity?.Invoke(null);
                    }

                    steps.Add(new AgentStep(call, decision.Permission, result, decision.Reason));
                    history.Add(new AgentExchange(call, result.ToObservation()));
                }
            }
            catch (OperationCanceledException)
            {
                reportActivity?.Invoke(null);
                return new AgentRunResult("Stopped.", steps, false, true);
            }

            reportActivity?.Invoke(null);
            return new AgentRunResult(
                $"I reached the {_maxSteps}-step limit for this model before finishing. Ask me to continue if the work so far looks right.",
                steps,
                true,
                false);
        }

        /// <summary>Commands the user chose to stop being asked about during this run.</summary>
        public IReadOnlyList<string> SessionAllowList => _sessionAllowList;
    }
}
