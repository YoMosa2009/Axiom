using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseActionParser
    {
        private static readonly Regex FenceRegex = new(@"```(?:json)?\s*(?<json>[\s\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly JsonDocumentOptions JsonOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        public static ComputerUseTurn Parse(string? raw)
        {
            string text = raw ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ComputerUseTurn
                {
                    RawText = text,
                    ParseError = "Empty model output."
                };
            }

            if (TryExtractJsonObject(text, out string json) && TryParseObject(json, text, out ComputerUseTurn parsed))
                return parsed;

            return new ComputerUseTurn
            {
                RawText = text,
                Thinking = TrimThinking(text),
                ParseError = "No valid Computer Use JSON object was found."
            };
        }

        private static bool TryParseObject(string json, string raw, out ComputerUseTurn turn)
        {
            turn = new ComputerUseTurn { RawText = raw };
            try
            {
                using JsonDocument document = JsonDocument.Parse(json, JsonOptions);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                JsonElement actionElement = default;
                bool hasAction = root.TryGetProperty("action", out actionElement);
                if (!hasAction && root.TryGetProperty("actions", out JsonElement actions) && actions.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in actions.EnumerateArray())
                    {
                        actionElement = item;
                        hasAction = true;
                        break;
                    }
                }

                if (!hasAction)
                    hasAction = LooksLikeAction(root);

                if (!hasAction || (actionElement.ValueKind != JsonValueKind.Undefined
                    && actionElement.ValueKind != JsonValueKind.Object))
                    return false;

                ComputerUseAction action = hasAction
                    ? ReadAction(hasAction && actionElement.ValueKind == JsonValueKind.Object ? actionElement : root)
                    : new ComputerUseAction { Type = ComputerUseActionType.Screenshot };
                var source = actionElement.ValueKind == JsonValueKind.Object ? actionElement : root;
                string actionName = ReadString(source, "type", "action", "name") ?? "";
                if (action.Type == ComputerUseActionType.Screenshot
                    && !string.Equals(actionName, "screenshot", StringComparison.OrdinalIgnoreCase))
                    return false;

                string thinking = ReadString(root, "thinking", "thought", "reasoning", "commentary")
                    ?? TrimThinking(raw);
                ComputerUseTaskProgress progress = ReadProgress(root);
                ComputerUseSafetyVerdict safety = ReadSafety(root);

                turn = new ComputerUseTurn
                {
                    Thinking = thinking,
                    Progress = progress,
                    Safety = safety,
                    Action = action,
                    RawText = raw,
                    Parsed = true
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ComputerUseTaskProgress ReadProgress(JsonElement root)
        {
            if (!root.TryGetProperty("task_progress", out JsonElement progress)
                && !root.TryGetProperty("progress", out progress))
            {
                return new ComputerUseTaskProgress();
            }

            if (progress.ValueKind == JsonValueKind.String)
            {
                return new ComputerUseTaskProgress { Current = progress.GetString() ?? "" };
            }

            if (progress.ValueKind != JsonValueKind.Object)
                return new ComputerUseTaskProgress();

            return new ComputerUseTaskProgress
            {
                Completed = ReadStringList(progress, "completed", "done", "finished"),
                Current = ReadString(progress, "current", "active", "current_goal") ?? "",
                Next = ReadString(progress, "next", "next_goal", "up_next") ?? "",
                Remaining = ReadStringList(progress, "remaining")
            };
        }

        private static ComputerUseSafetyVerdict ReadSafety(JsonElement root)
        {
            if (!root.TryGetProperty("safety", out JsonElement safety) || safety.ValueKind != JsonValueKind.Object)
            {
                return new ComputerUseSafetyVerdict
                {
                    Ok = false,
                    Dangerous = false,
                    Risk = "unknown",
                    Reason = "Safety pass missing."
                };
            }

            bool dangerous = ReadBool(safety, false, "dangerous", "is_dangerous", "harmful");
            bool ok = ReadBool(safety, !dangerous, "ok", "allowed", "safe");
            return new ComputerUseSafetyVerdict
            {
                Ok = ok && !dangerous,
                Dangerous = dangerous,
                Risk = ReadString(safety, "risk", "level") ?? (dangerous ? "high" : "low"),
                Causes = ReadString(safety, "causes", "cause") ?? "",
                Effects = ReadString(safety, "effects", "effect") ?? "",
                Reason = ReadString(safety, "reason", "notes", "explanation") ?? ""
            };
        }

        private static ComputerUseAction ReadAction(JsonElement element)
        {
            string typeText = ReadString(element, "type", "action", "name") ?? "screenshot";
            ComputerUseActionType type = ParseActionType(typeText);
            string button = (ReadString(element, "button") ?? "left").Trim().ToLowerInvariant();
            if (type == ComputerUseActionType.Click && button is "right" or "secondary")
                type = ComputerUseActionType.RightClick;

            int x = ReadInt(element, 0, "x", "screen_x");
            int y = ReadInt(element, 0, "y", "screen_y");
            if (type == ComputerUseActionType.Zoom)
            {
                string normalizedType = typeText.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
                int zoomDy = ReadInt(element, 0, "dy", "amount");
                if (zoomDy == 0)
                    zoomDy = normalizedType.Contains("out") ? -1 : 1;
                return new ComputerUseAction
                {
                    Type = type,
                    X = x,
                    Y = y,
                    Dy = zoomDy,
                    Explanation = ReadString(element, "explanation", "why", "intent") ?? ""
                };
            }
            if (element.TryGetProperty("coordinate", out JsonElement coordinate) && coordinate.ValueKind == JsonValueKind.Array)
            {
                var values = coordinate.EnumerateArray().ToList();
                if (values.Count >= 2)
                {
                    if (TryReadNumber(values[0], out int coordinateX))
                        x = coordinateX;
                    if (TryReadNumber(values[1], out int coordinateY))
                        y = coordinateY;
                }
            }

            string text = ReadString(element, "text", "typed", "app", "name", "target", "query") ?? "";
            (int dragStartX, int dragStartY, int dragEndX, int dragEndY) = type == ComputerUseActionType.Drag
                ? ReadDragEndpoints(element, x, y)
                : (x, y, 0, 0);
            if (type == ComputerUseActionType.Drag)
            {
                x = dragStartX;
                y = dragStartY;
            }

            return new ComputerUseAction
            {
                Type = type,
                X = x,
                Y = y,
                Dx = ReadInt(element, 0, "dx", "scroll_x"),
                Dy = ReadInt(element, 0, "dy", "scroll_y", "amount"),
                // A drag needs an end point. Models name it several ways, and some express it only
                // as an offset, so a delta from the start is the last resort rather than the
                // action silently becoming a zero-length drag.
                X2 = type == ComputerUseActionType.Drag
                    ? dragEndX
                    : ReadInt(element, x + ReadInt(element, 0, "dx", "scroll_x"), "x2", "to_x", "end_x", "target_x"),
                Y2 = type == ComputerUseActionType.Drag
                    ? dragEndY
                    : ReadInt(element, y + ReadInt(element, 0, "dy", "scroll_y", "amount"), "y2", "to_y", "end_y", "target_y"),
                Text = text,
                Keys = ReadString(element, "keys", "key", "combo") ?? "",
                Ms = ReadInt(element, type == ComputerUseActionType.Wait ? 400 : 0, "ms", "wait_ms", "duration"),
                Button = button,
                TargetId = NormalizeTargetId(ReadString(element, "target_id", "targetId", "ui_target")),
                ExpectedState = ReadString(element, "expected_state", "expected", "success_criteria") ?? "",
                Summary = ReadString(element, "summary", "result") ?? "",
                Outcome = ReadString(element, "outcome") ?? "",
                Explanation = ReadString(element, "explanation", "why", "intent") ?? ""
            };
        }

        public static ComputerUseActionType ParseActionType(string? value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            return normalized switch
            {
                "move" or "mouse_move" or "cursor" or "hover" => ComputerUseActionType.Move,
                "click" or "left_click" or "leftclick" or "mouse_click" => ComputerUseActionType.Click,
                "double_click" or "doubleclick" or "dblclick" => ComputerUseActionType.DoubleClick,
                "right_click" or "rightclick" or "context_click" => ComputerUseActionType.RightClick,
                "drag" or "left_click_drag" or "click_drag" or "drag_to" or "stroke" or "draw"
                    => ComputerUseActionType.Drag,
                "scroll" or "mouse_scroll" or "wheel" => ComputerUseActionType.Scroll,
                "type" or "type_text" or "insert_text" or "keyboard_type" => ComputerUseActionType.Type,
                "key" or "hotkey" or "press" or "keypress" => ComputerUseActionType.Key,
                "wait" or "sleep" or "pause" => ComputerUseActionType.Wait,
                "zoom" or "zoom_in" or "zoomin" => ComputerUseActionType.Zoom,
                "zoom_out" or "zoomout" => ComputerUseActionType.Zoom,
                "open" or "launch" or "start_app" or "open_app" => ComputerUseActionType.Open,
                "done" or "finish" or "complete" or "stop" => ComputerUseActionType.Done,
                _ => ComputerUseActionType.Screenshot
            };
        }

        private static bool LooksLikeAction(JsonElement root)
            => root.TryGetProperty("type", out _) || root.TryGetProperty("action", out _);

        private static bool TryExtractJsonObject(string text, out string json)
        {
            json = string.Empty;
            foreach (Match fence in FenceRegex.Matches(text))
            {
                string body = fence.Groups["json"].Value.Trim();
                if (TrySliceObject(body, out json))
                    return true;
            }

            return TrySliceObject(text, out json);
        }

        private static bool TrySliceObject(string text, out string json)
        {
            json = string.Empty;
            int start = text.IndexOf('{');
            if (start < 0)
                return false;

            int depth = 0;
            bool inString = false;
            bool escape = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape)
                        escape = false;
                    else if (c == '\\')
                        escape = true;
                    else if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        json = text[start..(i + 1)];
                        return true;
                    }
                }
            }

            return false;
        }

        private static string? ReadString(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out JsonElement value))
                {
                    if (value.ValueKind == JsonValueKind.String)
                        return value.GetString();
                    if (value.ValueKind == JsonValueKind.Number)
                        return value.ToString();
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        return value.GetBoolean().ToString();
                }
            }

            return null;
        }

        private static IReadOnlyList<string> ReadStringList(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (!element.TryGetProperty(name, out JsonElement value))
                    continue;

                if (value.ValueKind == JsonValueKind.String)
                    return [value.GetString() ?? ""];
                if (value.ValueKind != JsonValueKind.Array)
                    continue;

                return value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()?.Trim() ?? "")
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Take(12)
                    .ToList();
            }

            return Array.Empty<string>();
        }

        private static int ReadInt(JsonElement element, int fallback, params string[] names)
        {
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out JsonElement value) && TryReadNumber(value, out int number))
                    return number;
            }

            return fallback;
        }

        /// <summary>
        /// Reads a whole number, accepting the forms models actually emit: integers, decimals, and
        /// either of those as strings. A coordinate written as 740.0 used to be silently ignored,
        /// which for a drag meant falling back to its start point.
        /// </summary>
        private static bool TryReadNumber(JsonElement value, out int number)
        {
            number = 0;
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    if (value.TryGetInt32(out number))
                        return true;
                    if (value.TryGetDouble(out double real) && double.IsFinite(real))
                    {
                        number = (int)Math.Round(real);
                        return true;
                    }
                    return false;
                case JsonValueKind.String:
                    string text = (value.GetString() ?? string.Empty).Trim();
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                        return true;
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed))
                    {
                        number = (int)Math.Round(parsed);
                        return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>Reads a point given as [x, y] or {"x": …, "y": …} under any of the names.</summary>
        private static bool TryReadPoint(JsonElement element, out int x, out int y, params string[] names)
        {
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out JsonElement value) && TryReadPointValue(value, out x, out y))
                    return true;
            }

            x = 0;
            y = 0;
            return false;
        }

        private static bool TryReadPointValue(JsonElement value, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (value.ValueKind == JsonValueKind.Array)
            {
                using var items = value.EnumerateArray();
                var list = items.ToList();
                return list.Count >= 2 && TryReadNumber(list[0], out x) && TryReadNumber(list[1], out y);
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                bool hasX = value.TryGetProperty("x", out JsonElement xValue) || value.TryGetProperty("X", out xValue);
                bool hasY = value.TryGetProperty("y", out JsonElement yValue) || value.TryGetProperty("Y", out yValue);
                return hasX && hasY && TryReadNumber(xValue, out x) && TryReadNumber(yValue, out y);
            }

            return false;
        }

        /// <summary>
        /// Both ends of a drag, however the model chose to express them.
        /// </summary>
        /// <remarks>
        /// There is no single convention, and every one of these is in common use. The one that
        /// matters most is the standard computer-use tool shape — start_coordinate plus coordinate —
        /// because there "coordinate" is the END of the drag. Reading it as the start, as a click
        /// would, loses the end point entirely and turns the stroke into a zero-length drag. That is
        /// exactly what was logged: the model aimed at the canvas centre, said it meant to draw a
        /// circle, and produced "Drag from (640, 426) to (640, 426)" three times running.
        /// </remarks>
        private static (int StartX, int StartY, int EndX, int EndY) ReadDragEndpoints(JsonElement element, int genericX, int genericY)
        {
            bool explicitStart = TryReadPoint(element, out int startX, out int startY,
                "start_coordinate", "start_coordinates", "start", "from", "start_point", "source", "begin");
            if (!explicitStart)
            {
                startX = genericX;
                startY = genericY;
            }

            if (TryReadPoint(element, out int endX, out int endY,
                "end_coordinate", "end_coordinates", "end", "to", "end_point", "destination", "target_point", "finish"))
            {
                return (startX, startY, endX, endY);
            }

            // Computer-use convention: with an explicit start, "coordinate" is where the drag ENDS.
            if (explicitStart && TryReadPoint(element, out endX, out endY, "coordinate", "coordinates"))
                return (startX, startY, endX, endY);

            // A path of points: the first is the start (unless already given), the last the end.
            foreach (string pathName in new[] { "path", "points", "stroke" })
            {
                if (!element.TryGetProperty(pathName, out JsonElement path) || path.ValueKind != JsonValueKind.Array)
                    continue;

                var points = path.EnumerateArray()
                    .Select(item => TryReadPointValue(item, out int px, out int py) ? (Ok: true, X: px, Y: py) : (Ok: false, X: 0, Y: 0))
                    .Where(point => point.Ok)
                    .ToList();
                if (points.Count >= 2)
                {
                    if (!explicitStart)
                        (startX, startY) = (points[0].X, points[0].Y);
                    return (startX, startY, points[^1].X, points[^1].Y);
                }
            }

            // Flat fields, then an offset from the start as the last resort.
            int dx = ReadInt(element, 0, "dx", "scroll_x");
            int dy = ReadInt(element, 0, "dy", "scroll_y", "amount");
            endX = ReadInt(element, startX + dx, "x2", "to_x", "end_x", "target_x");
            endY = ReadInt(element, startY + dy, "y2", "to_y", "end_y", "target_y");
            return (startX, startY, endX, endY);
        }

        private static bool ReadBool(JsonElement element, bool fallback, params string[] names)
        {
            foreach (string name in names)
            {
                if (!element.TryGetProperty(name, out JsonElement value))
                    continue;
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return value.GetBoolean();
                if (value.ValueKind == JsonValueKind.String
                    && bool.TryParse(value.GetString(), out bool parsed))
                    return parsed;
            }

            return fallback;
        }

        private static string TrimThinking(string raw)
        {
            string trimmed = raw.Trim();
            int brace = trimmed.IndexOf('{');
            if (brace > 0)
                trimmed = trimmed[..brace].Trim();
            return trimmed.Length <= 1200 ? trimmed : trimmed[..1200];
        }
    
    // Models fill optional string fields with a placeholder rather than omitting them. "none" is
    // a statement that there is NO target, but it was read as a target id, looked up, not found,
    // and the action refused -- which blocked a plain Ctrl+T until the session gave up.
    private static readonly string[] AbsentTargetPlaceholders =
        ["none", "null", "nil", "n/a", "na", "undefined", "empty", "-", "--", "no target", "not applicable"];

    private static string NormalizeTargetId(string? value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        foreach (string placeholder in AbsentTargetPlaceholders)
        {
            if (string.Equals(trimmed, placeholder, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
        }

        return trimmed;
    }
}
}
