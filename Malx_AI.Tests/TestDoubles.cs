namespace Malx_AI
{
    internal sealed class LocalSemanticEmbeddingService
    {
        public static LocalSemanticEmbeddingService Shared { get; } = new();
        public bool IsAvailable => false;

        public bool TryGetSimilarity(string left, string right, out double similarity)
        {
            similarity = 0;
            return false;
        }

        public void PrewarmInBackground(IEnumerable<string> texts)
        {
        }
    }

    public static class AppDataPaths
    {
        public static string ChatHistory { get; } = Path.Combine(Path.GetTempPath(), "Axiom.Tests", "ChatHistory");
    }
}
