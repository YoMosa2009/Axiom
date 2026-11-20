using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging.Console;

namespace AxiomSubscriptionGateway;

internal sealed record AccountGatewayOptions(
    string DatabasePath, string StripePriceId, string? WebhookSecret, string? PortalConfigurationId,
    Uri? PublicOrigin = null, long MonthlyAllowanceMicros = 5_250_000);

internal static class AccountGateway
{
    private const long DefaultMonthlyAllowanceMicros = 5_250_000;
    internal const long SessionAllowanceMicros = 380_000;
    internal const long WeeklyAllowanceMicros = 1_350_000;
    private const string RatePolicy = "account-entry";
    private static readonly object RequestIdentity = new();

    public static WebApplication Create(AccountGatewayOptions options, IAxiomSignInEmail email,
        StripeTestCheckout stripe, OpenRouterInference inference, int port, TimeProvider? clock = null)
    {
        if (options.PublicOrigin is { } origin && (origin.Scheme != Uri.UriSchemeHttps
            || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment) || !string.IsNullOrEmpty(origin.UserInfo)))
            throw new ArgumentException("The public gateway origin must be an HTTPS origin without a path.");

        long monthlyAllowance = options.MonthlyAllowanceMicros > 0 ? options.MonthlyAllowanceMicros : DefaultMonthlyAllowanceMicros;
        var store = new AxiomAccountStore(options.DatabasePath, clock);
        var ledgerSchema = new AllowanceLedger(options.DatabasePath, monthlyAllowance, "account-gateway", clock,
            SessionAllowanceMicros, WeeklyAllowanceMicros);
        AllowanceLedger Ledger(string accountId) => ledgerSchema.ForAccount(accountId);
        StripeTestWebhook Billing(string customer) => new(customer, options.StripePriceId, options.WebhookSecret, options.PortalConfigurationId, stripe.IsLiveMode);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(config =>
        {
            config.Listen(IPAddress.Loopback, port);
            config.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
        });
        builder.Services.Configure<ConsoleLoggerOptions>(config => config.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddHostedService(services => new OpenRouterSettlementWorker(inference, ledgerSchema,
            services.GetRequiredService<ILogger<OpenRouterSettlementWorker>>(), clock, allAccounts: true));
        string? backupDir = Environment.GetEnvironmentVariable("AXIOM_BACKUP_DIR");
        if (!string.IsNullOrWhiteSpace(backupDir))
        {
            TimeSpan backupInterval = AccountDatabaseBackup.ParseIntervalFromEnvironment();
            builder.Services.AddHostedService(services => new AccountDatabaseBackupWorker(
                options.DatabasePath, backupDir, services.GetRequiredService<ILogger<AccountDatabaseBackupWorker>>(),
                clock, backupInterval));
        }
        builder.Services.AddRateLimiter(config =>
        {
            config.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            config.AddPolicy(RatePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
                }));
        });
        var app = builder.Build();
        app.UseRateLimiter();

        app.MapPost("/v1/accounts/request-code", async (BeginSignIn request, HttpContext context) =>
        {
            string? address = AxiomAccountStore.NormalizeEmail(request.Email);
            if (address is null)
                return LocalGateway.Error("Enter a valid email address.", "invalid_email", 400);

            if (store.IssueCode(address) is { } code)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await email.SendCodeAsync(address, code, deadline.Token);
                }
                catch (Exception ex) when (ex is System.Net.Mail.SmtpException or OperationCanceledException or InvalidOperationException)
                {
                    store.RevokeCode(address, code);
                    app.Logger.LogWarning("Axiom sign-in email delivery could not be completed.");
                    return LocalGateway.Error("Email delivery is temporarily unavailable. Try again shortly.", "email_unavailable", 503);
                }
            }

            return Results.Accepted();
        }).RequireRateLimiting(RatePolicy);

        app.MapPost("/v1/accounts/sign-in", (FinishSignIn request, HttpContext context) =>
        {
            string? address = AxiomAccountStore.NormalizeEmail(request.Email);
            AxiomAccountSession? session = address is null || request.Code is null
                ? null : store.CompleteSignIn(address, request.Code.Trim());
            if (session is null)
                return LocalGateway.Error("The sign-in code is invalid or has expired.", "invalid_code", 401);

            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(session);
        }).RequireRateLimiting(RatePolicy);

        app.MapPost("/v1/accounts/recover", (RecoverSignIn request, HttpContext context) =>
        {
            string? address = AxiomAccountStore.NormalizeEmail(request.Email);
            AxiomAccountSession? session = address is null || request.Code is null || request.RecoveryKey is null
                ? null : store.RecoverAccount(address, request.Code.Trim(), request.RecoveryKey.Trim());
            if (session is null)
                return LocalGateway.Error("Recovery could not be verified.", "invalid_recovery", 401);

            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(session);
        }).RequireRateLimiting(RatePolicy);

        var secure = app.MapGroup("/v1").AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext;
            if (!AuthenticationHeaderValue.TryParse(request.Request.Headers.Authorization, out var header)
                || !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                || header.Parameter is null
                || store.Authenticate(header.Parameter) is not { } account)
                return LocalGateway.Error("Sign in to your Axiom account.", "sign_in_required", 401);

            request.Response.Headers.CacheControl = "no-store";
            request.Items[RequestIdentity] = (account, header.Parameter);
            return await next(context);
        });

        secure.MapPost("/accounts/sign-out", (HttpContext context) =>
        {
            store.SignOut(Identity(context).Token);
            return Results.NoContent();
        });

        secure.MapPost("/accounts/recovery-key", (HttpContext context) =>
            store.IssueRecoveryKey(Identity(context).Token) is { } recoveryKey
                ? Results.Ok(new { recoveryKey })
                : LocalGateway.Error("Sign in again before creating a recovery key.", "recent_sign_in_required", 403))
            .RequireRateLimiting(RatePolicy);

        secure.MapGet("/subscription", (HttpContext context) =>
        {
            var account = Identity(context).Account;
            var ledger = Ledger(account.Id);
            var (balance, renewsAt) = ledger.GetPeriod();
            var usage = ledger.GetUsage();
            var billingState = ledger.GetBillingStatus();
            string billingStatus = billingState?.Status
                ?? (balance.AllowanceMicros > 0 ? "active" : "none");
            return Results.Ok(new
            {
                service = "axiom-managed-inference",
                sandbox = !stripe.IsLiveMode,
                monthlyUsd = 9,
                email = account.Email,
                modelLabel = "DeepSeek 4.1F-1017",
                billingStatus,
                balance,
                renewsAt,
                usage
            });
        });

        secure.MapPost("/subscription/checkout", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var account = Identity(context).Account;
            try
            {
                var ledger = Ledger(account.Id);
                if (ledger.GetBalance().AllowanceMicros > 0)
                    return Results.Conflict(new { error = "This account already has an active paid period." });

                if (store.TryGetActiveCheckoutUrl(account.Id) is { } existingUrl)
                    return Results.Ok(new { url = existingUrl });

                string customer = account.StripeCustomerId
                    ?? store.BindStripeCustomer(account.Id, await stripe.CreateCustomerAsync(account, cancellationToken));
                int priorPeriods = ledger.CountPeriods();
                string? suffix = priorPeriods > 0 ? $"cycle-{priorPeriods}" : null;
                var (result, sessionUrl) = await stripe.CreateSessionWithUrlAsync(
                    Billing(customer), ledger, context.Connection.LocalPort, cancellationToken, options.PublicOrigin, suffix);
                if (sessionUrl is not null)
                    store.SaveActiveCheckoutUrl(account.Id, sessionUrl.AbsoluteUri, TimeSpan.FromMinutes(25));
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return LocalGateway.Error("Stripe could not start Checkout.", "billing_unavailable", 502);
            }
        });

        secure.MapPost("/subscription/sync", (HttpContext context, CancellationToken cancellationToken) =>
        {
            var account = Identity(context).Account;
            var ledger = Ledger(account.Id);
            return account.StripeCustomerId is { } customer
                ? stripe.SynchronizeAsync(Billing(customer), ledger, monthlyAllowance, cancellationToken,
                    () => store.ClearActiveCheckout(account.Id))
                : Task.FromResult(Results.Ok(new { applied = 0, balance = ledger.GetBalance() }));
        });

        secure.MapPost("/subscription/portal", (HttpContext context, CancellationToken cancellationToken) =>
        {
            var account = Identity(context).Account;
            return account.StripeCustomerId is { } customer
                ? stripe.CreatePortalAsync(Billing(customer), context.Connection.LocalPort, cancellationToken, options.PublicOrigin)
                : Task.FromResult(LocalGateway.Error("Start Checkout before opening the billing portal.", "customer_pending", 409));
        });

        if (options.WebhookSecret is { } signingSecret)
        {
            async Task<IResult> HandleStripeWebhookAsync(HttpContext context)
            {
                return await StripeTestWebhook.ReadLifecycleEventAsync(context, signingSecret, stripe.IsLiveMode, async evt =>
                {
                    string? customer = evt.CustomerId;
                    if (customer is null && evt.EventType == "charge.dispute.created")
                        customer = await stripe.ResolveDisputeCustomerAsync(evt.DataObject, context.RequestAborted);
                    if (customer is null || store.FindStripeCustomer(customer) is not { } account)
                        return;

                    var ledger = Ledger(account.Id);
                    switch (evt.EventType)
                    {
                        case "invoice.paid":
                            if (Billing(customer).TryApplyPaidInvoice(evt.DataObject, ledger, monthlyAllowance))
                            {
                                ledger.SetBillingStatus("active");
                                store.ClearActiveCheckout(account.Id);
                            }
                            break;
                        case "invoice.payment_failed":
                            ledger.SetBillingStatus("payment_failed", "Latest subscription payment failed.");
                            break;
                        case "customer.subscription.deleted":
                            StripeTestWebhook.TryReadLong(evt.DataObject, "ended_at", out long endedAt);
                            ledger.EndActivePeriodAt(endedAt);
                            ledger.SetBillingStatus("canceled", "Subscription canceled.");
                            store.ClearActiveCheckout(account.Id);
                            break;
                        case "charge.refunded":
                        case "charge.dispute.created":
                            ledger.TryRevokeActivePeriod(evt.EventId, evt.EventType);
                            store.ClearActiveCheckout(account.Id);
                            break;
                    }
                });
            }

            app.MapPost(StripeTestWebhook.Path, (Func<HttpContext, Task<IResult>>)HandleStripeWebhookAsync);
            app.MapPost(StripeTestWebhook.LivePath, (Func<HttpContext, Task<IResult>>)HandleStripeWebhookAsync);
        }
        app.MapGet("/stripe/test/checkout/complete", () => Results.Text("Return to Axiom and refresh payment."));
        app.MapGet("/stripe/test/checkout/cancel", () => Results.Text("Test Checkout canceled."));
        app.MapGet("/stripe/test/portal/return", () => Results.Text("Return to Axiom to view your subscription."));
        app.MapGet("/stripe/checkout/complete", () => Results.Text("Payment submitted. Return to Axiom and click Refresh billing."));
        app.MapGet("/stripe/checkout/cancel", () => Results.Text("Checkout canceled. You can restart anytime in Axiom."));
        app.MapGet("/stripe/portal/return", () => Results.Text("Return to Axiom to view your subscription."));

        secure.MapGet("/models", () => LocalGateway.ModelCatalog(inference));
        secure.MapPost("/chat/completions", async (HttpContext context) =>
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                if (!LocalGateway.TryReadChatRequest(body.RootElement, out bool stream))
                    return LocalGateway.InvalidChatRequest();
                return await inference.CompleteAsync(context, body.RootElement, stream, Ledger(Identity(context).Account.Id));
            }
            catch (JsonException)
            {
                return LocalGateway.Error("Send a valid JSON chat request.", "invalid_request", 400);
            }
        });
        return app;
    }

    private static (AxiomAccount Account, string Token) Identity(HttpContext context)
        => ((AxiomAccount, string))context.Items[RequestIdentity]!;

    internal sealed record BeginSignIn(string? Email);
    internal sealed record FinishSignIn(string? Email, string? Code);
    internal sealed record RecoverSignIn(string? Email, string? Code, string? RecoveryKey);
}
