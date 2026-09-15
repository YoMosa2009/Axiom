using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Malx_AI
{
    public sealed class AxiomSkillDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Untitled skill";
        public string Description { get; set; } = "";
        public string Instructions { get; set; } = "";
        public string ActivationTerms { get; set; } = "";

        /// <summary>Extra rules stated as prohibitions, so a Skill's limits survive prompt compression.</summary>
        public string Boundaries { get; set; } = "";

        /// <summary>Artifact format this Skill renders into Project Canvas; empty means it answers in chat.</summary>
        public string DeliverableFormat { get; set; } = SkillDeliverableFormats.None;

        /// <summary>
        /// Terms that ask for the rendered deliverable specifically. Narrower than
        /// <see cref="ActivationTerms"/>: "average of column B" should load the Data Analysis
        /// procedure without turning a one-number answer into a full rendered report.
        /// </summary>
        public string CanvasTerms { get; set; } = "";

        /// <summary>What the rendered artifact must contain, appended to the canvas delivery prompt.</summary>
        public string CanvasContract { get; set; } = "";

        /// <summary>
        /// What a model too small to author HTML is asked for instead; Axiom composes the
        /// artifact from it. See <see cref="SkillSmallModelFormats"/>.
        /// </summary>
        public string SmallModelFormat { get; set; } = SkillSmallModelFormats.Markdown;

        public string IconGlyph { get; set; } = "✦";
        public bool IsBuiltIn { get; set; }
        public bool IsAttached { get; set; }

        public bool RendersToCanvas => SkillDeliverableFormats.IsRenderable(DeliverableFormat);
    }

    public sealed class AxiomPluginDefinition
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Instructions { get; set; } = "";
        public string ActivationTerms { get; set; } = "";
        public string CapabilityLabel { get; set; } = "";
        public string IconGlyph { get; set; } = "◇";
        public bool IsAttached { get; set; }
    }

    public sealed class AxiomCapabilitySnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public List<AxiomSkillDefinition> Skills { get; set; } = new();
        public List<AxiomPluginDefinition> Plugins { get; set; } = new();
    }

    /// <summary>
    /// Global Axiom Skills/Plugins state. Attachments intentionally live outside individual chats,
    /// models, and execution modes so one choice applies consistently throughout the app.
    /// </summary>
    public sealed class AxiomCapabilityRegistry
    {
        public const string WebResearchPluginId = "web-research";
        public const string DataLabPluginId = "data-lab";
        public const string FileIntelligencePluginId = "file-intelligence";
        public const string ConnectedAppsPluginId = "connected-apps";
        public const string CreatorStudioPluginId = "creator-studio";

        private const int MaxInjectedInstructionChars = 9000;
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private static readonly char[] ActivationSeparators = [',', ';', '\r', '\n', '|'];
        private readonly object _gate = new();
        private readonly string _statePath;
        private bool _loaded;

        public static AxiomCapabilityRegistry Shared { get; } = new();

        public List<AxiomSkillDefinition> Skills { get; private set; } = new();
        public List<AxiomPluginDefinition> Plugins { get; private set; } = new();
        public string LastLoadStatusMessage { get; private set; } = "";

        public AxiomCapabilityRegistry(string? statePath = null)
        {
            _statePath = statePath ?? Path.Combine(AppDataPaths.ChatHistory, "axiom_capabilities.json");
        }

        public void EnsureLoaded()
        {
            lock (_gate)
            {
                if (_loaded)
                    return;

                LoadCore();
                _loaded = true;
            }
        }

        public void Save()
        {
            lock (_gate)
            {
                EnsureLoaded();
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath) ?? AppDataPaths.ChatHistory);
                AtomicFileWriter.WriteAllText(_statePath, JsonSerializer.Serialize(CaptureSnapshot(), WriteOptions));
            }
        }

        public void SetSkillAttached(string id, bool attached)
        {
            lock (_gate)
            {
                EnsureLoaded();
                AxiomSkillDefinition? skill = Skills.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (skill == null || skill.IsAttached == attached)
                    return;

                skill.IsAttached = attached;
                Save();
            }
        }

        public void SetPluginAttached(string id, bool attached)
        {
            lock (_gate)
            {
                EnsureLoaded();
                AxiomPluginDefinition? plugin = Plugins.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (plugin == null || plugin.IsAttached == attached)
                    return;

                plugin.IsAttached = attached;
                Save();
            }
        }

        public AxiomSkillDefinition AddCustomSkill(
            string name,
            string description,
            string instructions,
            string activationTerms,
            string deliverableFormat = SkillDeliverableFormats.None,
            string canvasContract = "")
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A skill name is required.", nameof(name));
            if (string.IsNullOrWhiteSpace(instructions))
                throw new ArgumentException("Skill instructions are required.", nameof(instructions));

            lock (_gate)
            {
                EnsureLoaded();
                var skill = new AxiomSkillDefinition
                {
                    Id = "custom-" + Guid.NewGuid().ToString("N"),
                    Name = NormalizeText(name, 60),
                    Description = NormalizeText(description, 180),
                    Instructions = NormalizeText(instructions, 6000),
                    ActivationTerms = NormalizeText(activationTerms, 500),
                    DeliverableFormat = SkillDeliverableFormats.IsRenderable(deliverableFormat)
                        ? deliverableFormat.ToLowerInvariant()
                        : SkillDeliverableFormats.None,
                    CanvasContract = NormalizeText(canvasContract, 2000),
                    IconGlyph = "✦",
                    IsBuiltIn = false,
                    IsAttached = true
                };
                Skills.Add(skill);
                Save();
                return skill;
            }
        }

        public bool RemoveCustomSkill(string id)
        {
            lock (_gate)
            {
                EnsureLoaded();
                AxiomSkillDefinition? skill = Skills.FirstOrDefault(item =>
                    !item.IsBuiltIn && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (skill == null)
                    return false;

                Skills.Remove(skill);
                Save();
                return true;
            }
        }

        public bool IsPluginAttached(string id)
        {
            lock (_gate)
            {
                EnsureLoaded();
                return Plugins.Any(plugin => plugin.IsAttached && string.Equals(plugin.Id, id, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool ShouldUseWebResearch(string userMessage)
            => IsPluginAttached(WebResearchPluginId) && MatchesAnyTerm(userMessage,
                "latest,current,today,recent,news,source,citation,verify,search,look up,online,price,release,documentation,policy,law,medical,financial");

        public bool ShouldUseDataTools(string userMessage)
            => IsPluginAttached(DataLabPluginId) && MatchesAnyTerm(userMessage,
                "analyze,data,csv,spreadsheet,chart,graph,plot,calculate,compute,statistics,average,median,percent,python,run code");

        /// <summary>
        /// The attached Skill, if any, that should hand this turn a rendered Project Canvas
        /// artifact instead of a prose answer. Returns null when no attached Skill renders, or
        /// when the request only matches a Skill's procedure terms and not its deliverable terms.
        /// </summary>
        public SkillCanvasDirective? ResolveCanvasDirective(string userMessage)
        {
            lock (_gate)
            {
                EnsureLoaded();
                AxiomSkillDefinition? skill = FindCanvasSkill(Skills.Where(item => item.IsAttached).ToList(), userMessage);
                return skill == null
                    ? null
                    : new SkillCanvasDirective(skill.Id, skill.Name, skill.DeliverableFormat, skill.CanvasContract, skill.SmallModelFormat);
            }
        }

        /// <summary>
        /// Picks the rendering Skill whose deliverable terms the request matches most strongly.
        /// Scoring by match count keeps "build me a slide deck about our data" with Slide Deck
        /// Studio rather than handing it to whichever rendering Skill was attached first.
        /// </summary>
        private static AxiomSkillDefinition? FindCanvasSkill(IReadOnlyCollection<AxiomSkillDefinition> attachedSkills, string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return null;

            AxiomSkillDefinition? best = null;
            int bestScore = 0;
            foreach (AxiomSkillDefinition skill in attachedSkills)
            {
                if (!skill.RendersToCanvas)
                    continue;

                // A custom rendering Skill with no deliverable terms falls back to its activation
                // terms: the user wrote it to render, so it should not need a second term list.
                string terms = string.IsNullOrWhiteSpace(skill.CanvasTerms) ? skill.ActivationTerms : skill.CanvasTerms;
                int score = CountMatchingTerms(userMessage, terms);
                if (score > bestScore)
                {
                    best = skill;
                    bestScore = score;
                }
            }

            return best;
        }

        private static int CountMatchingTerms(string text, string terms)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(terms))
                return 0;

            string normalized = text.Trim();
            return terms.Split(ActivationSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(term => term.Length >= 2 && normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        public string BuildSystemInstruction(string userMessage, string surfaceName)
        {
            lock (_gate)
            {
                EnsureLoaded();
                List<AxiomSkillDefinition> attachedSkills = Skills.Where(skill => skill.IsAttached).ToList();
                List<AxiomPluginDefinition> attachedPlugins = Plugins.Where(plugin => plugin.IsAttached).ToList();
                if (attachedSkills.Count == 0 && attachedPlugins.Count == 0)
                    return string.Empty;

                var builder = new StringBuilder();
                builder.AppendLine("[AXIOM ATTACHED CAPABILITIES]");
                builder.AppendLine($"Surface: {surfaceName}. These capabilities are attached globally and apply regardless of the selected model or mode.");
                builder.AppendLine("Use a capability only when it is relevant to the user's request. Attached Skills are procedures, not proof that an external action occurred. Plugins expose only the tools and connectors the Axiom host actually provides on this turn. Never claim that a file was created, code ran, the web was searched, or an external service changed unless a host tool result confirms it. If a requested exporter or tool is unavailable, provide the best directly usable fallback and state the limitation briefly.");

                if (attachedSkills.Count > 0)
                {
                    builder.AppendLine("Attached skill catalog:");
                    foreach (AxiomSkillDefinition skill in attachedSkills)
                        builder.AppendLine($"- {skill.Name}: {skill.Description}");

                    // The Skill delivering this turn's artifact leads the list: it must survive the
                    // three-skill cap, or the canvas contract arrives without the procedure behind it.
                    AxiomSkillDefinition? canvasSkill = FindCanvasSkill(attachedSkills, userMessage);
                    List<AxiomSkillDefinition> relevantSkills = attachedSkills
                        .Where(skill => IsSkillRelevant(skill, userMessage) && skill != canvasSkill)
                        .Take(canvasSkill == null ? 3 : 2)
                        .ToList();
                    if (canvasSkill != null)
                        relevantSkills.Insert(0, canvasSkill);

                    if (relevantSkills.Count > 0)
                    {
                        builder.AppendLine("Load and follow these relevant skill procedures for this turn:");
                        foreach (AxiomSkillDefinition skill in relevantSkills)
                        {
                            builder.AppendLine($"<skill name=\"{EscapeAttribute(skill.Name)}\">");
                            builder.AppendLine(skill.Instructions.Trim());
                            if (!string.IsNullOrWhiteSpace(skill.Boundaries))
                                builder.AppendLine("Limits: " + skill.Boundaries.Trim());
                            builder.AppendLine("</skill>");
                        }
                    }
                }

                if (attachedPlugins.Count > 0)
                {
                    builder.AppendLine("Attached plugin packages:");
                    foreach (AxiomPluginDefinition plugin in attachedPlugins)
                    {
                        builder.AppendLine($"- {plugin.Name} ({plugin.CapabilityLabel}): {plugin.Description}");
                        if (IsPluginRelevant(plugin, userMessage))
                        {
                            builder.AppendLine($"<plugin name=\"{EscapeAttribute(plugin.Name)}\">");
                            builder.AppendLine(plugin.Instructions.Trim());
                            builder.AppendLine("</plugin>");
                        }
                    }
                }

                builder.Append("[/AXIOM ATTACHED CAPABILITIES]");
                string result = builder.ToString();
                return result.Length <= MaxInjectedInstructionChars
                    ? result
                    : result[..MaxInjectedInstructionChars] + "\n[/AXIOM ATTACHED CAPABILITIES]";
            }
        }

        private void LoadCore()
        {
            AxiomCapabilitySnapshot? loaded = null;
            if (File.Exists(_statePath))
            {
                JsonPersistenceRecoveryResult<AxiomCapabilitySnapshot> result = JsonPersistenceRecovery.Load<AxiomCapabilitySnapshot>(_statePath);
                loaded = result.Value;
                LastLoadStatusMessage = result.StatusMessage;
            }

            Skills = MergeSkills(loaded?.Skills);
            Plugins = MergePlugins(loaded?.Plugins);
        }

        private AxiomCapabilitySnapshot CaptureSnapshot() => new()
        {
            Skills = Skills.Select(CloneSkill).ToList(),
            Plugins = Plugins.Select(ClonePlugin).ToList()
        };

        private static List<AxiomSkillDefinition> MergeSkills(IReadOnlyCollection<AxiomSkillDefinition>? saved)
        {
            var result = new List<AxiomSkillDefinition>();
            Dictionary<string, AxiomSkillDefinition> savedById = (saved ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            foreach (AxiomSkillDefinition builtIn in CreateDefaultSkills())
            {
                if (savedById.TryGetValue(builtIn.Id, out AxiomSkillDefinition? persisted))
                    builtIn.IsAttached = persisted.IsAttached;
                result.Add(builtIn);
            }

            result.AddRange((saved ?? [])
                .Where(item => !item.IsBuiltIn && item.Id.StartsWith("custom-", StringComparison.OrdinalIgnoreCase))
                .Select(CloneSkill));
            return result;
        }

        private static List<AxiomPluginDefinition> MergePlugins(IReadOnlyCollection<AxiomPluginDefinition>? saved)
        {
            Dictionary<string, AxiomPluginDefinition> savedById = (saved ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            List<AxiomPluginDefinition> result = CreateDefaultPlugins();
            foreach (AxiomPluginDefinition plugin in result)
            {
                if (savedById.TryGetValue(plugin.Id, out AxiomPluginDefinition? persisted))
                    plugin.IsAttached = persisted.IsAttached;
            }
            return result;
        }

        private static bool IsSkillRelevant(AxiomSkillDefinition skill, string userMessage)
        {
            if (string.IsNullOrWhiteSpace(skill.ActivationTerms))
                return !skill.IsBuiltIn;
            return MatchesAnyTerm(userMessage, skill.ActivationTerms);
        }

        private static bool IsPluginRelevant(AxiomPluginDefinition plugin, string userMessage)
            => string.IsNullOrWhiteSpace(plugin.ActivationTerms) || MatchesAnyTerm(userMessage, plugin.ActivationTerms);

        private static bool MatchesAnyTerm(string text, string terms)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(terms))
                return false;
            string normalized = text.Trim();
            return terms.Split(ActivationSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(term => term.Length >= 2 && normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeText(string value, int maxLength)
        {
            string normalized = (value ?? string.Empty).Trim();
            return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
        }

        private static string EscapeAttribute(string value)
            => (value ?? string.Empty).Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

        private static AxiomSkillDefinition CloneSkill(AxiomSkillDefinition source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            Instructions = source.Instructions,
            ActivationTerms = source.ActivationTerms,
            Boundaries = source.Boundaries,
            DeliverableFormat = source.DeliverableFormat,
            CanvasTerms = source.CanvasTerms,
            CanvasContract = source.CanvasContract,
            SmallModelFormat = source.SmallModelFormat,
            IconGlyph = source.IconGlyph,
            IsBuiltIn = source.IsBuiltIn,
            IsAttached = source.IsAttached
        };

        private static AxiomPluginDefinition ClonePlugin(AxiomPluginDefinition source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            Instructions = source.Instructions,
            ActivationTerms = source.ActivationTerms,
            CapabilityLabel = source.CapabilityLabel,
            IconGlyph = source.IconGlyph,
            IsAttached = source.IsAttached
        };

        // Canvas contracts live as constants so the requirement list stays readable as a list.
        // Raw string indentation is stripped against the closing delimiter, so the leading
        // whitespace here never reaches the model.
        private const string SlideDeckCanvasContract = """
            - One HTML document holding every slide. Slide 1 is a title slide (title, one-line subtitle, and a presenter or date line when the request supplies one).
            - 8-14 content slides unless the user asks for a different count. One idea per slide.
            - Each slide is a 16:9 stage that scales to the pane width. Content must never overflow its slide; cut copy rather than shrinking type below readability.
            - Exactly one slide is visible at a time. Provide Previous/Next controls, ArrowLeft / ArrowRight / Space keyboard navigation, and a "current / total" counter.
            - Carry the evidence visually: CSS or inline SVG bar and line comparisons, split layouts, large callout figures, labelled diagrams, timelines. At least a third of the content slides need a real drawn visual.
            - Put each slide's speaker notes in a panel that is hidden by default and toggles with the "N" key.
            - Define one restrained colour and type system in :root and reuse it on every slide.
            """;

        private const string PdfStudioCanvasContract = """
            - One HTML document laid out as printed pages: a bounded content column, generous margins, and clear separation between sections.
            - Readable body type with a comfortable line height, a clear heading scale, and consistent spacing above and below every heading.
            - Include a title block (title, subtitle, date or author line when known) and, for anything past roughly four sections, a contents list linking to the section anchors.
            - Tables get a header row, aligned numeric columns, and units in the header rather than repeated in every cell.
            - Include an @media print block that sets page margins, avoids breaking inside headings and tables, and hides on-screen-only controls, so the user's own PDF export matches what they see.
            """;

        private const string DataAnalysisCanvasContract = """
            - One HTML document that opens with the headline finding in a sentence, then the evidence behind it.
            - Draw every chart yourself with inline SVG or the canvas API: no charting library is reachable offline, and plotted coordinates are enough for bars, lines, and scatter plots.
            - Every chart carries a title, labelled axes with units, readable tick values, and a caption naming the data source. One accent colour for the series under discussion, neutral grey for context series.
            - Put the underlying figures in a real table beneath each chart so the numbers stay checkable.
            - Close with a short "what this does not show" note covering the gaps, caveats, or missing data you found.
            """;

        private static List<AxiomSkillDefinition> CreateDefaultSkills() =>
        [
            new()
            {
                Id = "slide-deck-studio",
                Name = "Slide Deck Studio",
                Description = "Builds a real, navigable slide deck and renders it in Project Canvas.",
                Instructions = "Decide the audience, the objective, and the single takeaway before writing a slide. Give every slide a title that states its point rather than naming its topic, keep one idea per slide, and keep numbers and terminology consistent across the deck. Design slides as slides: short lines, generous spacing, and a visual wherever it carries the evidence better than a sentence would. Write speaker notes for the slides that need them.",
                Boundaries = "Never return only an outline, a bullet summary, or a description of the deck you would build. Never split the deck across several code blocks. Never claim a .pptx, .key, or .pdf file was generated or saved.",
                ActivationTerms = "powerpoint,ppt,pptx,slide,slides,slide deck,slidedeck,deck,presentation,pitch deck,keynote,slideshow",
                DeliverableFormat = SkillDeliverableFormats.Html,
                CanvasTerms = "powerpoint,ppt,pptx,slide,slides,slide deck,slidedeck,deck,presentation,pitch deck,keynote,slideshow",
                CanvasContract = SlideDeckCanvasContract,
                SmallModelFormat = SkillSmallModelFormats.Outline,
                IconGlyph = "\u25A4",
                IsBuiltIn = true
            },
            new()
            {
                Id = "pdf-studio",
                Name = "PDF Studio",
                Description = "Lays out print-ready reports and documents, rendered in Project Canvas.",
                Instructions = "Establish the audience and purpose from the request, then write the whole document: a strong title, an executive summary when the length warrants one, a logical heading hierarchy, readable paragraphs, accessible tables, and source notes. Preserve facts, figures, and wording from attached documents exactly as supplied.",
                Boundaries = "Never claim a PDF file was written, saved, exported, or attached; Axiom renders the document and the user exports it. Never invent figures, citations, or sources the request and attachments did not supply.",
                ActivationTerms = "pdf,report,white paper,whitepaper,print-ready,print ready,handout,brochure,one-pager,one pager,memo,datasheet",
                DeliverableFormat = SkillDeliverableFormats.Html,
                CanvasTerms = "pdf,report,white paper,whitepaper,print-ready,print ready,handout,brochure,one-pager,one pager,datasheet",
                CanvasContract = PdfStudioCanvasContract,
                SmallModelFormat = SkillSmallModelFormats.Markdown,
                IconGlyph = "PDF",
                IsBuiltIn = true
            },
            new()
            {
                Id = "data-analysis",
                Name = "Data Analysis",
                Description = "Analyzes tables and datasets, and renders charted findings in Project Canvas.",
                Instructions = "Define the question and the units before calculating. Inspect column meanings and missing values, state the formula or method behind any material metric, use the calculator or Python tool when the host exposes it, check totals and edge cases, and keep observed results separate from interpretation. When the answer is a single number or a short comparison, answer it directly instead of building a report around it.",
                Boundaries = "Never fabricate rows, columns, or totals. Never imply code ran unless a tool result is present. Never chart a comparison the data does not support, and never leave an axis without units.",
                ActivationTerms = "data,dataset,csv,spreadsheet,table,analyze,analyse,analysis,chart,graph,plot,statistics,average,median,trend,kpi,correlation,distribution",
                DeliverableFormat = SkillDeliverableFormats.Html,
                CanvasTerms = "chart,graph,plot,visualize,visualise,visualisation,visualization,dashboard,infographic,breakdown,histogram,scatter,trend line",
                CanvasContract = DataAnalysisCanvasContract,
                SmallModelFormat = SkillSmallModelFormats.Chart,
                IconGlyph = "\u2301",
                IsBuiltIn = true
            },
            new()
            {
                Id = "document-summarizer",
                Name = "Document Summarizer",
                Description = "Summarizes attached files in chat with traceable findings and explicit gaps.",
                Instructions = "Read the supplied document context before answering. Identify the purpose, the main claims, decisions, evidence, dates, named entities, risks, and action items. Distinguish what the document states from what you infer. Cite page, slide, sheet, section, or filename markers whenever the extracted context provides them, and say plainly which parts of the file did not extract.",
                Boundaries = "This Skill answers in chat and never produces a rendered artifact. Never invent content that is absent from the extracted context, and never ask the user to reattach a file Axiom already supplied in the prompt.",
                ActivationTerms = "summarize,summarise,summary,key points,main points,tldr,tl;dr,gist,recap",
                IconGlyph = "\u2261",
                IsBuiltIn = true
            },
            new()
            {
                Id = "code-review",
                Name = "Code Review",
                Description = "Reviews, repairs, and verifies code in chat with implementation-first discipline.",
                Instructions = "Understand the requested behavior and read the supplied code or repository evidence before proposing a change. Prioritize correctness, security, data loss, concurrency, and runtime integration over style. Keep changes scoped, preserve unrelated behavior, return complete runnable code or a valid Axiom codebase patch when the surface requires it, and verify with the build, test, or sandbox tools the host exposes.",
                Boundaries = "This Skill answers in chat and never produces a rendered artifact. Never claim files changed, a build ran, or tests passed unless a host tool result confirms it. Never rewrite code you were not shown.",
                ActivationTerms = "code,bug,debug,fix,refactor,review,repository,repo,compile,build,test,stack trace,exception,function,class,api",
                IconGlyph = "</>",
                IsBuiltIn = true
            }
        ];

        private static List<AxiomPluginDefinition> CreateDefaultPlugins() =>
        [
            new()
            {
                Id = WebResearchPluginId,
                Name = "Web Research",
                Description = "Adds current-source research behavior using Axiom's existing web search pipeline.",
                Instructions = "Use Axiom's web_search tool for current, unstable, niche, source-backed, policy, pricing, release, legal, medical, or financial claims when the tool is exposed. Resolve references from prior turns into a standalone query, prefer focused searches, synthesize results, and cite only evidence the tool actually returned. If web_search is unavailable, state that current verification is unavailable instead of fabricating sources.",
                ActivationTerms = "latest,current,today,recent,news,source,citation,verify,search,look up,online,price,release,documentation,policy,law,medical,financial",
                CapabilityLabel = "Web search",
                IconGlyph = "◎"
            },
            new()
            {
                Id = DataLabPluginId,
                Name = "Data Lab",
                Description = "Combines Axiom's Python sandbox and calculator for reproducible analysis.",
                Instructions = "Use calculate for direct arithmetic and unit conversions. Use run_python only for meaningful multi-step computation, validation, data transformation, or chart generation when the host exposes it. Keep code self-contained, use the standard library unless the runtime confirms another package, print the result needed for verification, and integrate tool output into the answer. Never install packages or claim execution without a tool result.",
                ActivationTerms = "analyze,data,csv,spreadsheet,chart,graph,plot,calculate,compute,statistics,average,median,percent,python,run code",
                CapabilityLabel = "Python + calculator",
                IconGlyph = "ƒx"
            },
            new()
            {
                Id = FileIntelligencePluginId,
                Name = "File Intelligence",
                Description = "Uses Axiom's attachment extraction and retrieval context across supported file types.",
                Instructions = "Treat Axiom-provided attachment blocks and retrieved chunks as the readable file contents. Ground answers in those blocks, retain filename/page/slide/sheet markers, identify extraction gaps, and never ask the user to upload content already present. This plugin does not grant arbitrary filesystem access; use only attachments and host-provided workspace tools.",
                ActivationTerms = "file,document,attachment,pdf,docx,pptx,presentation,spreadsheet,xlsx,csv,ebook,code file,summarize",
                CapabilityLabel = "Attachments + retrieval",
                IconGlyph = "▱"
            },
            new()
            {
                Id = ConnectedAppsPluginId,
                Name = "Connected Apps",
                Description = "Packages Axiom's configured MCP connectors for cloud and Hybrid Local models.",
                Instructions = "Use only connector tools the host exposes for the current turn. Read before writing when practical, follow each tool schema exactly, and confirm the target before consequential actions. A connector may require setup in Settings and may be unavailable in local-only inference. Never claim an email, file, task, or external record changed unless the connector tool returns success.",
                ActivationTerms = "gmail,email,drive,google,todoist,dropbox,github,connector,connected app,calendar,task,cloud file",
                CapabilityLabel = "Configured connectors",
                IconGlyph = "⌘"
            },
            new()
            {
                Id = CreatorStudioPluginId,
                Name = "Creator Studio",
                Description = "Coordinates attached document and presentation Skills into consistent deliverables.",
                Instructions = "For a requested deliverable, choose the relevant attached authoring Skill, preserve user-provided facts and branding instructions, maintain consistent terminology, and perform a final structure/readability check. Use only confirmed Axiom exporters or artifact surfaces. If binary export is unavailable, return a directly usable source format and label it accurately rather than claiming a file exists.",
                ActivationTerms = "pdf,report,document,powerpoint,pptx,slides,presentation,deck,handout,brochure,deliverable",
                CapabilityLabel = "Authoring workflows",
                IconGlyph = "✦"
            }
        ];
    }
}
