using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxiomSubscriptionGateway;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Malx_AI.Tests;

public sealed class AxiomAccountGatewayTests : IDisposable
{
    private const string Signer = "whsec_account_fixture";
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"axiom-accounts-{Guid.NewGuid():N}.sqlite");

    [Fact]
    public async Task RecoveredAccountKeepsItsPaidBalanceAndRenewsWithoutAffectingAnotherAccount()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var mail = new MailFixture();
        var stripeFixture = new StripeFixture();
        var providerFixture = new ProviderFixture();
        var clock = new FixedClock { Now = DateTimeOffset.UtcNow };
        using var stripeHttp = new HttpClient(stripeFixture);
        using var providerHttp = new HttpClient(providerFixture);
        await using var gateway = AccountGateway.Create(
            new AccountGatewayOptions(_database, "price_fixture", Signer, null),
            mail, new StripeTestCheckout("sk_test_fixture", stripeHttp),
            new OpenRouterInference("openrouter-fixture", providerHttp), port: 0, clock: clock);
        await gateway.StartAsync(timeout.Token);
        using var http = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };
        var desktop = new AxiomAccountClient(AxiomAccountClient.ParseEndpoint(new Uri(http.BaseAddress!, "v1").AbsoluteUri)!);

        async Task<AxiomDesktopSession> SignInAsync(string address)
        {
            Assert.Equal(HttpStatusCode.Accepted, await desktop.RequestCodeAsync(address, timeout.Token));
            string code = mail.Codes[address];
            using var mismatched = await http.PostAsJsonAsync("v1/accounts/sign-in",
                new { email = "different@example.com", code }, timeout.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, mismatched.StatusCode);
            var verify = await desktop.SignInAsync(address, code, timeout.Token);
            Assert.NotNull(verify);
            using var replay = await http.PostAsJsonAsync("v1/accounts/sign-in", new { email = address, code }, timeout.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
            return verify;
        }

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string session, object? payload = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
            if (payload is not null)
                request.Content = JsonContent.Create(payload);
            return await http.SendAsync(request, timeout.Token);
        }

        async Task<AllowanceBalance> BalanceAsync(string token)
        {
            using var response = await SendAsync(HttpMethod.Get, "v1/subscription", token);
            response.EnsureSuccessStatusCode();
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return status.RootElement.GetProperty("balance")
                .Deserialize<AllowanceBalance>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }

        var alice = await SignInAsync("alice@example.com");
        var bob = await SignInAsync("bob@example.com");
        using (var firstCheckout = await SendAsync(HttpMethod.Post, "v1/subscription/checkout", alice.Token))
            firstCheckout.EnsureSuccessStatusCode();
        using (var secondCheckout = await SendAsync(HttpMethod.Post, "v1/subscription/checkout", bob.Token))
            secondCheckout.EnsureSuccessStatusCode();
        Assert.Equal(new[] { "cus_alice_fixture", "cus_bob_fixture" }, stripeFixture.CheckoutCustomers);

        var chat = new { model = LocalGateway.Model, messages = new[] { new { role = "user", content = "Hello" } } };
        using (var unpaid = await SendAsync(HttpMethod.Post, "v1/chat/completions", alice.Token, chat))
            Assert.Equal(HttpStatusCode.PaymentRequired, unpaid.StatusCode);
        Assert.Equal(0, providerFixture.Calls);

        long now = clock.Now.ToUnixTimeSeconds();
        long renewal = now + 2_592_000;
        string Invoice(string type, string id, string status, string reason, long start, long end) => JsonSerializer.Serialize(new
        {
            type, livemode = false,
            data = new { @object = new
            {
                id, livemode = false, status, customer = "cus_alice_fixture",
                billing_reason = reason,
                parent = new { subscription_details = new { subscription = "sub_alice" } },
                lines = new { data = new[] { new
                {
                    pricing = new { price_details = new { price = "price_fixture" } },
                    parent = new { subscription_item_details = new { proration = false } },
                    period = new { start, end }
                } } }
            } }
        });

        string firstInvoice = Invoice("invoice.paid", "in_alice_paid", "paid", "subscription_create", now - 1, renewal);

        async Task<HttpStatusCode> DeliverAsync(string signingKey, string payload)
        {
            string stamp = now.ToString(System.Globalization.CultureInfo.InvariantCulture);
            byte[] signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(stamp + "." + payload));
            using var webhook = new HttpRequestMessage(HttpMethod.Post, StripeTestWebhook.Path)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            webhook.Headers.Add("Stripe-Signature", $"t={stamp},v1={Convert.ToHexString(signature)}");
            using var response = await http.SendAsync(webhook, timeout.Token);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await DeliverAsync("wrong-secret", firstInvoice));
        Assert.Equal(0, (await BalanceAsync(alice.Token)).AllowanceMicros);
        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(Signer, firstInvoice));
        using (var paid = await SendAsync(HttpMethod.Post, "v1/chat/completions", alice.Token, chat))
            paid.EnsureSuccessStatusCode();
        using (var blocked = await SendAsync(HttpMethod.Post, "v1/chat/completions", bob.Token, chat))
            Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
        Assert.Equal(1, providerFixture.Calls);
        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(Signer, firstInvoice));

        var recovered = await SignInAsync("alice@example.com");
        Assert.NotEqual(alice.Token, recovered.Token);
        var reopened = new AxiomAccountStore(_database);
        Assert.Equal(reopened.Authenticate(alice.Token)?.Id, reopened.Authenticate(recovered.Token)?.Id);
        Assert.Equal("cus_alice_fixture", reopened.Authenticate(recovered.Token)?.StripeCustomerId);
        Assert.Equal(new AllowanceBalance(5_250_000, 8, 0), await BalanceAsync(recovered.Token));
        Assert.Equal(new AllowanceBalance(0, 0, 0), await BalanceAsync(bob.Token));
        using (var portal = await SendAsync(HttpMethod.Post, "v1/subscription/portal", bob.Token, new { customer = "cus_alice_fixture" }))
            portal.EnsureSuccessStatusCode();
        Assert.Equal("cus_bob_fixture", stripeFixture.LastPortalCustomer);

        var aliceTransport = new OpenRouterChatService();
        aliceTransport.SetCustomEndpoint(new Uri(http.BaseAddress!, "v1").AbsoluteUri, alice.Token, OpenRouterChatService.AxiomSubscriptionModel);
        var activeRead = await aliceTransport.ReadAxiomSubscriptionDetailedAsync();
        Assert.False(activeRead.Unauthorized);
        Assert.NotNull(activeRead.Status);

        await desktop.SignOutAsync(alice.Token, timeout.Token);
        Assert.Null(reopened.Authenticate(alice.Token));
        var revokedRead = await aliceTransport.ReadAxiomSubscriptionDetailedAsync();
        Assert.True(revokedRead.Unauthorized);
        Assert.Null(revokedRead.Status);
        Assert.Equal(5_250_000, (await BalanceAsync(recovered.Token)).AllowanceMicros);

        var recoveryKey = await desktop.IssueRecoveryKeyAsync(recovered.Token, timeout.Token);
        Assert.NotNull(recoveryKey);
        Assert.Equal(HttpStatusCode.Accepted, await desktop.RequestCodeAsync("bob@example.com", timeout.Token));
        Assert.Null(await desktop.RecoverAsync("bob@example.com", mail.Codes["bob@example.com"], recoveryKey, timeout.Token));
        Assert.NotNull(reopened.Authenticate(recovered.Token));

        const string replacementEmail = "alice-new@example.com";
        Assert.Equal(HttpStatusCode.Accepted, await desktop.RequestCodeAsync(replacementEmail, timeout.Token));
        string replacementCode = mail.Codes[replacementEmail];
        Assert.Null(await desktop.RecoverAsync(replacementEmail, new string('0', 32), recoveryKey, timeout.Token));
        var transferred = await desktop.RecoverAsync(replacementEmail, replacementCode, recoveryKey, timeout.Token);
        Assert.NotNull(transferred);
        Assert.Equal(replacementEmail, transferred.Email);
        Assert.Null(reopened.Authenticate(recovered.Token));
        Assert.Equal("cus_alice_fixture", reopened.Authenticate(transferred.Token)?.StripeCustomerId);
        Assert.Equal(new AllowanceBalance(5_250_000, 8, 0), await BalanceAsync(transferred.Token));
        Assert.Equal(new AllowanceBalance(0, 0, 0), await BalanceAsync(bob.Token));
        Assert.Equal(HttpStatusCode.Accepted, await desktop.RequestCodeAsync(replacementEmail, timeout.Token));
        Assert.Null(await desktop.RecoverAsync(replacementEmail, mail.Codes[replacementEmail], recoveryKey, timeout.Token));

        long end = renewal + 2_592_000;
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(renewal - 1);
        var beforeRenewal = await SignInAsync(replacementEmail);
        var bobRenewal = await SignInAsync("bob@example.com");
        string failedInvoice = Invoice("invoice.payment_failed", "in_alice_renewal", "open", "subscription_cycle", renewal, end);
        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(Signer, failedInvoice));
        Assert.Equal(new AllowanceBalance(5_250_000, 8, 0), await BalanceAsync(beforeRenewal.Token));
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(renewal);
        Assert.Equal(new AllowanceBalance(0, 0, 0), await BalanceAsync(beforeRenewal.Token));
        using (var blocked = await SendAsync(HttpMethod.Post, "v1/chat/completions", beforeRenewal.Token, chat))
            Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);

        string paidRenewal = Invoice("invoice.paid", "in_alice_renewal", "paid", "subscription_cycle", renewal, end);
        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(Signer, paidRenewal));
        Assert.Equal(new AllowanceBalance(5_250_000, 0, 0), await BalanceAsync(beforeRenewal.Token));
        using (var paid = await SendAsync(HttpMethod.Post, "v1/chat/completions", beforeRenewal.Token, chat))
            paid.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(Signer, paidRenewal));
        Assert.Equal(new AllowanceBalance(5_250_000, 8, 0), await BalanceAsync(beforeRenewal.Token));
        Assert.Equal(new AllowanceBalance(0, 0, 0), await BalanceAsync(bobRenewal.Token));
    }

    [Theory]
    [InlineData("https://staging.example.com/v1", true)]
    [InlineData("http://127.0.0.1:5198/v1", true)]
    [InlineData("http://staging.example.com/v1", false)]
    [InlineData("https://a:b@staging.example.com/v1", false)]
    public void DesktopRequiresTlsForRemoteAccountEndpoints(string address, bool accepted)
        => Assert.Equal(accepted, AxiomAccountClient.ParseEndpoint(address) is not null);

    [Fact]
    public void ExpiredCodeCannotCreateAnAccountAndExpiredSessionCannotBeUsed()
    {
        var clock = new FixedClock();
        var store = new AxiomAccountStore(_database, clock);
        const string email = "reader@example.com";
        string expired = store.IssueCode(email)!;
        Assert.Null(store.IssueCode(email));
        clock.Now = clock.Now.AddMinutes(16);
        Assert.Null(store.CompleteSignIn(email, expired));
        string fresh = store.IssueCode(email)!;
        var session = store.CompleteSignIn(email, fresh)!;
        Assert.NotNull(store.Authenticate(session.Token));
        clock.Now = clock.Now.AddDays(30);
        Assert.Null(store.Authenticate(session.Token));
    }

    [Fact]
    public void CreatingANewRecoveryKeyRequiresRecentSignInAndInvalidatesThePreviousKey()
    {
        var clock = new FixedClock();
        var store = new AxiomAccountStore(_database, clock);
        const string address = "account@example.com";
        var session = store.CompleteSignIn(address, store.IssueCode(address)!)!;
        string originalKey = store.IssueRecoveryKey(session.Token)!;
        Assert.StartsWith("axr_", originalKey);
        clock.Now = clock.Now.AddMinutes(16);
        Assert.Null(store.IssueRecoveryKey(session.Token));

        var freshSession = store.CompleteSignIn(address, store.IssueCode(address)!)!;
        string freshKey = store.IssueRecoveryKey(freshSession.Token)!;
        Assert.NotEqual(originalKey, freshKey);
        const string newEmail = "new-account@example.com";
        string code = store.IssueCode(newEmail)!;
        Assert.Null(store.RecoverAccount(newEmail, code, originalKey));
        var recovered = store.RecoverAccount(newEmail, code, freshKey);
        Assert.NotNull(recovered);
        Assert.Null(store.Authenticate(session.Token));
        Assert.Null(store.Authenticate(freshSession.Token));
    }

    [Fact]
    public void AccountBackupRestoresIdentityBillingAndRecoveryWithoutOldSessions()
    {
        var clock = new FixedClock();
        var sourceStore = new AxiomAccountStore(_database, clock);
        var sourceLedger = new AllowanceLedger(_database, 0, "account-gateway", clock);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _database, Pooling = false
        }.ToString());
        writer.Open();
        using var configure = writer.CreateCommand();
        configure.CommandText = "PRAGMA journal_mode = WAL;";
        Assert.Equal("wal", configure.ExecuteScalar());

        const string originalEmail = "paid@example.com";
        const string newEmail = "replacement@example.com";
        var oldSession = sourceStore.CompleteSignIn(originalEmail, sourceStore.IssueCode(originalEmail)!)!;
        var account = sourceStore.Authenticate(oldSession.Token)!;
        var paidLedger = sourceLedger.ForAccount(account.Id);
        long start = clock.Now.ToUnixTimeSeconds();
        Assert.Equal("cus_backup", sourceStore.BindStripeCustomer(account.Id, "cus_backup"));
        Assert.True(paidLedger.TryGrantPeriod("in_backup", start, start + 86_400, 5_000_000));
        Assert.True(paidLedger.TryReserve("settled", 100));
        Assert.True(paidLedger.TrySettle("settled", 23));
        Assert.True(paidLedger.TryReserve("pending", 200));
        paidLedger.RecordUpstreamGeneration("pending", "gen-pending");
        string key = sourceStore.IssueRecoveryKey(oldSession.Token)!;
        string oldCode = sourceStore.IssueCode(newEmail)!;

        string backup = _database + ".backup";
        try
        {
            AccountDatabaseBackup.Create(_database, backup);
            Assert.Throws<IOException>(() => AccountDatabaseBackup.Create(_database, backup));
            Assert.False(File.Exists(backup + "-wal"));
            Assert.NotNull(sourceStore.Authenticate(oldSession.Token));
            Assert.Null(sourceStore.IssueCode(newEmail));

            var restoredStore = new AxiomAccountStore(backup, clock);
            var restoredLedger = new AllowanceLedger(backup, 0, "account-gateway", clock).ForAccount(account.Id);
            Assert.Null(restoredStore.Authenticate(oldSession.Token));
            Assert.Null(restoredStore.RecoverAccount(newEmail, oldCode, key));
            var session = restoredStore.RecoverAccount(newEmail, restoredStore.IssueCode(newEmail)!, key)!;
            Assert.Equal(account with { Email = newEmail, StripeCustomerId = "cus_backup" },
                restoredStore.Authenticate(session.Token));
            Assert.Equal(new AllowanceBalance(5_000_000, 23, 200), restoredLedger.GetBalance());
            clock.Now = clock.Now.AddMinutes(2);
            Assert.Equal(new PendingGeneration("pending", "gen-pending", account.Id),
                Assert.Single(restoredLedger.PendingUpstreamRequests()));
            Assert.False(restoredLedger.TryGrantPeriod("in_backup", start, start + 86_400, 5_000_000));
            Assert.True(restoredLedger.TrySettle("pending", 11));
            Assert.Equal(new AllowanceBalance(5_000_000, 34, 0), restoredLedger.GetBalance());
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public void LegacyLedgerMigratesIdempotentlyAndEnforcesThreeHourAndWeeklyWindows()
    {
        var clock = new FixedClock { Now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero) };
        using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database, Pooling = false }.ToString()))
        {
            legacy.Open();
            using var cmd = legacy.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE allowance_period (
                    id TEXT PRIMARY KEY,
                    account TEXT NOT NULL,
                    starts INTEGER NOT NULL,
                    ends INTEGER NOT NULL CHECK (ends > starts),
                    allowance INTEGER NOT NULL CHECK (allowance >= 0),
                    spent INTEGER NOT NULL DEFAULT 0 CHECK (spent >= 0),
                    reserved INTEGER NOT NULL DEFAULT 0 CHECK (reserved >= 0),
                    CHECK (spent + reserved <= allowance)
                );
                CREATE INDEX period_account_start ON allowance_period (account, starts);
                CREATE TABLE allowance_request (
                    id TEXT PRIMARY KEY,
                    period_id TEXT NOT NULL REFERENCES allowance_period (id),
                    reserved INTEGER NOT NULL CHECK (reserved > 0),
                    actual INTEGER CHECK (actual >= 0 AND actual <= reserved)
                );
                CREATE TABLE upstream_generation (
                    request_id TEXT PRIMARY KEY REFERENCES allowance_request (id),
                    generation_id TEXT NOT NULL UNIQUE,
                    observed_at INTEGER NOT NULL
                );
                INSERT INTO allowance_period (id, account, starts, ends, allowance, spent, reserved)
                VALUES ('in_existing', 'cus_existing', 1000, 9999999999, 5000000, 580, 0);
                INSERT INTO allowance_request (id, period_id, reserved, actual)
                VALUES ('req_existing', 'in_existing', 10000, 580);
                """;
            cmd.ExecuteNonQuery();
        }

        var migrated = new AllowanceLedger(_database, 0, "cus_pacing", clock,
            sessionLimitMicros: 350_000, weeklyLimitMicros: 1_250_000);
        var idempotent = new AllowanceLedger(_database, 0, "cus_pacing", clock,
            sessionLimitMicros: 350_000, weeklyLimitMicros: 1_250_000);

        using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database, Pooling = false }.ToString()))
        {
            verify.Open();
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var colCmd = verify.CreateCommand())
            {
                colCmd.CommandText = "PRAGMA table_info(allowance_request);";
                using var reader = colCmd.ExecuteReader();
                while (reader.Read())
                    cols.Add(reader.GetString(1));
            }
            Assert.Contains("created_at", cols);

            using var tblCmd = verify.CreateCommand();
            tblCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'usage_session_window';";
            Assert.Equal("usage_session_window", tblCmd.ExecuteScalar());
        }

        Assert.Equal(new AllowanceBalance(5_000_000, 580, 0),
            idempotent.ForAccount("cus_existing").GetBalance());

        DateTimeOffset firstAnchor = clock.Now;
        Assert.True(idempotent.TryGrantPeriod("in_pacing", firstAnchor.ToUnixTimeSeconds(), firstAnchor.AddDays(30).ToUnixTimeSeconds(), 5_000_000));
        AllowanceUsageSnapshot initial = idempotent.GetUsage();
        Assert.False(initial.Session.Active);
        Assert.Null(initial.Session.ResetsAt);
        Assert.Equal(0, initial.Session.UsedMicros);
        Assert.Equal(0, initial.Week.UsedMicros);

        Assert.Equal(ReserveStatus.Reserved, idempotent.TryReserveDetailed("s1-a", 200_000).Status);
        Assert.True(idempotent.TrySettle("s1-a", 200_000));
        AllowanceUsageSnapshot afterFirst = idempotent.GetUsage();
        Assert.True(afterFirst.Session.Active);
        Assert.Equal(firstAnchor.AddHours(3), afterFirst.Session.ResetsAt);
        Assert.Equal(200_000, afterFirst.Session.UsedMicros);

        clock.Now = firstAnchor.AddHours(1);
        Assert.Equal(ReserveStatus.Reserved, idempotent.TryReserveDetailed("s1-b", 150_000).Status);
        Assert.True(idempotent.TrySettle("s1-b", 150_000));
        Assert.Equal(firstAnchor.AddHours(3), idempotent.GetUsage().Session.ResetsAt);
        Assert.Equal(ReserveStatus.SessionLimitReached, idempotent.TryReserveDetailed("s1-over", 1_000).Status);

        clock.Now = firstAnchor.AddHours(3).AddSeconds(1);
        AllowanceUsageSnapshot afterSessionReset = idempotent.GetUsage();
        Assert.False(afterSessionReset.Session.Active);
        Assert.Equal(0, afterSessionReset.Session.UsedMicros);
        Assert.Equal(350_000, afterSessionReset.Week.UsedMicros);

        for (int i = 0; i < 3; i++)
        {
            clock.Now = clock.Now.AddHours(4);
            string reqId = $"w-{i}";
            Assert.Equal(ReserveStatus.Reserved, idempotent.TryReserveDetailed(reqId, 300_000).Status);
            Assert.True(idempotent.TrySettle(reqId, 300_000));
        }

        Assert.Equal(1_250_000, idempotent.GetUsage().Week.UsedMicros);
        Assert.Equal(ReserveStatus.WeeklyLimitReached, idempotent.TryReserveDetailed("w-over", 1_000).Status);

        clock.Now = firstAnchor.AddDays(7).AddSeconds(1);
        AllowanceUsageSnapshot afterWeekReset = idempotent.GetUsage();
        Assert.Equal(0, afterWeekReset.Week.UsedMicros);
        Assert.Equal(0d, afterWeekReset.Week.UsedPercent);
        Assert.Equal(ReserveStatus.Reserved, idempotent.TryReserveDetailed("w-new", 10_000).Status);
        Assert.True(idempotent.TrySettle("w-new", 10_000));
        Assert.Equal(new AllowanceBalance(5_000_000, 1_260_000, 0), migrated.GetBalance());
    }

    [Fact]
    public async Task AccountGatewaySubscriptionReportsModelLabelAndUsageThroughDesktopClient()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var mail = new MailFixture();
        var stripeFixture = new StripeFixture();
        var providerFixture = new ProviderFixture();
        var clock = new FixedClock { Now = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero) };
        using var stripeHttp = new HttpClient(stripeFixture);
        using var providerHttp = new HttpClient(providerFixture);
        await using var gateway = AccountGateway.Create(
            new AccountGatewayOptions(_database, "price_fixture", Signer, null),
            mail, new StripeTestCheckout("sk_test_fixture", stripeHttp),
            new OpenRouterInference("openrouter-fixture", providerHttp), port: 0, clock: clock);
        await gateway.StartAsync(timeout.Token);

        string v1Url = new Uri(new Uri(gateway.Urls.Single()), "v1").AbsoluteUri;
        var accountClient = new AxiomAccountClient(AxiomAccountClient.ParseEndpoint(v1Url)!);
        Assert.Equal(HttpStatusCode.Accepted, await accountClient.RequestCodeAsync("usage@example.com", timeout.Token));
        AxiomDesktopSession session = (await accountClient.SignInAsync("usage@example.com", mail.Codes["usage@example.com"], timeout.Token))!;

        var store = new AxiomAccountStore(_database, clock);
        var account = store.Authenticate(session.Token)!;
        store.BindStripeCustomer(account.Id, "cus_usage_test");
        var ledger = new AllowanceLedger(_database, 0, "account-gateway", clock,
            AccountGateway.SessionAllowanceMicros, AccountGateway.WeeklyAllowanceMicros).ForAccount(account.Id);
        ledger.TryGrantPeriod("in_usage_test", clock.Now.ToUnixTimeSeconds(), clock.Now.AddDays(30).ToUnixTimeSeconds(), 5_000_000);

        var desktop = new OpenRouterChatService();
        desktop.SetManagedEndpoint(v1Url, session.Token);
        var initial = await desktop.ReadAxiomSubscriptionAsync();
        Assert.NotNull(initial);
        Assert.Equal(OpenRouterChatService.AxiomManagedModelLabel, initial.ModelLabel);
        Assert.NotNull(initial.Usage);
        Assert.False(initial.Usage!.Session.Active);
        Assert.Equal(AccountGateway.SessionAllowanceMicros, initial.Usage.Session.LimitMicros);
        Assert.Equal(AccountGateway.WeeklyAllowanceMicros, initial.Usage.Week.LimitMicros);

        using var http = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        req.Content = JsonContent.Create(new { model = LocalGateway.Model, messages = new[] { new { role = "user", content = "Hi" } } });
        using var chatRes = await http.SendAsync(req, timeout.Token);
        chatRes.EnsureSuccessStatusCode();

        var afterChat = await desktop.ReadAxiomSubscriptionAsync();
        Assert.NotNull(afterChat?.Usage);
        Assert.True(afterChat!.Usage!.Session.Active);
        Assert.Equal(clock.Now.AddHours(3), afterChat.Usage.Session.ResetsAt);
        Assert.Equal(8, afterChat.Usage.Session.UsedMicros);
        Assert.Equal(8, afterChat.Usage.Week.UsedMicros);

        long remainingSession = AccountGateway.SessionAllowanceMicros - 8;
        Assert.Equal(ReserveStatus.Reserved, ledger.TryReserveDetailed("fill-session", remainingSession).Status);
        Assert.True(ledger.TrySettle("fill-session", remainingSession));

        using var throttledReq = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
        throttledReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        throttledReq.Content = JsonContent.Create(new { model = LocalGateway.Model, messages = new[] { new { role = "user", content = "Over limit" } } });
        using var throttledRes = await http.SendAsync(throttledReq, timeout.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttledRes.StatusCode);
        string throttledBody = await throttledRes.Content.ReadAsStringAsync(timeout.Token);
        Assert.Contains("axiom_session_limit_reached", throttledBody);

        var capped = await desktop.ReadAxiomSubscriptionAsync();
        Assert.NotNull(capped?.Usage);
        Assert.True(capped!.Usage!.Session.UsedPercent >= 100d);
    }

    public void Dispose() => File.Delete(_database);

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddDays(20_000);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MailFixture : IAxiomSignInEmail
    {
        public ConcurrentDictionary<string, string> Codes { get; } = new();
        public Task SendCodeAsync(string email, string code, CancellationToken cancellationToken)
        {
            Codes[email] = code;
            return Task.CompletedTask;
        }
    }

    private sealed class ProviderFixture : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id = $"gen-account-{Calls}",
                    choices = new[] { new { message = new { content = "Hello" } } },
                    usage = new { cost = 0.00000756m }
                })
            });
        }
    }

    private sealed class StripeFixture : HttpMessageHandler
    {
        public List<string> CheckoutCustomers { get; } = [];
        public string? LastPortalCustomer { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string raw = await request.Content!.ReadAsStringAsync(cancellationToken);
            var fields = raw.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(
                pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
            object result;
            switch (request.RequestUri?.AbsolutePath)
            {
                case "/v1/customers":
                    result = new { id = $"cus_{fields["email"].Split('@')[0]}_fixture", livemode = false };
                    break;
                case "/v1/checkout/sessions":
                    CheckoutCustomers.Add(fields["customer"]);
                    result = new { url = "https://checkout.stripe.com/c/pay/cs_test_fixture", livemode = false };
                    break;
                case "/v1/billing_portal/sessions":
                    LastPortalCustomer = fields["customer"];
                    result = new { url = "https://billing.stripe.com/p/session/test_fixture", livemode = false };
                    break;
                default:
                    throw new InvalidOperationException("Unexpected Stripe account test request.");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
}
