using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Malx_AI.ComputerUse;

// Desired outcomes are fixed before the first action. An observer can supply visual
// evidence, but cannot change the destination or waive a tab requirement.
internal sealed class ComputerUseTaskContract
{
    public const string SystemPrompt = """
        Translate the user's ENTIRE request into ordered, independently verifiable outcomes.
        This is planning only: do not act, inspect the screen, or claim completion. Include EVERY
        requested destination and later outcome. A new tab is a constraint on its destination,
        not a substitute for visiting that destination. Keep independent destinations separate.
        For request_text quote the user's words supporting each outcome. Cover every requested
        application, destination, action and constraint across the outcomes. Minor punctuation
        and linking-word differences are fine; do not omit a substantive clause or later outcome.
        kind must be browser for ANY requested website, search, or browser content, otherwise desktop.
        Opening or focusing an application is kind desktop with application set and destination
        empty -- including a browser itself. Only a requested page or site is kind browser, and
        every browser outcome must carry its own absolute URL.
        For browser outcomes destination must be an absolute HTTPS/HTTP URL: use the exact supplied
        URL or trusted navigation context for a specific resource; never invent an owner or path.
        For a named public website, use its canonical homepage. match is exact for a specific URL,
        or site when the requested content must be found on that website. Preserve richer criteria
        (e.g. playing a particular video) in description; reaching its site alone is insufficient.
        separate_tab is true when that destination must be in a new/different tab from the prior
        browser outcome. Do not include a standalone 'open new tab' outcome.
        For ALL applications, set application to the required app name when the user specifies it.
        Set required_text to exact literal text the user asks to enter or display, otherwise [].
        These are mandatory observable constraints, not summaries. Preserve other success conditions
        in description (saving, exporting, selecting the right item, etc.). Do not turn opening a
        dialog or app into completion of the work the user asked to do inside it.
        If a destination is ambiguous or cannot be resolved, return error explaining what is missing.
        Return only JSON: {"outcomes":[{"request_text":"exact phrase from request",
        "description":"complete desired result","kind":"browser|desktop",
        "destination":"URL for browser; empty for desktop","match":"exact|site",
        "separate_tab":false,"application":"required app or empty","required_text":[]}],"error":""}.
        """;

    internal sealed record Outcome(string Description, string Kind, string Destination, bool SiteMatch,
        bool SeparateTab, string Application, IReadOnlyList<string> RequiredText, string RequestText = "");
    private sealed record BrowserEvidence(IntPtr Window, string Tab, IReadOnlyList<string> Tabs);
    private readonly IReadOnlyList<Outcome> _outcomes;
    private BrowserEvidence? _previousBrowser;
    // Evidence for the outcome in progress, latched when first observed. Browser evidence is
    // destroyed the moment the agent moves on -- opening the new tab that the NEXT outcome
    // requires replaces the active document, so a goal that was genuinely reached stops being
    // provable. Re-deriving it from the live capture therefore strands the contract on an
    // already-finished outcome, and the controller then keeps asking the model to redo it.
    private BrowserEvidence? _reachedBrowser;
    private bool _destinationReached;
    private int _index;
    public Outcome? Current => _index < _outcomes.Count ? _outcomes[_index] : null;

    /// <summary>
    /// True once the current outcome's destination has been observed loaded. Latched, so it stays
    /// true after the agent moves on to whatever the outcome additionally requires.
    /// </summary>
    public bool CurrentDestinationReached => _destinationReached;
    public bool Complete => _index == _outcomes.Count;

    private ComputerUseTaskContract(IReadOnlyList<Outcome> outcomes) => _outcomes = outcomes;

    // Words that describe getting an application on screen, plus the nouns planners use for the
    // thing being opened. Whatever a description says BEYOND these and the application's own name
    // is work to be done inside the app, and the launch alone must not complete it.
    private static readonly HashSet<string> LaunchVocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opens", "opened", "opening", "launch", "launches", "launched", "launching",
        "start", "starts", "started", "starting", "run", "runs", "running",
        "focus", "focuses", "focused", "focusing", "switch", "switches", "switched", "switching",
        "bring", "brings", "brought", "activate", "activates", "activated",
        "show", "shows", "shown", "display", "displays", "displayed", "view", "views",
        "application", "app", "apps", "program", "window", "windows", "browser", "desktop",
        "screen", "visible", "foreground", "front"
    };

    /// <summary>
    /// Whether this outcome asks for nothing beyond getting its application on screen. Anything
    /// left over once launch words and the application's own name are removed is work the launch
    /// does not accomplish -- "turn on dark mode in Settings" keeps "turn", "dark", "mode" -- so
    /// such an outcome still needs a visual assessment before it can complete.
    /// </summary>
    private static bool IsApplicationLaunchOnly(Outcome outcome)
    {
        if (outcome.RequiredText.Count > 0 || outcome.Destination.Length > 0)
            return false;

        var applicationWords = new HashSet<string>(SourceWords(outcome.Application), StringComparer.OrdinalIgnoreCase);
        foreach (string word in SourceWords(outcome.Description))
        {
            if (!LaunchVocabulary.Contains(word) && !applicationWords.Contains(word))
                return false;
        }

        return true;
    }

    // Words that only describe GOING somewhere, plus the nouns used to name a destination. What a
    // request still says after these and the destination's own words are removed is a criterion
    // that arriving does not satisfy -- "play the lofi video on youtube" keeps "play", "lofi",
    // "video", while "take me to github" keeps nothing.
    private static readonly HashSet<string> NavigationVocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        "take", "takes", "taking", "took", "go", "goes", "going", "get", "gets", "bring", "brings",
        "navigate", "navigates", "navigating", "navigation", "open", "opens", "opening", "opened",
        "visit", "visits", "load", "loads", "loaded", "show", "shows", "pull", "head", "make",
        "makes", "making", "create", "creates", "launch", "launches", "put", "switch", "move",
        "page", "pages", "homepage", "home", "site", "website", "websites", "url", "link", "links",
        "address", "repo", "repos", "repository", "tab", "tabs", "window", "browser",
        "app", "apps", "application", "applications", "service", "portal", "dashboard",
        "new", "second", "another", "separate", "different", "called", "named", "titled",
        "main", "official"
    };

    // Verbs that ask for something to be DONE somewhere, as opposed to going there. Navigation
    // verbs are deliberately absent: "take me to", "show me", "open" all describe arriving.
    private static readonly HashSet<string> ActionVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "play", "plays", "playing", "watch", "watches", "watching", "listen", "listens",
        "search", "searches", "searching", "find", "finds", "finding", "look", "looks", "looking",
        "click", "clicks", "press", "presses", "select", "selects", "choose", "chooses",
        "download", "downloads", "upload", "uploads", "install", "installs",
        "buy", "buys", "purchase", "purchases", "order", "orders", "checkout",
        "sign", "signs", "login", "log", "subscribe", "subscribes", "follow", "follows",
        "like", "likes", "star", "stars", "fork", "forks", "comment", "comments",
        "post", "posts", "send", "sends", "share", "shares", "reply", "replies",
        "save", "saves", "export", "exports", "print", "prints", "screenshot", "capture",
        "delete", "deletes", "remove", "removes", "add", "adds", "create", "creates",
        "write", "writes", "type", "types", "enter", "enters", "fill", "fills", "submit", "submits",
        "copy", "copies", "paste", "pastes", "scroll", "scrolls", "zoom", "zooms",
        "close", "closes", "quit", "exit", "pause", "pauses", "stop", "stops", "mute", "mutes",
        "rename", "renames", "edit", "edits", "update", "updates", "change", "changes",
        "check", "checks", "verify", "verifies", "confirm", "confirms", "compare", "compares",
        "read", "reads", "summarize", "summarise", "translate", "translates", "count", "counts"
    };

    /// <summary>
    /// Whether the user's own wording for this outcome asks for anything beyond arriving at its
    /// destination.
    /// </summary>
    /// <remarks>
    /// Judged from whether the request names an ACTION, not from leftover words. Which destination
    /// satisfies "any weather app" or "some music service" is a decision the planner already made,
    /// and re-litigating it with an observer that cannot verify such a category is what stalled a
    /// run whose page was plainly loaded. The trade-off is deliberate: an unlisted verb completes
    /// on arrival, which loses a sub-goal, where the opposite error loses the entire run.
    /// </remarks>
    private static bool AddsCriteriaBeyondDestination(Outcome outcome)
    {
        // Literal text the user asked to see or enter is always the controller's to check.
        if (outcome.RequiredText.Count > 0)
            return true;
        // Nothing to reason from: keep the stricter reading.
        if (outcome.RequestText.Length == 0)
            return true;

        return SourceWords(outcome.RequestText).Any(ActionVerbs.Contains);
    }

    private static bool HasRequiredText(Outcome outcome, ComputerUseCapture capture)
        => outcome.RequiredText.All(required =>
            capture.UiTargets.Any(target => target.Name.Contains(required, StringComparison.Ordinal)
                || target.Value.Contains(required, StringComparison.Ordinal)));

    private void Advance()
    {
        if (_reachedBrowser != null)
            _previousBrowser = _reachedBrowser;
        _reachedBrowser = null;
        _destinationReached = false;
        _index++;
    }

    /// <summary>
    /// Records controller-side evidence for the outcome in progress from a fresh capture, and
    /// finishes it outright when the destination is the whole of it.
    /// </summary>
    /// <remarks>
    /// Must be called every step, before any gating. This is hard evidence read from the browser
    /// itself -- the loaded document address, not the address field, and not the model's opinion
    /// -- so nothing about a later, unrelated pending action may suppress it. An exact-URL outcome
    /// is fully specified by its URL: once that document is loaded without an error page, there is
    /// nothing further to judge. A site-match outcome only reaches its precondition here, because
    /// its real criteria live in the description and still need a visual assessment.
    /// </remarks>
    public string ObserveCurrentOutcome(ComputerUseCapture capture)
    {
        Outcome? current = Current;
        if (current == null)
            return "";

        if (current.Kind != "browser")
        {
            // The foreground window identity is exactly as hard as the loaded document address
            // trusted for browser outcomes, and leaving a launch to the observer model strands the
            // whole contract on a goal that is plainly done: the agent is then told its current
            // goal is "open Edge" while Edge is open, and it goes back and redoes earlier work.
            if (current.Application.Length == 0
                || !ComputerUseApplicationVerification.IsRequestedApplicationVisible(current.Application, capture)
                || !HasRequiredText(current, capture)
                || !IsApplicationLaunchOnly(current))
            {
                return "";
            }

            string launched = current.Description;
            Advance();
            return $"[OUTCOME VERIFIED] {launched} {current.Application} is the foreground window "
                + $"({capture.ForegroundProcessName}: {capture.ForegroundWindowTitle}). This outcome is complete "
                + "and stays complete; do not reopen or revisit it. Work only on the next outcome.";
        }

        ComputerUseBrowserState? browser = capture.BrowserState;
        // Matched against the loaded document, not the address bar text, so address-bar focus is
        // not evidence of anything here and must not suppress a destination that is open.
        if (browser == null || browser.IsErrorPage || !MatchesDestination(current, browser.DocumentAddress))
            return "";

        if (current.Application.Length > 0
            && !ComputerUseApplicationVerification.IsRequestedApplicationVisible(current.Application, capture))
        {
            return "";
        }

        if (current.SeparateTab && !HasSeparateTab(capture))
            return "";

        if (!HasRequiredText(current, capture))
            return "";

        bool firstObservation = !_destinationReached;
        _destinationReached = true;
        _reachedBrowser = new(capture.TargetWindowHandle, browser.ActiveTabId, browser.TabIds.ToArray());

        if (!current.SiteMatch || !AddsCriteriaBeyondDestination(current))
        {
            string description = current.Description;
            Advance();
            return $"[OUTCOME VERIFIED] {description} The browser has {browser.DocumentAddress} loaded with no error page. "
                + "This outcome is complete and stays complete; do not return to it. Work only on the next outcome.";
        }

        return firstObservation
            ? $"[OUTCOME DESTINATION REACHED] {browser.DocumentAddress} is loaded for the current outcome. "
              + "The destination no longer needs revisiting; establish the remaining criteria for this outcome here."
            : "";
    }

    public static bool TryParse(string raw, string request, out ComputerUseTaskContract? contract)
        => TryParse(raw, request, out contract, out _);

    public static bool TryParse(string raw, string request, out ComputerUseTaskContract? contract, out string error)
        => TryParse(raw, request, out contract, out error, out _, strictCitations: true);

    /// <summary>
    /// Validates a planner's output into a contract.
    /// </summary>
    /// <remarks>
    /// Three classes of problem, deliberately handled differently, because a rejected plan ends the
    /// session before a single action is sent — a far worse outcome than an imperfectly worded one:
    /// <list type="bullet">
    /// <item>Unsafe or unusable — no destination for a browser outcome, no outcomes at all,
    /// unparseable JSON. Always fatal; there is nothing to act on and nothing may be invented.</item>
    /// <item>Repairable — a stated fact in the wrong field. Normalised in place, never rejected,
    /// and never by inventing anything the planner did not supply.</item>
    /// <item>Citation quality — how the planner quoted the request. Fatal while retries remain
    /// (<paramref name="strictCitations"/>), then downgraded to <paramref name="advisory"/> so a
    /// structurally sound plan still runs, with the shortfall reported to the user.</item>
    /// </list>
    /// </remarks>
    public static bool TryParse(string raw, string request, out ComputerUseTaskContract? contract,
        out string error, out string advisory, bool strictCitations)
    {
        contract = null;
        error = "";
        advisory = "";
        var advisories = new List<string>();
        try
        {
            int first = raw.IndexOf('{'), last = raw.LastIndexOf('}');
            if (first < 0 || last < first) return Invalid("Return an object containing an outcomes array.", out error);
            using var doc = JsonDocument.Parse(raw[first..(last + 1)], new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var reported) && !string.IsNullOrWhiteSpace(reported.GetString()))
                return Invalid("Planner could not resolve the request: " + reported.GetString(), out error);
            if (!root.TryGetProperty("outcomes", out var items) || items.ValueKind != JsonValueKind.Array)
                return Invalid("The outcomes field must be an array.", out error);
            var outcomes = new List<Outcome>();
            var sourceWords = new List<string>();
            string[] requestWords = SourceWords(request);
            foreach (var item in items.EnumerateArray())
            {
                string source = ReadOptionalString(item, "request_text");
                string description = ReadOptionalString(item, "description");
                string kind = ReadOptionalString(item, "kind").ToLowerInvariant();
                string destination = ReadOptionalString(item, "destination");
                string match = ReadOptionalString(item, "match").ToLowerInvariant();
                bool separate = item.TryGetProperty("separate_tab", out var tab)
                    && tab.ValueKind is JsonValueKind.True or JsonValueKind.False && tab.GetBoolean();
                string app = ReadOptionalString(item, "application");
                string[] declaredText = item.TryGetProperty("required_text", out var texts) && texts.ValueKind == JsonValueKind.Array
                    ? texts.EnumerateArray().Select(text => text.GetString() ?? "").ToArray() : [];

                // required_text must stay the user's own literal text, but a planner echoing it in
                // different case is quoting the same words, not inventing new ones. Adopt the
                // request's exact casing so what gets typed is what was asked for.
                var requiredText = new List<string>();
                foreach (string text in declaredText)
                {
                    if (!TryQuoteRequestText(request, text, out string literal))
                        return Invalid($"Outcome {outcomes.Count + 1} lists required_text that does not appear in the request: \"{text}\". Only literal text the user asked to enter or display may appear here.", out error);
                    requiredText.Add(literal);
                }

                if (description.Length == 0 || kind is not ("browser" or "desktop"))
                    return Invalid($"Outcome {outcomes.Count + 1} needs a description and kind browser or desktop.", out error);

                // An application launch has no destination. A planner labelling it browser is a
                // natural reading — Edge IS a browser — and it then has no URL to give, so
                // demanding one is unanswerable and the identical plan comes back until the
                // attempts run out. Reclassifying drops an unsupported browser claim rather than
                // inventing an address; the launch is still verified against the foreground window.
                if (kind == "browser" && destination.Length == 0 && app.Length > 0 && !separate)
                    kind = "desktop";

                // The mirror case: a desktop outcome carrying a real URL is a browser outcome that
                // was mislabelled. Honour the destination the planner actually stated.
                if (kind == "desktop" && ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl(destination, out _))
                    kind = "browser";

                if (kind == "browser")
                {
                    if (!ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl(destination, out destination))
                        return Invalid($"Outcome {outcomes.Count + 1} is kind browser but has no absolute destination URL. If it only opens or focuses an application, use kind desktop with application set and destination empty. Otherwise take the URL from the supplied navigation context; never invent one.", out error);
                    // An unrecognised match value is a wrong label on a stated destination, not a
                    // reason to abandon the task. Exact is the stricter reading of the two.
                    if (match is not ("exact" or "site"))
                        match = "exact";
                    if (separate && !outcomes.Any(outcome => outcome.Kind == "browser"))
                    {
                        // "Open a new tab and go to YouTube" as the whole request: there is no
                        // preceding destination for this one to be separate FROM, so the contract
                        // cannot verify the constraint and would deadlock holding it. The action
                        // side still verifies the new tab against the real tab count.
                        separate = false;
                        advisories.Add($"Outcome {outcomes.Count + 1} asks for a new tab but has no preceding browser destination to be separate from; the tab is verified when it is opened instead.");
                    }
                }
                else
                {
                    // Desktop outcomes have no use for these. Junk in them is a mislabelling, and
                    // clearing it loses nothing a desktop outcome could ever have acted on.
                    if (destination.Length != 0)
                        advisories.Add($"Outcome {outcomes.Count + 1} is a desktop outcome; its destination \"{destination}\" is not a URL and was ignored.");
                    destination = "";
                    separate = false;
                }

                // Citations should quote the request, but planners paraphrase, and a paraphrase is
                // a wording problem rather than an invented instruction.
                string[] words = SourceWords(source);
                string[] invented = words.Where(word => !IsCitedBy(word, requestWords)).ToArray();
                if (invented.Length > 0)
                {
                    string reason = $"Outcome {outcomes.Count + 1} request_text must quote the user's request, without invented words: " + string.Join(", ", invented);
                    if (strictCitations)
                        return Invalid(reason, out error);
                    advisories.Add(reason);
                }

                sourceWords.AddRange(words);
                outcomes.Add(new(description, kind, destination, match == "site", separate, app, requiredText, source));
            }

            if (outcomes.Count is 0 or > 64) return Invalid("Include between one and 64 requested outcomes.", out error);

            string[] missing = requestWords.Where(word => !sourceWords.Any(cited => IsCitedBy(cited, [word]))).ToArray();
            if (missing.Length > 0)
            {
                string reason = "The plan may omit requested details. Include outcomes quoting these words: " + string.Join(", ", missing);
                if (strictCitations)
                    return Invalid(reason, out error);
                advisories.Add(reason);
            }

            contract = new(outcomes);
            advisory = string.Join(" ", advisories);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Invalid("Invalid plan JSON or field type: " + ex.Message, out error);
        }
    }

    /// <summary>
    /// Finds <paramref name="text"/> in the request and returns it with the request's own casing,
    /// so required_text stays literally the user's text without a case difference being fatal.
    /// </summary>
    private static bool TryQuoteRequestText(string request, string text, out string literal)
    {
        literal = text;
        if (text.Length == 0)
            return false;
        if (request.Contains(text, StringComparison.Ordinal))
            return true;

        int index = request.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return false;

        literal = request.Substring(index, text.Length);
        return true;
    }

    /// <summary>
    /// Whether a cited word is one of the request's words. Planners inflect and expand what they
    /// quote ("repo" for "repository"), which is the same word rather than an invented one; a
    /// genuinely new instruction word such as "delete" shares no stem with anything requested.
    /// </summary>
    private static bool IsCitedBy(string word, IReadOnlyList<string> requestWords)
    {
        foreach (string candidate in requestWords)
        {
            if (string.Equals(candidate, word, StringComparison.OrdinalIgnoreCase))
                return true;
            int shared = Math.Min(candidate.Length, word.Length);
            if (shared >= 4
                && (candidate.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                    || word.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadOptionalString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString()?.Trim() ?? "" : "";

    private static bool Invalid(string reason, out string error)
    {
        error = reason;
        return false;
    }

    // Closed-class function words and request scaffolding. The coverage check exists to catch a
    // plan that DROPS a requested outcome, so it must compare content words only: rejecting a
    // sound plan because the planner did not quote "i then want you to" ends the session before a
    // single action is sent, which is strictly worse than the omission it is guarding against.
    // Every word that could name an application, destination, or action stays substantive.
    private static readonly HashSet<string> RequestFunctionWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "this", "that", "these", "those", "any", "some", "there", "here",
        "i", "me", "my", "mine", "you", "your", "yours", "we", "us", "our", "it", "its", "them", "their",
        "am", "is", "are", "was", "were", "be", "been", "being",
        "do", "does", "did", "have", "has", "had",
        "can", "could", "will", "would", "shall", "should", "may", "might", "must",
        "want", "wants", "wanted", "need", "needs", "needed", "like", "let", "lets", "please", "kindly",
        "to", "of", "for", "on", "in", "at", "with", "from", "by", "into", "onto", "up", "over",
        "and", "or", "but", "so", "as", "than",
        "then", "now", "also", "next", "after", "first", "just", "ahead", "thanks", "thank"
    };

    private static string[] SourceWords(string value)
        => System.Text.RegularExpressions.Regex.Matches(value, @"[\p{L}\p{N}_]+")
            .Select(match => match.Value.ToLowerInvariant())
            .Where(word => !RequestFunctionWords.Contains(word))
            .ToArray();

    public ComputerUseTaskProgress Progress(IReadOnlyList<string> completed) => new()
    {
        Completed = completed,
        Current = Current?.Description ?? "",
        Next = _outcomes.Skip(_index + 1).FirstOrDefault()?.Description ?? "",
        Remaining = _outcomes.Skip(_index + 2).Select(outcome => outcome.Description).ToArray(),
        PlanInitialized = true
    };

    public string Describe() => Current == null ? "All contracted outcomes are complete." :
        $"Outcome {_index + 1}/{_outcomes.Count}: {Current.Description}\n" +
        $"Required destination: {Current.Destination}; match: {(Current.SiteMatch ? "site" : "exact")}; separate tab: {Current.SeparateTab}\n" +
        $"Required application: {Current.Application}; required visible text: {string.Join("; ", Current.RequiredText)}";

    public bool TryComplete(ComputerUseCapture capture, ComputerUseVisualAssessment assessment, out string reason)
    {
        // Controller-side evidence is read first and does not depend on the model agreeing with
        // it. An exact-URL outcome is finished outright here: the loaded document address is the
        // whole of its criteria, and a cautious "not loaded yet" from the model must not be able
        // to strand a goal the controller can already prove.
        int indexBeforeObservation = _index;
        string observed = ObserveCurrentOutcome(capture);
        if (_index > indexBeforeObservation)
        {
            reason = observed;
            return true;
        }

        if (Current == null)
        {
            reason = "All contracted outcomes are complete.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(assessment.Evidence))
        {
            reason = "The current outcome has no supporting visual assessment.";
            return false;
        }

        if (!assessment.GoalCompleted)
        {
            // Said as PROGRESS, not as a failure. The old wording — "no supporting visual
            // assessment" — was sent even when an assessment existed and described real work done,
            // e.g. "a large circle has been drawn, but the eyes and mouth are missing". Told its
            // work had not been assessed, the model treated a correct first stroke as a failure and
            // redid it, instead of going on to the parts that were actually missing.
            reason = "Not complete yet. What is visible now: " + assessment.Evidence.Trim()
                + " Everything already done stays done — do not redo it. Continue with the part that is still missing.";
            return false;
        }
        if (Current.Application.Length > 0
            && !ComputerUseApplicationVerification.IsRequestedApplicationVisible(Current.Application, capture))
        {
            reason = $"Required application is {Current.Application}; observed {capture.ForegroundProcessName}: {capture.ForegroundWindowTitle}.";
            return false;
        }
        foreach (string required in Current.RequiredText)
        {
            if (!capture.UiTargets.Any(target => target.Name.Contains(required, StringComparison.Ordinal)
                || target.Value.Contains(required, StringComparison.Ordinal)))
            {
                reason = "The required text has not been observed in the current application's accessible controls.";
                return false;
            }
        }
        // Only site-match outcomes reach here: the destination is a precondition, and the rest of
        // the criteria live in the description for the assessment to judge. The destination itself
        // is taken from the latch, never re-read from the active tab, so moving on does not undo it.
        if (Current.Kind == "browser" && !_destinationReached)
        {
            var browser = capture.BrowserState;
            reason = Current.SeparateTab && !HasSeparateTab(capture)
                ? "The destination has not been observed in a different tab with the prior destination's tab still present."
                : $"Required loaded destination is {Current.Destination}; observed {browser?.DocumentAddress ?? "unknown"}. This outcome remains unfinished.";
            return false;
        }
        reason = "Verified outcome: " + Current.Description;
        Advance();
        return true;
    }

    private bool HasSeparateTab(ComputerUseCapture capture)
    {
        var browser = capture.BrowserState;
        return _previousBrowser != null && browser != null
            && capture.TargetWindowHandle == _previousBrowser.Window
            && _previousBrowser.Window != IntPtr.Zero && _previousBrowser.Tab.Length > 0
            && browser.ActiveTabId.Length > 0 && browser.ActiveTabId != _previousBrowser.Tab
            && browser.TabIds.Contains(_previousBrowser.Tab)
            && !_previousBrowser.Tabs.Contains(browser.ActiveTabId);
    }

    public bool BlocksAction(ComputerUseAction action, ComputerUseCapture capture, out string reason, bool addressEntryExpected = false)
    {
        reason = "";
        if (Current?.Kind != "browser") return false;
        if (action.Type == ComputerUseActionType.Type
            && (addressEntryExpected || capture.BrowserState?.AddressHasFocus == true)
            && TryNavigationUrl(action.Text, out string url)
            && !MatchesDestination(Current, url))
        {
            bool alreadyDone = _outcomes.Take(_index).Any(outcome => outcome.Kind == "browser" && MatchesDestination(outcome, url));
            // Same host as the required exact destination but a different path is a mistyped or
            // invented variant of THIS destination -- an owner or path the model guessed -- and it
            // navigates straight to a 404 that then has to be recovered from. A different host is
            // ordinary intermediate navigation (sign-in, SSO, a redirector) and must stay allowed.
            bool guessedVariant = !Current.SiteMatch && SharesHost(Current.Destination, url);
            if (!alreadyDone && !guessedVariant)
                return false;

            reason = alreadyDone
                ? $"[OUTCOME DESTINATION BLOCK] Current outcome requires {Current.Destination}, not {url}. That destination is already complete and must not be revisited."
                : $"[OUTCOME DESTINATION BLOCK] Current outcome requires exactly {Current.Destination}, not {url}. Type the required destination character for character; do not substitute a guessed owner, path, or query.";
            return true;
        }
        if (Current.SeparateTab && !HasSeparateTab(capture)
            && (action.Type == ComputerUseActionType.Type
                || action.Type == ComputerUseActionType.Key && action.Keys.Equals("Enter", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "[OUTCOME TAB BLOCK] Create/select the requested new tab before entering its destination. The prior destination's tab must remain open.";
            return true;
        }
        return false;
    }

    public bool ConfirmsNavigation(string requestedUrl, ComputerUseCapture capture)
        => Current?.Kind == "browser" && capture.BrowserState is { IsErrorPage: false } browser
            && MatchesDestination(Current, requestedUrl) && MatchesDestination(Current, browser.DocumentAddress);

    private static bool TryNavigationUrl(string text, out string url)
    {
        if (ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl(text, out url)) return true;
        string value = text.Trim();
        // Address fields commonly omit the scheme. Never reinterpret a search phrase as a URL.
        return !value.Any(char.IsWhiteSpace) && value.Split('/')[0].Contains('.')
            && ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl("https://" + value, out url);
    }

    private static string HostOf(Uri uri)
        => uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;

    private static bool SharesHost(string expected, string actual)
        => Uri.TryCreate(expected, UriKind.Absolute, out Uri? left)
            && Uri.TryCreate(actual, UriKind.Absolute, out Uri? right)
            && string.Equals(HostOf(left), HostOf(right), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesDestination(Outcome outcome, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var actual)) return false;
        var expected = new Uri(outcome.Destination);
        if (!outcome.SiteMatch) return ComputerUseBrowserVerification.AreEquivalentUrls(outcome.Destination, value);
        return actual.Scheme is "http" or "https" && string.Equals(HostOf(expected), HostOf(actual), StringComparison.OrdinalIgnoreCase);
    }
}
