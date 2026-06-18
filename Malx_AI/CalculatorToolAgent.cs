using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    internal static class CalculatorToolAgent
    {
        private static readonly Regex CalcCallRegex = new(@"\[\[\s*calc\s*:\s*(.+?)\s*\]\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SciCallRegex = new(@"\[\[\s*sci\s*:\s*(.+?)\s*\]\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ConvertRegex = new(@"(?<value>-?\d+(?:\.\d+)?)\s*(?<from>km|m|cm|mm|mi|ft|in|kg|g|lb|oz|l|ml|gal|c|f|k)\s*(?:to|in)\s*(?<to>km|m|cm|mm|mi|ft|in|kg|g|lb|oz|l|ml|gal|c|f|k)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryBuildContext(string input, out string contextBlock, out string userSignal)
        {
            contextBlock = string.Empty;
            userSignal = string.Empty;
            if (string.IsNullOrWhiteSpace(input)) return false;

            var entries = new List<string>();

            foreach (Match m in CalcCallRegex.Matches(input))
            {
                if (TryEvaluateScientificExpression(m.Groups[1].Value, out double value, out var steps))
                {
                    entries.Add($"Tool: calc\nExpression: {m.Groups[1].Value.Trim()}\nSteps: {steps}\nResult: {value.ToString("G17", CultureInfo.InvariantCulture)}");
                }
            }

            foreach (Match m in SciCallRegex.Matches(input))
            {
                if (TryEvaluateScientificExpression(m.Groups[1].Value, out double value, out var steps))
                {
                    entries.Add($"Tool: sci\nExpression: {m.Groups[1].Value.Trim()}\nSteps: {steps}\nResult: {value.ToString("G17", CultureInfo.InvariantCulture)}");
                }
            }

            foreach (Match m in ConvertRegex.Matches(input))
            {
                if (TryConvertUnits(
                    double.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture),
                    m.Groups["from"].Value,
                    m.Groups["to"].Value,
                    out double converted,
                    out string steps))
                {
                    entries.Add($"Tool: convert\nConversion: {m.Value}\nSteps: {steps}\nResult: {converted.ToString("G17", CultureInfo.InvariantCulture)} {m.Groups["to"].Value}");
                }
            }

            if (entries.Count == 0)
            {
                string candidate = input;
                var simpleExprMatch = Regex.Match(candidate, @"(?<!\w)(?:\d|\(|\+|\-|\*|\/|\.|\s|\^|pi|e|sin|cos|tan|sqrt|log|ln|abs|round|floor|ceil|ceiling){6,}", RegexOptions.IgnoreCase);
                if (simpleExprMatch.Success && TryEvaluateScientificExpression(simpleExprMatch.Value, out double value, out var steps))
                {
                    entries.Add($"Tool: calc-auto\nExpression: {simpleExprMatch.Value.Trim()}\nSteps: {steps}\nResult: {value.ToString("G17", CultureInfo.InvariantCulture)}");
                }
            }

            if (entries.Count == 0) return false;

            var sb = new StringBuilder();
            sb.AppendLine("[[CALCULATOR TOOL RESULTS]]");
            for (int i = 0; i < entries.Count; i++)
            {
                sb.AppendLine($"[{i + 1}]");
                sb.AppendLine(entries[i]);
                sb.AppendLine();
            }
            sb.AppendLine("Use these exact computed results for all math, conversion, scientific, or coded calculations.");
            sb.Append("[[END CALCULATOR TOOL RESULTS]]");

            contextBlock = "\n\n" + sb;
            userSignal = $"🧮 Calculator tool active ({entries.Count} operation(s))";
            return true;
        }

        public static bool TryEvaluateExpression(string expression, out string resultText)
        {
            resultText = string.Empty;
            if (string.IsNullOrWhiteSpace(expression))
                return false;

            string trimmed = expression.Trim();

            Match convertMatch = ConvertRegex.Match(trimmed);
            if (convertMatch.Success
                && TryConvertUnits(
                    double.Parse(convertMatch.Groups["value"].Value, CultureInfo.InvariantCulture),
                    convertMatch.Groups["from"].Value,
                    convertMatch.Groups["to"].Value,
                    out double converted,
                    out string convertSteps))
            {
                resultText = $"Conversion: {trimmed}\nSteps: {convertSteps}\nResult: {converted.ToString("G17", CultureInfo.InvariantCulture)} {convertMatch.Groups["to"].Value}";
                return true;
            }

            string normalized = trimmed;
            if (normalized.StartsWith("[[calc:", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith("]]", StringComparison.Ordinal))
                normalized = normalized[7..^2].Trim();
            else if (normalized.StartsWith("[[sci:", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith("]]", StringComparison.Ordinal))
                normalized = normalized[6..^2].Trim();

            if (TryEvaluateScientificExpression(normalized, out double value, out string steps))
            {
                resultText = $"Expression: {normalized}\nSteps: {steps}\nResult: {value.ToString("G17", CultureInfo.InvariantCulture)}";
                return true;
            }

            return false;
        }

        private static bool TryEvaluateScientificExpression(string expr, out double result, out string steps)
        {
            result = 0;
            steps = "";
            if (string.IsNullOrWhiteSpace(expr)) return false;

            string normalized = expr.Trim().ToLowerInvariant();
            normalized = normalized.Replace("π", "pi", StringComparison.OrdinalIgnoreCase);
            normalized = normalized.Replace("×", "*", StringComparison.Ordinal)
                                   .Replace("÷", "/", StringComparison.Ordinal)
                                   .Replace("−", "-", StringComparison.Ordinal); // unicode minus
            normalized = Regex.Replace(normalized, @"\btau\b", (2 * Math.PI).ToString("R", CultureInfo.InvariantCulture));
            normalized = Regex.Replace(normalized, @"\bpi\b", Math.PI.ToString("R", CultureInfo.InvariantCulture));
            normalized = Regex.Replace(normalized, @"\be\b", Math.E.ToString("R", CultureInfo.InvariantCulture));

            // Factorial postfix (n!) — resolved first so it composes with powers/functions. The
            // negative lookahead avoids consuming the "!" in a "!=" comparison.
            int guard = 0;
            var factorialRegex = new Regex(@"(\d+(?:\.\d+)?)\s*!(?!=)", RegexOptions.Compiled);
            while (factorialRegex.IsMatch(normalized) && guard++ < 20)
            {
                normalized = factorialRegex.Replace(normalized, m =>
                {
                    double f = Factorial(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                    return double.IsNaN(f) ? m.Value : f.ToString("R", CultureInfo.InvariantCulture);
                }, 1);
            }

            guard = 0;
            var powerRegex = new Regex(@"(-?\d+(?:\.\d+)?)\s*\^\s*(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
            while (Regex.IsMatch(normalized, @"(-?\d+(?:\.\d+)?)\s*\^\s*(-?\d+(?:\.\d+)?)") && guard++ < 12)
            {
                normalized = powerRegex.Replace(normalized, m =>
                {
                    double a = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double b = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    return Math.Pow(a, b).ToString("R", CultureInfo.InvariantCulture);
                }, 1);
            }

            // Longer/more specific names first so an overlapping prefix (e.g. sinh vs sin, ceiling vs
            // ceil, factorial vs fact) is matched correctly.
            const string functionAlternation =
                "factorial|fact|asinh|acosh|atanh|sinh|cosh|tanh|asin|acos|atan|sind|cosd|tand|" +
                "sqrt|cbrt|sign|exp|sin|cos|tan|log2|log10|log|ln|abs|floor|ceiling|ceil|round|trunc|deg|rad";
            string functionPattern = "(" + functionAlternation + @")\(([^()]+)\)";
            var functionRegex = new Regex(functionPattern, RegexOptions.Compiled);

            guard = 0;
            while (functionRegex.IsMatch(normalized) && guard++ < 40)
            {
                normalized = functionRegex.Replace(normalized, m =>
                {
                    string fn = m.Groups[1].Value;
                    string argExpr = m.Groups[2].Value;
                    if (!TryEvaluateBasicExpression(argExpr, out double arg)) return m.Value;
                    double val = fn switch
                    {
                        "sin" => Math.Sin(arg),
                        "cos" => Math.Cos(arg),
                        "tan" => Math.Tan(arg),
                        "sind" => Math.Sin(arg * Math.PI / 180.0),
                        "cosd" => Math.Cos(arg * Math.PI / 180.0),
                        "tand" => Math.Tan(arg * Math.PI / 180.0),
                        "asin" => Math.Asin(arg),
                        "acos" => Math.Acos(arg),
                        "atan" => Math.Atan(arg),
                        "sinh" => Math.Sinh(arg),
                        "cosh" => Math.Cosh(arg),
                        "tanh" => Math.Tanh(arg),
                        "asinh" => Math.Asinh(arg),
                        "acosh" => Math.Acosh(arg),
                        "atanh" => Math.Atanh(arg),
                        "sqrt" => Math.Sqrt(arg),
                        "cbrt" => Math.Cbrt(arg),
                        "exp" => Math.Exp(arg),
                        "log" or "log10" => Math.Log10(arg),
                        "log2" => Math.Log2(arg),
                        "ln" => Math.Log(arg),
                        "abs" => Math.Abs(arg),
                        "sign" => Math.Sign(arg),
                        "floor" => Math.Floor(arg),
                        "ceil" or "ceiling" => Math.Ceiling(arg),
                        "round" => Math.Round(arg),
                        "trunc" => Math.Truncate(arg),
                        "deg" => arg * 180.0 / Math.PI,
                        "rad" => arg * Math.PI / 180.0,
                        "fact" or "factorial" => Factorial(arg),
                        _ => arg
                    };
                    // Leave undefined results (e.g. asin(2), sqrt(-1), factorial of a non-integer)
                    // unresolved so the overall expression fails cleanly instead of poisoning the
                    // downstream evaluator with "NaN"/"Infinity" literals it cannot parse.
                    if (double.IsNaN(val) || double.IsInfinity(val))
                        return m.Value;
                    return val.ToString("R", CultureInfo.InvariantCulture);
                });
            }

            if (!TryEvaluateBasicExpression(normalized, out result)) return false;
            steps = "constants/factorials/powers/functions normalized, then expression evaluated";
            return true;
        }

        // Exact factorial for non-negative integers up to 170 (171! overflows double). Returns NaN for
        // negative, non-integer, or out-of-range inputs so the caller can leave the token unresolved.
        private static double Factorial(double n)
        {
            if (double.IsNaN(n) || n < 0 || n > 170 || Math.Floor(n) != n)
                return double.NaN;

            double result = 1;
            for (int i = 2; i <= (int)n; i++)
                result *= i;
            return result;
        }

        private static bool TryEvaluateBasicExpression(string expression, out double value)
        {
            value = 0;
            try
            {
                var table = new DataTable();
                object? obj = table.Compute(expression, "");
                if (obj == null) return false;
                value = Convert.ToDouble(obj, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConvertUnits(double value, string from, string to, out double converted, out string steps)
        {
            converted = 0;
            steps = "";
            from = from.ToLowerInvariant();
            to = to.ToLowerInvariant();

            var length = new Dictionary<string, double> { ["mm"] = 0.001, ["cm"] = 0.01, ["m"] = 1, ["km"] = 1000, ["in"] = 0.0254, ["ft"] = 0.3048, ["mi"] = 1609.344 };
            var mass = new Dictionary<string, double> { ["g"] = 0.001, ["kg"] = 1, ["oz"] = 0.028349523125, ["lb"] = 0.45359237 };
            var volume = new Dictionary<string, double> { ["ml"] = 0.001, ["l"] = 1, ["gal"] = 3.785411784 };

            if (length.ContainsKey(from) && length.ContainsKey(to))
            {
                converted = value * length[from] / length[to];
                steps = "length converted via meters base";
                return true;
            }

            if (mass.ContainsKey(from) && mass.ContainsKey(to))
            {
                converted = value * mass[from] / mass[to];
                steps = "mass converted via kilograms base";
                return true;
            }

            if (volume.ContainsKey(from) && volume.ContainsKey(to))
            {
                converted = value * volume[from] / volume[to];
                steps = "volume converted via liters base";
                return true;
            }

            if ((from is "c" or "f" or "k") && (to is "c" or "f" or "k"))
            {
                double c = from switch
                {
                    "c" => value,
                    "f" => (value - 32) * 5.0 / 9.0,
                    "k" => value - 273.15,
                    _ => value
                };

                converted = to switch
                {
                    "c" => c,
                    "f" => c * 9.0 / 5.0 + 32,
                    "k" => c + 273.15,
                    _ => c
                };

                steps = "temperature converted via celsius base";
                return true;
            }

            return false;
        }
    }
}
