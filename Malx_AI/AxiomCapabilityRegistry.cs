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
        public string IconGlyph { get; set; } = "✦";
        public bool IsBuiltIn { get; set; }
        public bool IsAttached { get; set; }
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

        public AxiomSkillDefinition AddCustomSkill(string name, string description, string instructions, string activationTerms)
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

                    List<AxiomSkillDefinition> relevantSkills = attachedSkills
                        .Where(skill => IsSkillRelevant(skill, userMessage))
                        .Take(3)
                        .ToList();
                    if (relevantSkills.Count > 0)
                    {
                        builder.AppendLine("Load and follow these relevant skill procedures for this turn:");
                        foreach (AxiomSkillDefinition skill in relevantSkills)
                        {
                            builder.AppendLine($"<skill name=\"{EscapeAttribute(skill.Name)}\">");
                            builder.AppendLine(skill.Instructions.Trim());
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

        private static List<AxiomSkillDefinition> CreateDefaultSkills() =>
        [
            new()
            {
                Id = "pdf-studio",
                Name = "PDF Studio",
                Description = "Design polished, print-ready reports and documents for PDF delivery.",
                Instructions = "Clarify the audience and purpose from the request, then create a complete document with a strong title, concise executive summary when useful, logical heading hierarchy, readable paragraphs, accessible tables, source notes, and a final quality check. Preserve facts from attached documents exactly. If Axiom does not expose a confirmed binary PDF export tool on the current surface, return clean print-ready Markdown or HTML and say that it is ready to export; never claim a PDF file was saved without a tool result or path.",
                ActivationTerms = "pdf,report,white paper,whitepaper,print-ready,print ready,handout,brochure",
                IconGlyph = "PDF",
                IsBuiltIn = true
            },
            new()
            {
                Id = "slide-deck-studio",
                Name = "Slide Deck Studio",
                Description = "Build focused presentation narratives, slide structures, and speaker notes.",
                Instructions = "Turn the request into a coherent presentation story. Start with the audience, objective, and single takeaway. Produce a title slide followed by one idea per slide, short titles that state the point, concise body copy, suggested visuals only when they add evidence, and optional speaker notes. Keep terminology and numbers consistent across slides. If no confirmed PPTX or presentation export tool is available on the current surface, return a numbered slide-by-slide deck specification and never claim a PowerPoint file was saved.",
                ActivationTerms = "powerpoint,ppt,pptx,slide,slides,slide deck,presentation,pitch deck,keynote",
                IconGlyph = "▤",
                IsBuiltIn = true
            },
            new()
            {
                Id = "document-summarizer",
                Name = "Document Summarizer",
                Description = "Summarize attached files with traceable findings and explicit gaps.",
                Instructions = "Read the supplied document context before answering. Identify the document purpose, main claims, decisions, evidence, dates, named entities, risks, and action items. Distinguish what the document states from your inference. Cite page, slide, sheet, section, or filename markers when the extracted context provides them. Do not invent missing content and do not ask the user to reattach a file that Axiom already supplied in the prompt.",
                ActivationTerms = "summarize,summary,key points,main points,tldr,tl;dr,document,file,attachment,pdf,presentation,spreadsheet",
                IconGlyph = "≡",
                IsBuiltIn = true
            },
            new()
            {
                Id = "data-analysis",
                Name = "Data Analysis",
                Description = "Analyze tables and datasets with reproducible calculations and clear charts.",
                Instructions = "Define the question and units before calculating. Inspect column meanings and missing values, show the formula or method for material metrics, use the calculator or Python tool when the host exposes it, validate totals and edge cases, and separate observed results from interpretation. Recommend a chart only when it clarifies the decision; specify axes, units, labels, and source. Never fabricate rows or imply code executed unless a tool result confirms execution.",
                ActivationTerms = "data,dataset,csv,spreadsheet,table,analyze,analysis,chart,graph,plot,statistics,average,median,trend,kpi",
                IconGlyph = "⌁",
                IsBuiltIn = true
            },
            new()
            {
                Id = "code-review",
                Name = "Code Review",
                Description = "Review, repair, and verify code with implementation-first discipline.",
                Instructions = "Understand the requested behavior and inspect supplied code or repository evidence before proposing changes. Prioritize correctness, security, data loss, concurrency, and runtime integration issues. Keep changes scoped, preserve unrelated behavior, provide complete runnable code or a valid Axiom codebase patch when that surface requires it, and verify with available build, test, or sandbox tools. Do not claim files changed or tests passed unless the host confirms it.",
                ActivationTerms = "code,bug,debug,fix,refactor,review,repository,repo,compile,build,test,implementation,function,class,api",
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
