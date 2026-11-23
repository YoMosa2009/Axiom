using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AxiomSubscriptionGateway;
using Xunit;

namespace Malx_AI.Tests;

public sealed class DiagnosticsReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScrubRemovesKeysPathsEmailsUrlsAndQuotedText()
    {
        string scrubbed = DiagnosticsReport.Scrub(
            "Failed for jane.doe@example.com reading C:\\Users\\jane\\Secret Plans\\deck.html with key sk-or-v1-abcdefghijklmnopqrstuvwxyz0123 "
            + "at https://api.example.com/v1/run?token=abc123 near 'my private prompt text goes right here'");

        Assert.DoesNotContain("jane", scrubbed);
        Assert.DoesNotContain("Secret Plans", scrubbed);
        Assert.DoesNotContain("sk-or-v1", scrubbed);
        Assert.DoesNotContain("token=abc123", scrubbed);
        Assert.DoesNotContain("private prompt", scrubbed);
        Assert.Contains("<email>", scrubbed);
        Assert.Contains("<path>", scrubbed);
        Assert.Contains("https://api.example.com/", scrubbed);
        Assert.Contains("<text>", scrubbed);
    }

    [Fact]
    public void ScrubCapsLength()
    {
        Assert.True(DiagnosticsReport.Scrub(new string('x', 2000)).Length <= DiagnosticsReport.MaxMessageChars + 1);
    }

    [Fact]
    public void ExceptionEventKeepsTypeAreaAndMethodNamesOnly()
    {
        Exception caught;
        try { ThrowForTest(); throw new InvalidOperationException("unreachable"); }
        catch (Exception ex) { caught = ex; }

        var e = DiagnosticsReport.FromException("Workplace.Agent", caught, crash: false, Now);

        Assert.Equal("error", e.Kind);
        Assert.Equal("Workplace.Agent", e.Area);
        Assert.Equal(typeof(InvalidOperationException).FullName, e.ExceptionType);
        Assert.Contains(nameof(ThrowForTest), e.Stack);
        Assert.DoesNotContain(".cs", e.Stack);
        Assert.DoesNotContain(":line", e.Stack);
        Assert.Equal("inner: System.IO.IOException", e.Detail);
        Assert.Equal(16, e.Fingerprint.Length);
    }

    [Fact]
    public void RepeatsRaiseACountInsteadOfAddingEvents()
    {
        var pending = new List<DiagnosticsEvent>();
        var e = DiagnosticsReport.Feature("Chat.Send", "cloud", 1, Now);
        DiagnosticsReport.Merge(pending, e);
        DiagnosticsReport.Merge(pending, e);
        DiagnosticsReport.Merge(pending, DiagnosticsReport.Feature("Chat.Send", "local", 1, Now));

        Assert.Equal(2, pending.Count);
        Assert.Equal(2, pending[0].Occurrences);
    }

    [Fact]
    public void AFullQueueKeepsErrorsOverUsageCounts()
    {
        var pending = new List<DiagnosticsEvent>();
        for (int i = 0; i < DiagnosticsReport.MaxPendingEvents; i++)
            DiagnosticsReport.Merge(pending, DiagnosticsReport.Feature("Area" + i, "x", 1, Now));

        DiagnosticsReport.Merge(pending, DiagnosticsReport.FromException("Chat", new TimeoutException("slow"), false, Now));
        DiagnosticsReport.Merge(pending, DiagnosticsReport.Feature("Extra", "x", 1, Now));

        Assert.Equal(DiagnosticsReport.MaxPendingEvents, pending.Count);
        Assert.Contains(pending, p => p.Kind == "error");
        Assert.DoesNotContain(pending, p => p.Area == "Extra");
    }

    [Fact]
    public void PayloadRequiresARandomInstallId()
    {
        Assert.Throws<ArgumentException>(() => DiagnosticsReport.BuildPayload("not-an-id", "1.11.0", "Windows", []));
        string id = DiagnosticsReport.NewInstallId();
        Assert.True(DiagnosticsReport.IsValidInstallId(id));
        using var doc = JsonDocument.Parse(DiagnosticsReport.BuildPayload(id, "1.11.0", "Microsoft Windows 10.0.26300",
            [DiagnosticsReport.Session("start", Now)]));
        Assert.Equal(id, doc.RootElement.GetProperty("installId").GetString());
        Assert.Equal("session", doc.RootElement.GetProperty("events")[0].GetProperty("kind").GetString());
    }

    private static void ThrowForTest()
        => throw new InvalidOperationException("outer", new System.IO.IOException("inner"));
}

public sealed class DiagnosticsStoreTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"axiom-diag-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_database + suffix)) File.Delete(_database + suffix);
    }

    private static DiagnosticsBatchInput Batch(string installId, params DiagnosticsEventInput[] events)
        => new(installId, "1.11.0", "Microsoft Windows 10.0.26300", events.ToList());

    private static DiagnosticsEventInput Error(string area, string fp, int n = 1)
        => new("error", area, "System.TimeoutException", "slow", "A.B", fp, n, "", null);

    [Fact]
    public void InvalidBatchesAreRejectedWhole()
    {
        var store = new DiagnosticsStore(_database);
        string id = Guid.NewGuid().ToString("N");
        Assert.Null(store.Accept(Batch("bad id", Error("Chat", "f1"))));
        Assert.Null(store.Accept(Batch(id)));
        Assert.Null(store.Accept(Batch(id, new DiagnosticsEventInput("delete", "Chat", null, null, null, null, 1, null, null))));
        Assert.Null(store.Accept(Batch(id, Error("Chat'; DROP TABLE x;--", "f1"))));
        Assert.Null(store.Accept(Batch(id, Enumerable.Range(0, DiagnosticsStore.MaxEventsPerBatch + 1).Select(i => Error("Chat", "f" + i)).ToArray())));
    }

    [Fact]
    public void SummaryGroupsByFingerprintAndCountsInstalls()
    {
        var store = new DiagnosticsStore(_database);
        string a = Guid.NewGuid().ToString("N"), b = Guid.NewGuid().ToString("N");
        Assert.Equal(1, store.Accept(Batch(a, Error("Chat", "same", 3))));
        Assert.Equal(1, store.Accept(Batch(b, Error("Chat", "same", 2))));

        var row = Assert.Single(store.Summarize(7));
        Assert.Equal(5, row.Occurrences);
        Assert.Equal(2, row.Installs);
        Assert.Equal(2, Assert.Single(store.ActiveInstalls(7)).Installs);
    }

    [Fact]
    public void OneInstallCannotFloodTheTable()
    {
        var store = new DiagnosticsStore(_database);
        string id = Guid.NewGuid().ToString("N");
        int total = 0;
        for (int i = 0; i < 8; i++)
            total += store.Accept(Batch(id, Enumerable.Range(0, 50).Select(j => Error("Chat", $"{i}-{j}")).ToArray())) ?? 0;
        Assert.Equal(DiagnosticsStore.MaxRowsPerInstallPerDay, total);
    }
}

public sealed partial class AxiomAccountGatewayTests
{
    [Fact]
    public async Task DiagnosticsEndpointAcceptsAnonymousBatchesWithoutSignIn()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var stripeHttp = new HttpClient(new StripeFixture());
        using var providerHttp = new HttpClient(new ProviderFixture());
        await using var gateway = AccountGateway.Create(
            new AccountGatewayOptions(_database, "price_fixture", Signer, null),
            new MailFixture(), new StripeTestCheckout("sk_test_fixture", stripeHttp),
            new OpenRouterInference("openrouter-fixture", providerHttp), port: 0);
        await gateway.StartAsync(timeout.Token);
        using var http = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };

        string payload = DiagnosticsReport.BuildPayload(DiagnosticsReport.NewInstallId(), "1.11.0", "Microsoft Windows 10.0.26300",
            [DiagnosticsReport.FromException("Chat.Send", new TimeoutException("slow"), crash: false, DateTimeOffset.UtcNow),
             DiagnosticsReport.Feature("Chat.Send", "local", 4, DateTimeOffset.UtcNow)]);
        using (var ok = await http.PostAsync("v1/diagnostics", new StringContent(payload, Encoding.UTF8, "application/json"), timeout.Token))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(2, (await ok.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("stored").GetInt32());
        }
        using (var bad = await http.PostAsync("v1/diagnostics", new StringContent("{\"installId\":\"x\"}", Encoding.UTF8, "application/json"), timeout.Token))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var junk = await http.PostAsync("v1/diagnostics", new StringContent("not json", Encoding.UTF8, "application/json"), timeout.Token))
            Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);

        var rows = new DiagnosticsStore(_database).Summarize(1);
        Assert.Contains(rows, r => r.Kind == "error" && r.ExceptionType == "System.TimeoutException");
        Assert.Contains(rows, r => r.Kind == "feature" && r.Occurrences == 4);
        await gateway.StopAsync(timeout.Token);
    }
}
