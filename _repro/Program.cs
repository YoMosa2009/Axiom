using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

// Headless reproduction harness for the gemma-2-9b CUDA illegal-memory-access crash.
// Reuses the app's already-wired native CUDA backend folder. Toggles GPU layer count so
// we can isolate: partial-offload vs full-offload vs CPU, and short vs long generation,
// and single-turn vs multi-turn. Writes llama.cpp's native log (incl. the abort reason)
// synchronously to native-llama-repro.log.

class Program
{
    static readonly string NativeDir = @"G:\VSS_Projects\Malx_AI\Malx_AI\bin\Debug\net10.0-windows\runtimes\win-x64\native\cuda12";
    static readonly string BaseNativeDir = @"G:\VSS_Projects\Malx_AI\Malx_AI\bin\Debug\net10.0-windows\runtimes\win-x64\native";
    static readonly string LogPath = @"C:\Users\mosaa\AppData\Local\Axiom\logs\native-llama-repro.log";
    static readonly object LogGate = new object();

    static async Task<int> Main(string[] args)
    {
        string modelPath = args.Length > 0 ? args[0] : @"C:\Users\mosaa\.lmstudio\models\lmstudio-community\gemma-2-9b-it-GGUF\gemma-2-9b-it-Q4_K_M.gguf";
        int ngl = args.Length > 1 ? int.Parse(args[1]) : 999;     // gpu layers (999 = full)
        uint ctx = args.Length > 2 ? uint.Parse(args[2]) : 8192;
        int maxTokens = args.Length > 3 ? int.Parse(args[3]) : 220; // long generation
        bool multiTurn = args.Length > 4 && args[4] == "multi";

        File.WriteAllText(LogPath, $"===== repro {DateTime.Now:HH:mm:ss} model={Path.GetFileName(modelPath)} ngl={ngl} ctx={ctx} maxTok={maxTokens} multi={multiTurn} =====\n");
        Log($"START ngl={ngl} ctx={ctx} maxTokens={maxTokens} multiTurn={multiTurn}");

        // Replicate the app's PATH setup so ggml-cuda.dll resolves its CUDA runtime deps.
        string cudaRuntime = FindCudaBin();
        if (cudaRuntime != null) Prepend("PATH", cudaRuntime);
        Prepend("PATH", NativeDir);
        Prepend("PATH", BaseNativeDir);

        var cfg = NativeLibraryConfig.LLama;
        cfg.WithCuda();
        cfg.WithLogCallback((LLamaLogLevel level, string msg) =>
        {
            try { lock (LogGate) { using var s = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite); var b = System.Text.Encoding.UTF8.GetBytes(level == LLamaLogLevel.Continue ? msg : $"[{level}] {msg}"); s.Write(b, 0, b.Length); s.Flush(true); } } catch { }
        });

        var mp = new ModelParams(modelPath)
        {
            ContextSize = ctx,
            GpuLayerCount = ngl,
            MainGpu = 0,
            BatchSize = 2048,
            UBatchSize = 512,   // matches app ApplyPerformanceTuning for this model (not tightVram/large)
            FlashAttention = false,   // gemma soft-capping: FA off (matches app)
            UseMemorymap = true,
            DefragThreshold = 0.1f,
        };

        Console.WriteLine($"Loading {Path.GetFileName(modelPath)} ngl={ngl} ctx={ctx} ...");
        using var weights = LLamaWeights.LoadFromFile(mp);

        // Match the app's RebuildChatSession system prompt (length/content can affect prefill batching).
        string sysP = "You are a helpful AI assistant and expert software engineer. Provide clear, concise, and accurate responses. Execute tasks directly and provide final output unless explicitly asked for step-by-step guidance.\n\nFor complex math or data processing tasks, solve them by writing a Python 3 script internally. Use print() for the final answer. Never expose internal Python/tool code, PAUSE directives, or raw calculation traces unless the user explicitly asks for them. In workplace/council mode, this can be triggered via [PAUSE: PYTHON_MATH | \"your_code_here\"].";

        if ((args.Length > 4 ? args[4] : "single") == "incremental")
        {
            // CANDIDATE FIX: keep ONE persistent context across both turns. Turn 2 is just
            // ChatAsync on the existing session (history already in KV) — no dispose, no
            // history re-prefill. This is structurally identical to turn 1, which works.
            var ipInc = new InferenceParams { MaxTokens = maxTokens, AntiPrompts = new[] { "<end_of_turn>", "<|im_end|>", "<|end_of_text|>" }.ToList(), SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.7f, TopK = 40, TopP = 0.95f, MinP = 0.05f, RepeatPenalty = 1.1f } };
            Log("INCREMENTAL: one persistent context for both turns");
            using var ctxI = weights.CreateContext(mp);
            var exI = new InteractiveExecutor(ctxI);
            var sesI = new ChatSession(exI);
            sesI.WithHistoryTransform(new LLama.Transformers.PromptTemplateTransformer(weights, withAssistant: true));
            sesI.AddSystemMessage(sysP);
            Log("INCREMENTAL turn1 ChatAsync(hello)");
            int it1 = await Stream(() => sesI.ChatAsync(new LLama.Common.ChatHistory.Message(AuthorRole.User, "hello"), new InferenceParams { MaxTokens = 40, AntiPrompts = ipInc.AntiPrompts, SamplingPipeline = ipInc.SamplingPipeline }), 40);
            Log($"INCREMENTAL turn1 done it1={it1}");
            Log("INCREMENTAL turn2 ChatAsync(neural network) — SAME context, NO replay");
            int it2 = await Stream(() => sesI.ChatAsync(new LLama.Common.ChatHistory.Message(AuthorRole.User, "what is a neural network?"), ipInc), maxTokens);
            Log($"INCREMENTAL turn2 done it2={it2}");
            Console.WriteLine($"\n[incremental OK it1={it1} it2={it2}]");
            Log("EXIT clean");
            return 0;
        }

        if ((args.Length > 4 ? args[4] : "single") == "appflow_save")
        {
            // Faithful to the app INCLUDING the per-turn KV SaveState. After turn 1 the app
            // calls _executor.SaveState(...) (fire-and-forget persistence). Test whether that
            // native KV serialization on a GPU context corrupts state for the next context.
            var ipS = new InferenceParams { MaxTokens = maxTokens, AntiPrompts = new[] { "<end_of_turn>", "<|im_end|>", "<|end_of_text|>" }.ToList(), SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.7f, TopK = 40, TopP = 0.95f, MinP = 0.05f, RepeatPenalty = 1.1f } };
            string statePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "repro_kvstate.bin");
            Log("APPFLOW_SAVE turn1 create ctxA");
            var ctxA = weights.CreateContext(mp);
            var exA = new InteractiveExecutor(ctxA);
            var sesA = new ChatSession(exA);
            sesA.WithHistoryTransform(new LLama.Transformers.PromptTemplateTransformer(weights, withAssistant: true));
            sesA.AddSystemMessage(sysP);
            Log("APPFLOW_SAVE turn1 ChatAsync(hello)");
            int t1 = await Stream(() => sesA.ChatAsync(new LLama.Common.ChatHistory.Message(AuthorRole.User, "hello"), new InferenceParams { MaxTokens = 40, AntiPrompts = ipS.AntiPrompts, SamplingPipeline = ipS.SamplingPipeline }), 40);
            string reply1s = lastResponse;
            Log("APPFLOW_SAVE SaveState(ctxA) — app KV persistence after the turn");
            await exA.SaveState(statePath, CancellationToken.None);
            Log("APPFLOW_SAVE dispose ctxA");
            ctxA.Dispose();
            Log("APPFLOW_SAVE turn2 create ctxB");
            var ctxB = weights.CreateContext(mp);
            var exB = new InteractiveExecutor(ctxB);
            var sesB = new ChatSession(exB);
            sesB.WithHistoryTransform(new LLama.Transformers.PromptTemplateTransformer(weights, withAssistant: true));
            sesB.AddSystemMessage(sysP);
            await sesB.AddAndProcessUserMessage("hello", CancellationToken.None);
            await sesB.AddAndProcessAssistantMessage(string.IsNullOrWhiteSpace(reply1s) ? "Hello!" : reply1s, CancellationToken.None);
            Log("APPFLOW_SAVE turn2 ChatAsync(user2)");
            int t2 = await Stream(() => sesB.ChatAsync(new LLama.Common.ChatHistory.Message(AuthorRole.User, "what is a neural network?"), ipS), maxTokens);
            Log($"APPFLOW_SAVE done t1={t1} t2={t2}");
            ctxB.Dispose();
            Console.WriteLine($"\n[appflow_save OK t1={t1} t2={t2}]");
            Log("EXIT clean");
            return 0;
        }

        if ((args.Length > 4 ? args[4] : "single") == "appflow")
        {
            // FULLY faithful to the app's gemma (non-strict) path: a NEW context is created
            // per turn (RebuildChatSessionWithPromptAsync disposes the old + makes a new one),
            // history is replayed via AddAndProcess*, then ChatAsync runs the turn.
            var ipFull = new InferenceParams { MaxTokens = maxTokens, AntiPrompts = new[] { "<end_of_turn>", "<|im_end|>" }.ToList(), SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.7f, TopK = 40, TopP = 0.95f, MinP = 0.05f, RepeatPenalty = 1.1f } };

            // ---- TURN 1: fresh context A, no history, ChatAsync("hello") ----
            Log("APPFLOW turn1: create context A");
            var ctxA = weights.CreateContext(mp);
            var exA = new InteractiveExecutor(ctxA);
            var sesA = new ChatSession(exA);
            sesA.WithHistoryTransform(new LLama.Transformers.PromptTemplateTransformer(weights, withAssistant: true));
            sesA.AddSystemMessage(sysP);
            Log("APPFLOW turn1: ChatAsync(hello)");
            int t1 = await Stream(() => sesA.ChatAsync(new LLama.Common.ChatHistory.Message(AuthorRole.User, "hello"), new InferenceParams { MaxTokens = 40, AntiPrompts = ipFull.AntiPrompts, SamplingPipeline = ipFull.SamplingPipeline }), 40);
            string reply1 = lastResponse;
            Log($"APPFLOW turn1 done t1={t1}");

            // ---- between turns: dispose context A (app frees old KV before new alloc) ----
            Log("APPFLOW dispose context A");
            ctxA.Dispose();

            // ---- TURN 2: fresh context B, replay [user1, asst1], ChatAsync(user2) ----
            Log("APPFLOW turn2: create context B");
            var ctxB = weights.CreateContext(mp);
            var exB = new InteractiveExecutor(ctxB);
            var sesB = new ChatSession(exB);
            sesB.WithHistoryTransform(new LLama.Transformers.PromptTemplateTransformer(weights, withAssistant: true));
            sesB.AddSystemMessage(sysP);
            Log("APPFLOW turn2: replay user1");
            await sesB.AddAndProcessUserMessage("hello", CancellationToken.None);
            Log("APPFLOW turn2: replay asst1");
            await sesB.AddAndProcessAssistantMessage(string.IsNullOrWhiteSpace(reply1) ? "Hello! How can I help you today?" : reply1, CancellationToken.None);
            Log("APPFLOW turn2: ChatAsync(user2)");
            int t2 = await Stream(() => sesB.ChatAsync(new LLama.Common.ChatHistory.Message(AuthorRole.User, "what is a neural network?"), ipFull), maxTokens);
            Log($"APPFLOW turn2 done t2={t2}");
            ctxB.Dispose();
            Console.WriteLine($"\n[appflow OK, t1={t1} t2={t2}]");
            Log("EXIT clean");
            return 0;
        }

        using var context = weights.CreateContext(mp);
        var executor = new InteractiveExecutor(context);

        var ip = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = new[] { "<end_of_turn>", "<|im_end|>", "<|endoftext|>" }.ToList(),
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.7f, TopK = 40, TopP = 0.95f, MinP = 0.05f, RepeatPenalty = 1.1f }
        };

        string sys = "You are a helpful AI assistant. Give clear, accurate answers.";

        string mode = args.Length > 4 ? args[4] : "single";

        if (mode == "session")
        {
            // Mimic the app's gemma (non-strict) turn-2 path EXACTLY:
            // fresh ChatSession + PromptTemplateTransformer, replay history via
            // AddAndProcess*, THEN ChatAsync on the (same) current user message.
            var session = new ChatSession(executor);
            session.WithHistoryTransform(new LLama.Transformers.PromptTemplateTransformer(weights, withAssistant: true));
            session.AddSystemMessage(sys);

            string a1 = "Hello! How can I help you today?";
            Log("SESSION replay user1");
            await session.AddAndProcessUserMessage("hello", CancellationToken.None);
            Log("SESSION replay asst1");
            await session.AddAndProcessAssistantMessage(a1, CancellationToken.None);
            bool doubleProcess = args.Length > 5 && args[5] == "double";
            if (doubleProcess)
            {
                Log("SESSION replay user2 (DOUBLE-PROCESS like app: replay includes current user)");
                await session.AddAndProcessUserMessage("what is a neural network?", CancellationToken.None);
            }
            Log("SESSION ChatAsync(user2)");
            int ns = await Stream(() => session.ChatAsync(new LLama.Common.ChatHistory.Message(AuthorRole.User, "what is a neural network?"), ip), maxTokens);
            Log($"SESSION done tokens={ns}");
            Console.WriteLine($"\n[session OK, {ns} tokens, doubleProcess={doubleProcess}]");
        }
        else if (!multiTurn)
        {
            // Single long generation — tests "does a long answer IMA on turn 1?"
            string prompt = BuildGemma(sys, new (string,string)[0], "what is a neural network?");
            Log("DECODE single-turn long prompt");
            int n = await Stream(() => executor.InferAsync(prompt, ip), maxTokens);
            Log($"DONE single-turn, tokens={n}");
            Console.WriteLine($"\n[single-turn OK, {n} tokens]");
        }
        else
        {
            // Turn 1 short, turn 2 long — mimics the app's reproduction.
            Log("DECODE turn1");
            string p1 = BuildGemma(sys, new (string,string)[0], "hello");
            int n1 = await Stream(() => executor.InferAsync(p1, new InferenceParams { MaxTokens = 40, AntiPrompts = ip.AntiPrompts, SamplingPipeline = ip.SamplingPipeline }), 40);
            string a1 = lastResponse;
            Log($"turn1 done tokens={n1} reply='{a1.Replace("\n"," ").Substring(0, Math.Min(60, a1.Length))}'");

            Log("DECODE turn2 (with history) — same context, fresh InferAsync");
            string p2 = BuildGemma(sys, new[] { ("user","hello"), ("model", a1) }, "what is a neural network?");
            int n2 = await Stream(() => executor.InferAsync(p2, ip), maxTokens);
            Log($"turn2 done tokens={n2}");
            Console.WriteLine($"\n[multi-turn OK, t1={n1} t2={n2} tokens]");
        }

        Log("EXIT clean");
        return 0;
    }

    static string lastResponse = "";
    static async Task<int> Stream(Func<IAsyncEnumerable<string>> f, int cap)
    {
        int n = 0; var sb = new System.Text.StringBuilder();
        await foreach (var t in f())
        {
            sb.Append(t); Console.Write(t); n++;
            if (n >= cap + 8) break;
        }
        lastResponse = sb.ToString();
        return n;
    }

    static string BuildGemma(string sys, (string role, string content)[] turns, string user)
    {
        var sb = new System.Text.StringBuilder();
        // gemma has no system role; fold system into the first user turn (as llama.cpp's template does).
        sb.Append("<start_of_turn>user\n").Append(sys).Append("\n\n");
        if (turns.Length == 0)
        {
            sb.Append(user).Append("<end_of_turn>\n<start_of_turn>model\n");
            return sb.ToString();
        }
        // first turn carries system
        sb.Append(turns[0].content).Append("<end_of_turn>\n");
        for (int i = 1; i < turns.Length; i++)
            sb.Append("<start_of_turn>").Append(turns[i].role).Append('\n').Append(turns[i].content).Append("<end_of_turn>\n");
        sb.Append("<start_of_turn>user\n").Append(user).Append("<end_of_turn>\n<start_of_turn>model\n");
        return sb.ToString();
    }

    static void Log(string m) { try { lock (LogGate) { using var s = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite); var b = System.Text.Encoding.UTF8.GetBytes($"\n>>> [{DateTime.Now:HH:mm:ss.fff}] {m}\n"); s.Write(b,0,b.Length); s.Flush(true);} } catch {} }

    static void Prepend(string var, string dir) { var cur = Environment.GetEnvironmentVariable(var) ?? ""; if (!cur.Contains(dir)) Environment.SetEnvironmentVariable(var, dir + Path.PathSeparator + cur); }

    static string FindCudaBin()
    {
        string env = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "bin", "cudart64_12.dll"))) return Path.Combine(env, "bin");
        string root = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
        if (Directory.Exists(root))
            foreach (var d in Directory.GetDirectories(root).OrderByDescending(x => x))
                if (File.Exists(Path.Combine(d, "bin", "cudart64_12.dll"))) return Path.Combine(d, "bin");
        return null;
    }
}
