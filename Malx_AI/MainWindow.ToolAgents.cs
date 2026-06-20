using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Data;
using System.Globalization;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Transformers;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private sealed class SandboxVariableSeed
        {
            public string Name { get; init; } = string.Empty;
            public string DisplayValue { get; init; } = string.Empty;
            public string PythonAssignment { get; init; } = string.Empty;
        }

        private sealed class SandboxPreparation
        {
            public bool IsEligible { get; init; }
            public int Score { get; init; }
            public string SystemPromptInjection { get; init; } = string.Empty;
            public string CalculatorContext { get; init; } = string.Empty;
            public string CalculatorSignal { get; init; } = string.Empty;
            public string PythonPreamble { get; init; } = string.Empty;
            public List<SandboxVariableSeed> Seeds { get; init; } = new();
            public string ExplicitPythonCode { get; init; } = string.Empty;
            public string PreInferencePythonContext { get; init; } = string.Empty;
            public bool UserInputIntentDetected { get; init; }
            public string NormalizedExpressionNote { get; init; } = string.Empty;
        }

        private sealed class PythonCodeExecutionOutcome
        {
            public string Code { get; init; } = string.Empty;
            public string ExecutionResult { get; init; } = string.Empty;
            public bool WasCorrected { get; init; }
        }

        private static string BuildCalculatorContext(string input)
        {
            return CalculatorToolAgent.TryBuildContext(input, out var context, out _)
                ? context
                : "";
        }

        private static int ScoreSandboxEligibility(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return 0;

            int score = SandboxExpressionRegex.Matches(message).Count * 3;
            string lower = message.ToLowerInvariant();

            foreach (string unit in SandboxUnitWords)
                score += Regex.Matches(lower, $@"\b{Regex.Escape(unit)}\b").Count * 2;

            foreach (string phrase in SandboxQuantityPhrases)
                score += Regex.Matches(lower, Regex.Escape(phrase)).Count * 2;

            foreach (string word in SandboxDomainWords)
                score += Regex.Matches(lower, $@"\b{Regex.Escape(word)}\b").Count;

            if (message.Contains("```", StringComparison.Ordinal))
                score += 4;

            return score;
        }

        private static bool DetectDynamicUserInputIntent(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string lower = message.ToLowerInvariant();
            return DynamicInputIntentPhrases.Any(lower.Contains);
        }

        private static List<SandboxVariableSeed> ExtractSandboxVariableSeeds(string message, bool skipDynamicInputs = false)
        {
            var seeds = new List<SandboxVariableSeed>();
            if (string.IsNullOrWhiteSpace(message))
                return seeds;

            var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in SandboxNumberWithUnitRegex.Matches(message))
            {
                string number = match.Groups["number"].Value;
                if (string.IsNullOrWhiteSpace(number))
                    continue;

                string unit = match.Groups["unit"].Value.Trim();
                string label = match.Groups["label"].Value.Trim();
                if (skipDynamicInputs && string.IsNullOrWhiteSpace(unit) && IsLikelyDynamicInputLabel(label))
                    continue;

                string baseName = BuildSandboxVariableName(label, unit, seeds.Count + 1);
                if (nameCounts.TryGetValue(baseName, out int count))
                {
                    count++;
                    nameCounts[baseName] = count;
                    baseName = $"{baseName}_{count}";
                }
                else
                {
                    nameCounts[baseName] = 1;
                }

                string assignment = BuildSandboxAssignment(baseName, number, unit);
                if (string.IsNullOrWhiteSpace(assignment))
                    continue;

                string displayValue = unit.Equals("percent", StringComparison.OrdinalIgnoreCase)
                    ? $"{number}%"
                    : string.IsNullOrWhiteSpace(unit) ? number : $"{number} {unit}";

                seeds.Add(new SandboxVariableSeed
                {
                    Name = baseName,
                    DisplayValue = displayValue,
                    PythonAssignment = assignment
                });
            }

            return seeds;
        }

        private static bool IsLikelyDynamicInputLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return true;

            string lower = label.ToLowerInvariant();
            string[] dynamicHints = ["user", "input", "enter", "prompt", "value", "number", "amount", "quantity", "count", "hours", "time", "duration", "cost", "price", "rate", "sold"];
            return dynamicHints.Any(lower.Contains);
        }

        private static string BuildSandboxVariableName(string label, string unit, int fallbackIndex)
        {
            string candidate = string.IsNullOrWhiteSpace(label) ? unit : label;
            candidate = Regex.Replace(candidate?.ToLowerInvariant() ?? string.Empty, @"[^a-z0-9]+", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(candidate))
                candidate = $"value_{fallbackIndex}";
            if (char.IsDigit(candidate[0]))
                candidate = "value_" + candidate;

            return candidate switch
            {
                "invest" => "invest_amount",
                "at" when unit.Equals("percent", StringComparison.OrdinalIgnoreCase) => "rate",
                _ => candidate
            };
        }

        private static string BuildSandboxAssignment(string variableName, string number, string unit)
        {
            if (!double.TryParse(number, out _))
                return string.Empty;

            if (unit.Equals("percent", StringComparison.OrdinalIgnoreCase))
                return $"{variableName} = {number} / 100.0";

            return $"{variableName} = {number}";
        }

        private static string BuildPythonPreamble(List<SandboxVariableSeed> seeds, bool isCloudMode)
        {
            if (isCloudMode)
                return string.Empty;

            if (seeds.Count == 0)
                return string.Empty;

            return string.Join("\n", seeds.Select(s => s.PythonAssignment));
        }

        private static string BuildSandboxSystemPromptInjection(List<SandboxVariableSeed> seeds, bool userInputIntentDetected, bool isCloudMode)
        {
            var lines = new List<string>
            {
                "[PYTHON SANDBOX] A Python 3 execution environment is available and will run automatically.",
                "Do not perform arithmetic or calculations in prose.",
                "Write one clean executable Python script that computes the answer.",
                "Print each intermediate result with a descriptive label before computing the next step.",
                "For each calculation step, print the step name, the formula used, and the result on separate lines in this format: Step: [name], Formula: [expression], Result: [value]",
                "The verified output of that script will be shown as the final result."
            };

            if (isCloudMode)
            {
                lines.Add("The final answer shown to the user must include runnable Python code, not pseudocode or partial fragments.");
                lines.Add("Assume the code may be pasted directly into an online Python compiler with no hidden variables or prior state.");
                lines.Add(userInputIntentDetected
                    ? "If the task requires runtime input, explicitly call input() and assign the result before using it."
                    : "Do not rely on undeclared placeholder variables. If a value is needed, define it in code or compute it.");
            }
            else
            {
                lines.Add("Do not use input() in your code. All variable values will be pre-declared in the environment and are already available by name.");
            }

            if (userInputIntentDetected)
                lines.Add("The sandbox will substitute placeholder values for user input variables to verify the code runs correctly, and the actual program will accept real input when the user runs it outside the sandbox.");

            if (seeds.Count > 0)
            {
                lines.Add("Declared variables: " + string.Join(", ", seeds.Select(s => $"{s.Name}={s.DisplayValue}")));
            }

            return string.Join("\n", lines);
        }

        private static string ExtractExplicitPythonCode(string message)
        {
            var match = SandboxPythonCodeBlockRegex.Match(message ?? string.Empty);
            return match.Success ? match.Groups["code"].Value.Trim() : string.Empty;
        }

        private static string NormalizeMathExpressionForPython(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            string normalized = message;
            normalized = Regex.Replace(normalized, @"\bone\s+half\b", "0.5", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bone\s+over\s+two\b", "1/2", RegexOptions.IgnoreCase);
            normalized = DigitLetterMultiplicationRegex.Replace(normalized, "${digit}*${letter}");
            normalized = CaretExponentRegex.Replace(normalized, "${left}**${right}");
            normalized = SqrtRegex.Replace(normalized, "math.sqrt");
            normalized = PiRegex.Replace(normalized, "math.pi");
            return normalized.Trim();
        }

        private static string BuildNormalizedExpressionNote(string originalMessage)
        {
            string normalized = NormalizeMathExpressionForPython(originalMessage);
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, originalMessage?.Trim(), StringComparison.Ordinal))
                return string.Empty;

            return "Normalized math expression: " + normalized;
        }

        private static string AppendUnitToNumericOutput(string output, string originalUserMessage)
        {
            output = ArtifactRenderService.RemoveChartOutputLines(output);
            if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(originalUserMessage))
                return output;

            string unit = ExtractRelevantUnit(originalUserMessage);
            if (string.IsNullOrWhiteSpace(unit))
                return output;

            string canonicalUnit = CanonicalizeUnit(unit);
            var transformed = new List<string>();
            foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.None))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    transformed.Add(rawLine);
                    continue;
                }

                Match simpleMatch = NumericOnlyLineRegex.Match(line);
                if (simpleMatch.Success)
                {
                    transformed.Add(line + " " + canonicalUnit);
                    continue;
                }

                Match labeledMatch = NumericWithOptionalLabelRegex.Match(line);
                if (labeledMatch.Success && string.IsNullOrWhiteSpace(labeledMatch.Groups["suffix"].Value))
                {
                    string prefix = string.IsNullOrWhiteSpace(labeledMatch.Groups["label"].Value)
                        ? string.Empty
                        : labeledMatch.Groups["label"].Value.Trim() + ": ";
                    transformed.Add(prefix + labeledMatch.Groups["value"].Value + " " + canonicalUnit);
                    continue;
                }

                transformed.Add(rawLine);
            }

            return string.Join("\n", transformed);
        }

        private static string ExtractRelevantUnit(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            string lower = message.ToLowerInvariant();
            foreach (string unit in SandboxUnitWords)
            {
                if (Regex.IsMatch(lower, $@"\b{Regex.Escape(unit)}\b", RegexOptions.IgnoreCase))
                    return unit;
            }

            return string.Empty;
        }

        private static string CanonicalizeUnit(string unit)
        {
            string lower = unit?.Trim().ToLowerInvariant() ?? string.Empty;
            return lower switch
            {
                "kilometers" => "km",
                "meters" => "m",
                "kilograms" => "kg",
                "percent" => "%",
                "seconds" => "s",
                "minutes" => "min",
                "hours" => "hr",
                _ => lower
            };
        }

        private static string FormatPythonResultBlock(string output)
        {
            output = ArtifactRenderService.RemoveChartOutputLines(output);
            if (string.IsNullOrWhiteSpace(output))
                return string.Empty;

            var lines = new List<string>();
            foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                var match = PythonResultLineRegex.Match(line);
                lines.Add(match.Success
                    ? $"- {match.Groups["label"].Value.Trim()}: {match.Groups["value"].Value.Trim()}"
                    : $"- {line}");
            }

            return lines.Count == 0 ? string.Empty : "[[PYTHON RESULT]]\n" + string.Join("\n", lines) + "\n[[END PYTHON RESULT]]";
        }

        private static string ConvertPythonResultBlockToUserDisplay(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return Regex.Replace(text,
                @"\[\[PYTHON RESULT\]\]\s*(?<body>[\s\S]*?)\s*\[\[END PYTHON RESULT\]\]",
                m => "<div class=\"python-result-block\"><div class=\"python-result-label\">Computed Result</div><div class=\"python-result-body\">"
                    + string.Join("<br/>", m.Groups["body"].Value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(System.Net.WebUtility.HtmlEncode))
                    + "</div></div>",
                RegexOptions.IgnoreCase);
        }

        private SandboxPreparation PrepareSandboxContext(string userMsg, bool isCloudMode = false)
        {
            int score = ScoreSandboxEligibility(userMsg);
            bool eligible = score >= SandboxEligibilityThreshold;
            string calculatorContext = string.Empty;
            string calculatorSignal = string.Empty;
            string explicitPythonCode = string.Empty;
            string preInferencePythonContext = string.Empty;
            string normalizedExpressionNote = string.Empty;
            bool userInputIntentDetected = DetectDynamicUserInputIntent(userMsg);
            var seeds = new List<SandboxVariableSeed>();

            if (eligible)
            {
                if (CalculatorToolAgent.TryBuildContext(userMsg, out var calcContext, out var calcSignal))
                {
                    calculatorContext = calcContext;
                    calculatorSignal = calcSignal;
                }

                if (!isCloudMode)
                    seeds = ExtractSandboxVariableSeeds(userMsg, userInputIntentDetected);

                explicitPythonCode = ExtractExplicitPythonCode(userMsg);
                normalizedExpressionNote = BuildNormalizedExpressionNote(userMsg);
            }

            string preamble = BuildPythonPreamble(seeds, isCloudMode);
            string injection = eligible ? BuildSandboxSystemPromptInjection(seeds, userInputIntentDetected, isCloudMode) : string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedExpressionNote))
                injection = string.IsNullOrWhiteSpace(injection) ? normalizedExpressionNote : injection + "\n" + normalizedExpressionNote;

            return new SandboxPreparation
            {
                IsEligible = eligible,
                Score = score,
                SystemPromptInjection = injection,
                CalculatorContext = calculatorContext,
                CalculatorSignal = calculatorSignal,
                PythonPreamble = preamble,
                Seeds = seeds,
                ExplicitPythonCode = explicitPythonCode,
                PreInferencePythonContext = preInferencePythonContext,
                UserInputIntentDetected = userInputIntentDetected,
                NormalizedExpressionNote = normalizedExpressionNote
            };
        }

        private static string RenderPythonResultBlockForChat(string pythonResultBlock)
        {
            if (string.IsNullOrWhiteSpace(pythonResultBlock))
                return string.Empty;

            var match = Regex.Match(pythonResultBlock, @"\[\[PYTHON RESULT\]\]\s*(?<body>[\s\S]*?)\s*\[\[END PYTHON RESULT\]\]", RegexOptions.IgnoreCase);
            if (!match.Success)
                return pythonResultBlock.Trim();

            string body = string.Join("\n", match.Groups["body"].Value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()));

            return $"\nComputed Result\n{body}";
        }

        private static string RenderPythonErrorNote(string pythonErrorBlock)
        {
            if (string.IsNullOrWhiteSpace(pythonErrorBlock))
                return string.Empty;

            string error = Regex.Replace(pythonErrorBlock, @"\[\[END PYTHON ERROR\]\]|\[\[PYTHON ERROR\]\]", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(error))
                return string.Empty;

            string firstLine = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? error;
            return $"\nNote: Python execution error: {firstLine}";
        }

        private static string BuildSandboxTimeoutInlineBlock()
        {
            return "<div class=\"sandbox-timeout-block\"><div class=\"sandbox-timeout-label\">Sandbox Timeout</div><div class=\"sandbox-timeout-note\">The script took too long to execute.</div></div>";
        }

        private async Task<string> AppendPythonExecutionResultsToAssistantMessageAsync(string answer, string preamble, string originalUserMessage, CancellationToken token, bool sanitizeSandboxCode = true, bool replaceCorrectedCodeBlocks = false, bool appendExecutionDetails = true)
        {
            if (string.IsNullOrWhiteSpace(answer))
                return answer;

            var matches = SandboxPythonCodeBlockRegex.Matches(answer);
            if (matches.Count == 0)
                return answer;

            string updated = answer;
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var match = matches[i];
                string code = match.Groups["code"].Value.Trim();
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                PythonCodeExecutionOutcome executionOutcome = await ExecuteAndRepairPythonCodeAsync(code, preamble, originalUserMessage, token, sanitizeSandboxCode);
                string codeBlockText = match.Value;
                if (replaceCorrectedCodeBlocks && executionOutcome.WasCorrected && !string.IsNullOrWhiteSpace(executionOutcome.Code))
                {
                    codeBlockText = "```python\n" + executionOutcome.Code.Trim() + "\n```";
                    updated = updated.Remove(match.Index, match.Length).Insert(match.Index, codeBlockText);
                }

                string executionResult = executionOutcome.ExecutionResult;
                string insertion = executionResult.StartsWith("[[PYTHON TIMEOUT]]", StringComparison.OrdinalIgnoreCase)
                    ? BuildSandboxTimeoutInlineBlock()
                    : executionResult.StartsWith("[[PYTHON ERROR]]", StringComparison.OrdinalIgnoreCase)
                    ? RenderPythonErrorNote(executionResult)
                    : RenderPythonResultBlockForChat(executionResult);

                if (!appendExecutionDetails || string.IsNullOrWhiteSpace(insertion))
                    continue;

                updated = updated.Insert(match.Index + codeBlockText.Length, "\n" + insertion.TrimStart('\n'));
            }

            return updated;
        }

        private static string BuildStructuredSandboxCorrectionPrompt(string code, PythonExecutionResult result, string originalUserMessage)
        {
            string errorType = string.IsNullOrWhiteSpace(result.ErrorType) ? "PythonError" : result.ErrorType.Trim();
            string errorLine = (result.Output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? result.Output;

            return "Original code:\n```python\n" + code.Trim() + "\n```\n"
                + "User request: " + (string.IsNullOrWhiteSpace(originalUserMessage) ? "(not provided)" : originalUserMessage.Trim()) + "\n"
                + "Error: " + errorType + ": " + errorLine.Trim() + "\n"
                + "Return one full corrected Python script, not a partial diff.\n"
                + "Keep the original behavior, but make the code runnable in a fresh Python 3 environment.\n"
                + "Declare or assign every referenced identifier before use.\n"
                + "Never leave placeholders like name, x, y, data, value, result, args, or user_name undefined.\n"
                + "If the script greets a user or prints a person-specific message, read the value using input() before use.\n"
                + "Preserve existing variable names when valid, but replace undefined placeholder names with explicit assignments or input() where appropriate.\n"
                + "Output only a single corrected Python code block with no explanation.";
        }

        private static string TryAutoRepairUndefinedPlaceholderPython(string code, PythonExecutionResult result)
        {
            if (string.IsNullOrWhiteSpace(code) || result == null || string.IsNullOrWhiteSpace(result.Output))
                return string.Empty;

            Match match = PythonNameErrorRegex.Match(result.Output);
            if (!match.Success)
                return string.Empty;

            string variableName = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(variableName))
                return string.Empty;

            string lower = variableName.ToLowerInvariant();
            string assignment = lower switch
            {
                "name" or "user_name" or "username" => $"{variableName} = input(\"Enter your name: \")",
                "message" or "text" => $"{variableName} = input(\"Enter {variableName.Replace('_', ' ')}: \")",
                "x" or "y" or "z" or "value" or "amount" or "number" or "count" => $"{variableName} = float(input(\"Enter {variableName}: \") )",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(assignment))
                return string.Empty;

            if (Regex.IsMatch(code, $@"(?m)^\s*{Regex.Escape(variableName)}\s*=", RegexOptions.IgnoreCase))
                return string.Empty;

            var lines = code.Replace("\r\n", "\n").Split('\n').ToList();
            int insertIndex = 0;
            while (insertIndex < lines.Count)
            {
                string trimmed = lines[insertIndex].Trim();
                if (trimmed.StartsWith("import ", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("from ", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("#", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(trimmed))
                {
                    insertIndex++;
                    continue;
                }

                break;
            }

            lines.Insert(insertIndex, assignment);
            return string.Join("\n", lines);
        }

        private async Task<PythonCodeExecutionOutcome> ExecuteAndRepairPythonCodeAsync(string code, string preamble, string originalUserMessage, CancellationToken token, bool sanitizeSandboxCode = true)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return new PythonCodeExecutionOutcome();
            }

            StartToolActivityIndicator("Running Python");
            var first = await _pythonExecutionService.ExecuteMathScriptAsync(code, preamble, 10000, token, sanitizeSandboxCode);
            if (first.Success)
            {
                await BackendLogService.LogEventAsync("MainWindow.PythonSandbox", $"Initial execution succeeded. Duration:{first.Duration.TotalMilliseconds:F0}ms");
                StopToolActivityIndicator();
                return new PythonCodeExecutionOutcome
                {
                    Code = code,
                    ExecutionResult = FormatPythonResultBlock(AppendUnitToNumericOutput(first.Output, originalUserMessage))
                };
            }

            if (first.TimedOut)
            {
                await BackendLogService.LogEventAsync("MainWindow.PythonSandbox", "Initial execution timed out.");
                StopToolActivityIndicator();
                return new PythonCodeExecutionOutcome
                {
                    Code = code,
                    ExecutionResult = "[[PYTHON TIMEOUT]]"
                };
            }

            await BackendLogService.LogEventAsync("MainWindow.PythonSandbox", $"Initial execution failed. Type:{first.ErrorType}\nMessage:{first.Output}");

            string heuristicCode = TryAutoRepairUndefinedPlaceholderPython(code, first);
            if (!string.IsNullOrWhiteSpace(heuristicCode))
            {
                var heuristicResult = await _pythonExecutionService.ExecuteMathScriptAsync(heuristicCode, preamble, 10000, token, sanitizeSandboxCode);
                if (heuristicResult.Success)
                {
                    await BackendLogService.LogEventAsync("MainWindow.PythonSandbox", $"Heuristic repair succeeded. Duration:{heuristicResult.Duration.TotalMilliseconds:F0}ms");
                    StopToolActivityIndicator();
                    return new PythonCodeExecutionOutcome
                    {
                        Code = heuristicCode,
                        ExecutionResult = FormatPythonResultBlock(AppendUnitToNumericOutput(heuristicResult.Output, originalUserMessage)),
                        WasCorrected = !string.Equals(heuristicCode.Trim(), code.Trim(), StringComparison.Ordinal)
                    };
                }
            }

            string correctionPrompt = BuildStructuredSandboxCorrectionPrompt(code, first, originalUserMessage);
            string retrySystemPrompt = ((BuildDefaultAssistantSystemPrompt() + "\n\n" + BuildCloudCodingSystemInstruction("python code repair for an online python compiler") + BuildPriorComputationResultsBlock()) + "\n\nReturn only a corrected Python code block.").Trim();
            string corrected = _cloudModeActive && _openRouterChatService.HasValidKey
                ? await GenerateSingleTurnCloudResponseAsync(retrySystemPrompt, correctionPrompt, token)
                : await GenerateSingleTurnResponseAsync(retrySystemPrompt, correctionPrompt, 200, token);
            string correctedCode = ExtractExplicitPythonCode(corrected);
            if (string.IsNullOrWhiteSpace(correctedCode))
                correctedCode = corrected.Trim();

            if (string.IsNullOrWhiteSpace(correctedCode))
            {
                StopToolActivityIndicator();
                return new PythonCodeExecutionOutcome
                {
                    Code = code,
                    ExecutionResult = $"[[PYTHON ERROR]]{first.Output}[[END PYTHON ERROR]]"
                };
            }

            var second = await _pythonExecutionService.ExecuteMathScriptAsync(correctedCode, preamble, 10000, token, sanitizeSandboxCode);
            if (second.Success)
            {
                await BackendLogService.LogEventAsync("MainWindow.PythonSandbox", $"Retry execution succeeded. Duration:{second.Duration.TotalMilliseconds:F0}ms");
                StopToolActivityIndicator();
                return new PythonCodeExecutionOutcome
                {
                    Code = correctedCode,
                    ExecutionResult = FormatPythonResultBlock(AppendUnitToNumericOutput(second.Output, originalUserMessage)),
                    WasCorrected = !string.Equals(correctedCode.Trim(), code.Trim(), StringComparison.Ordinal)
                };
            }

            if (second.TimedOut)
            {
                await BackendLogService.LogEventAsync("MainWindow.PythonSandbox", "Retry execution timed out.");
                StopToolActivityIndicator();
                return new PythonCodeExecutionOutcome
                {
                    Code = code,
                    ExecutionResult = "[[PYTHON TIMEOUT]]"
                };
            }

            await BackendLogService.LogEventAsync("MainWindow.PythonSandbox", $"Retry execution failed. Type:{second.ErrorType}\nMessage:{second.Output}");
            StopToolActivityIndicator();
            return new PythonCodeExecutionOutcome
            {
                Code = code,
                ExecutionResult = $"[[PYTHON ERROR]]{second.Output}[[END PYTHON ERROR]]"
            };
        }

        private async Task<string> ExecutePythonWithSingleRetryAsync(string code, string preamble, string originalUserMessage, CancellationToken token, bool sanitizeSandboxCode = true)
        {
            PythonCodeExecutionOutcome outcome = await ExecuteAndRepairPythonCodeAsync(code, preamble, originalUserMessage, token, sanitizeSandboxCode);
            return outcome.ExecutionResult;
        }

        private static List<string> ExtractDeclaredVariableNames(string preamble)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(preamble))
                return names;

            foreach (string rawLine in preamble.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                string candidate = line[..eq].Trim();
                if (Regex.IsMatch(candidate, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    names.Add(candidate);
            }

            return names;
        }

        private static bool ShouldUseWebSearch(string query)
        {
            return ContainsExplicitWebSearchRequest(query);
        }

        private static bool ContainsExplicitWebSearchRequest(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return false;

            string lower = query.ToLowerInvariant();
            return ExplicitWebSearchMarkers.Any(m => lower.Contains(m));
        }

        private static string BuildWebSearchStatusLabel(string query)
        {
            string text = query?.Trim() ?? string.Empty;
            if (text.Length > 72)
                text = text[..72] + "...";

            return string.IsNullOrWhiteSpace(text)
                ? "Searching the web..."
                : $"Searching the web for: {text}";
        }

        private static string BuildSingleTurnWebSearchInstruction()
        {
            return "[WEB-ONLY ANSWERING RULE] Web search is enabled and source material is provided above. You MUST ground current factual claims in [[WEB SEARCH DATA]] and treat it as the authoritative source for this turn. Do NOT use background knowledge, model training data, memory, or unstated assumptions to add unsupported facts beyond those sources. Every factual statement in your answer should be traceable to the provided sources. Prefer sources marked High confidence over Medium confidence. Ignore any Low confidence source unless the user explicitly asks for it. Do not answer with phrases like 'as of my last update' when [[WEB SEARCH DATA]] is present. If the sources contain only part of the answer, provide the supported portion first, then briefly note what the sources do not confirm. For broad requests like latest news, summarize the strongest confirmed headlines or developments from the provided sources instead of claiming the dataset is unusable. If sources conflict, report the conflict instead of resolving it by guessing. When the user asks for news, latest updates, or current information, format the answer as a clean numbered or bulleted list with short explanations, and include the source name and published date for each item. Cite the relevant source title or host naturally when making factual claims, and avoid any claim that cannot be tied back to a source in [[WEB SEARCH DATA]].";
        }

        private static string BuildWebGroundedUserTurnInstruction()
        {
            return "[WEB TURN RULE] For this answer, treat [[WEB SEARCH DATA]] as the source of truth for factual claims. Do not use prior assistant messages, memory, or model background knowledge to add details that are not explicitly supported by those sources. If a requested detail is missing, answer with what the sources do confirm and briefly call out the missing detail instead of refusing the whole answer. For broad current-information requests, synthesize the most relevant supported developments from the sources. For news or latest-update requests, present the answer as a clean list with short explanations and include the source name and date for each item. If a fact is not in the sources, omit it rather than inferring it from training data.";
        }

        private static IReadOnlyList<ConversationSearchTurn> BuildNormalChatSearchTurns(IReadOnlyList<ChatMessage>? conversationHistory)
        {
            if (conversationHistory == null || conversationHistory.Count == 0)
                return [];

            return conversationHistory
                .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .TakeLast(10)
                .Select(m => new ConversationSearchTurn(m.Role, m.Content))
                .ToList();
        }

        private async Task<string> TryBuildWebContextAsync(string userQuery, bool forceEnableForTurn, CancellationToken token, IReadOnlyList<ChatMessage>? conversationHistory = null, int maxChars = 2200)
        {
            bool shouldSearch = forceEnableForTurn || _normalWebSearchEnabled || ContainsExplicitWebSearchRequest(userQuery);
            if (!shouldSearch)
                return string.Empty;

            string contextualPrompt = ConversationSearchContext.BuildContextualSearchPrompt(
                userQuery,
                BuildNormalChatSearchTurns(conversationHistory));
            if (string.IsNullOrWhiteSpace(contextualPrompt))
                contextualPrompt = userQuery?.Trim() ?? string.Empty;

            string searchQuery = _webSearchService.BuildFocusedNormalChatQuery(contextualPrompt);
            if (string.IsNullOrWhiteSpace(searchQuery))
                searchQuery = contextualPrompt;

            try
            {
                StartToolActivityIndicator("Searching the web");
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                await BackendLogService.LogEventAsync("MainWindow.WebSearch", $"Prompt:{userQuery}\nContextualPrompt:{contextualPrompt}\nQuery:{searchQuery}");
                string data = await _webSearchService.SearchTopSnippetsForNormalChatAsync(contextualPrompt, token);
                if (string.IsNullOrWhiteSpace(data)
                    || data.Contains("No web results", StringComparison.OrdinalIgnoreCase)
                    || data.Contains("Web search unavailable", StringComparison.OrdinalIgnoreCase)
                    || data.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                {
                    await BackendLogService.LogEventAsync("ToolFailReadOnly", $"Tool:WebSearch\nPrompt:{userQuery}\nQuery:{searchQuery}\nStatus:No usable results");
                }

                if (string.IsNullOrWhiteSpace(data)
                    || data.Contains("No web results", StringComparison.OrdinalIgnoreCase)
                    || data.Contains("Web search unavailable", StringComparison.OrdinalIgnoreCase)
                    || data.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                data = _webSearchService.PreparePromptContext(data, maxChars);

                return data;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await BackendLogService.LogEventAsync("ToolFailReadOnly", $"Tool:WebSearch\nPrompt:{userQuery}\nContextualPrompt:{contextualPrompt}\nQuery:{searchQuery}\nError:{ex.Message}");
                return string.Empty;
            }
            finally
            {
                StopToolActivityIndicator();
            }
        }

        private void ToolActivityTimer_Tick(object? sender, EventArgs e)
        {
            if (ToolActivityIndicatorText == null)
                return;

            string dots = _activeToolIndicatorPhase switch
            {
                0 => string.Empty,
                1 => ".",
                2 => "..",
                _ => "..."
            };

            ToolActivityIndicatorText.Text = _activeToolIndicatorLabel + dots;
            _activeToolIndicatorPhase = (_activeToolIndicatorPhase + 1) % 4;

            if (ToolActivityIndicatorGlyph != null)
            {
                string[] glyphFrames = ["◌", "○", "◍", "●"];
                ToolActivityIndicatorGlyph.Text = glyphFrames[_activeToolIndicatorGlyphPhase % glyphFrames.Length];
                _activeToolIndicatorGlyphPhase = (_activeToolIndicatorGlyphPhase + 1) % glyphFrames.Length;
            }
        }

        private void StartToolActivityIndicator(string toolName)
        {
            void Apply()
            {
                _activeToolIndicatorLabel = toolName?.Trim() ?? string.Empty;
                _activeToolIndicatorPhase = 0;
                _activeToolIndicatorGlyphPhase = 0;
                if (ToolActivityIndicatorText != null && ToolActivityIndicatorHost != null)
                {
                    ToolActivityIndicatorHost.Visibility = string.IsNullOrWhiteSpace(_activeToolIndicatorLabel)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    ToolActivityIndicatorText.Text = _activeToolIndicatorLabel;
                    if (ToolActivityIndicatorGlyph != null)
                        ToolActivityIndicatorGlyph.Text = "◌";
                }

                if (!string.IsNullOrWhiteSpace(_activeToolIndicatorLabel))
                {
                    _toolActivityTimer.Start();
                    ToolActivityTimer_Tick(this, EventArgs.Empty);
                    ToolActivityIndicatorHost?.InvalidateVisual();
                    ToolActivityIndicatorHost?.UpdateLayout();
                }
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                _ = Dispatcher.InvokeAsync(Apply, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void StopToolActivityIndicator()
        {
            void Apply()
            {
                _toolActivityTimer.Stop();
                _activeToolIndicatorLabel = string.Empty;
                _activeToolIndicatorPhase = 0;
                _activeToolIndicatorGlyphPhase = 0;
                if (ToolActivityIndicatorText != null && ToolActivityIndicatorHost != null)
                {
                    ToolActivityIndicatorText.Text = string.Empty;
                    ToolActivityIndicatorHost.Visibility = Visibility.Collapsed;
                    if (ToolActivityIndicatorGlyph != null)
                        ToolActivityIndicatorGlyph.Text = string.Empty;
                }
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                _ = Dispatcher.InvokeAsync(Apply, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void NormalWebSearchToggle_Click(object sender, RoutedEventArgs e)
        {
            _normalWebSearchEnabled = !_normalWebSearchEnabled;

            RefreshNormalWebToggleUi();

            AddChatMessage("system", _normalWebSearchEnabled
                ? "Normal chat web search enabled."
                : "Normal chat web search disabled.");
        }
    }
}
