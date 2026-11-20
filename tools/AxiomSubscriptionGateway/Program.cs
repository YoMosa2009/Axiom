using AxiomSubscriptionGateway;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

if (args is ["--run-subscription-sandbox"])
    return await SubscriptionSandbox.RunAsync();

if (args.Length > 0 && args[0] == "--backup-accounts")
{
    if (args is not ["--backup-accounts", var destination])
    {
        Console.Error.WriteLine("Usage: --backup-accounts <new-backup-path>");
        return 2;
    }

    try
    {
        AccountDatabaseBackup.Create(Required("AXIOM_ACCOUNTS_DB"), destination);
        Console.WriteLine("Account backup created and checked.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or SqliteException or InvalidOperationException
        or ArgumentException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Account backup failed: {ex.Message}");
        return 1;
    }
}

if (args is ["--provision-live", var publicOriginArg, ..])
{
    string? writeEnvPath = args switch
    {
        [_, _] => null,
        [_, _, "--write-env", var envFile] => envFile,
        _ => throw new ArgumentException("Usage: --provision-live <https-public-origin> [--write-env <path>]")
    };
    if (!Uri.TryCreate(publicOriginArg, UriKind.Absolute, out Uri? originUri))
        throw new InvalidOperationException("Pass a valid HTTPS origin URL to --provision-live.");

    var liveStripe = StripeTestCheckout.FromEnvironment(allowLiveMode: true)
        ?? throw new InvalidOperationException("Set AXIOM_STRIPE_SECRET_KEY to your Stripe live key.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var provisioned = await liveStripe.ProvisionLiveAsync(originUri, deadline.Token);

    if (!string.IsNullOrWhiteSpace(writeEnvPath))
    {
        string fullEnvPath = Path.GetFullPath(writeEnvPath);
        string? envDir = Path.GetDirectoryName(fullEnvPath);
        if (!string.IsNullOrWhiteSpace(envDir))
            Directory.CreateDirectory(envDir);

        var envLines = new[]
        {
            $"AXIOM_STRIPE_PRICE={provisioned.PriceId}",
            $"AXIOM_STRIPE_PORTAL_CONFIGURATION={provisioned.PortalConfigurationId}",
            $"AXIOM_STRIPE_WEBHOOK_SECRET={provisioned.WebhookSecret}",
            $"AXIOM_ACCOUNTS_PUBLIC_ORIGIN={originUri.GetLeftPart(UriPartial.Authority)}"
        };
        File.WriteAllLines(fullEnvPath, envLines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"Provisioned live Stripe Price ({provisioned.PriceId}), Portal ({provisioned.PortalConfigurationId}), and Webhook ({provisioned.WebhookEndpointId}).");
        Console.WriteLine($"Saved Stripe config values to {fullEnvPath}.");
    }
    else
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            priceId = provisioned.PriceId,
            portalConfigurationId = provisioned.PortalConfigurationId,
            webhookEndpointId = provisioned.WebhookEndpointId,
            webhookSecret = provisioned.WebhookSecret,
            publicOrigin = originUri.GetLeftPart(UriPartial.Authority)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    return 0;
}

if (args is ["--serve-accounts"] or ["--serve-sandbox-accounts"])
{
    bool allowLiveMode = args[0] == "--serve-accounts";
    string accountDatabase = Required("AXIOM_ACCOUNTS_DB");
    string plan = allowLiveMode
        ? (Environment.GetEnvironmentVariable("AXIOM_STRIPE_PRICE")
            ?? Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_PRICE")
            ?? throw new InvalidOperationException("Set AXIOM_STRIPE_PRICE (or AXIOM_STRIPE_TEST_PRICE)."))
        : Required("AXIOM_STRIPE_TEST_PRICE");
    var stripe = StripeTestCheckout.FromEnvironment(allowLiveMode)
        ?? throw new InvalidOperationException(allowLiveMode
            ? "Set AXIOM_STRIPE_SECRET_KEY (or AXIOM_STRIPE_TEST_SECRET_KEY)."
            : "Set AXIOM_STRIPE_TEST_SECRET_KEY.");
    var inference = OpenRouterInference.FromEnvironment()
        ?? throw new InvalidOperationException("Set AXIOM_GATEWAY_OPENROUTER_KEY or AXIOM_UPSTREAM_API_KEY.");
    var email = SmtpAxiomSignInEmail.FromEnvironment();
    var origin = Environment.GetEnvironmentVariable("AXIOM_ACCOUNTS_PUBLIC_ORIGIN");
    var webhook = allowLiveMode
        ? (Environment.GetEnvironmentVariable("AXIOM_STRIPE_WEBHOOK_SECRET")
            ?? Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_WEBHOOK_SECRET"))
        : Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_WEBHOOK_SECRET");
    var portal = allowLiveMode
        ? (Environment.GetEnvironmentVariable("AXIOM_STRIPE_PORTAL_CONFIGURATION")
            ?? Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_PORTAL_CONFIGURATION"))
        : Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_PORTAL_CONFIGURATION");
    int port = int.TryParse(Environment.GetEnvironmentVariable("AXIOM_PORT"), out int parsedPort) && parsedPort > 0
        ? parsedPort : 5198;
    long allowanceMicros = long.TryParse(Environment.GetEnvironmentVariable("AXIOM_MONTHLY_ALLOWANCE_MICROS"), out long parsedAllowance)
        && parsedAllowance > 0
        ? parsedAllowance : 5_250_000;
    int maxActiveSubscribers = int.TryParse(Environment.GetEnvironmentVariable("AXIOM_MAX_ACTIVE_SUBSCRIBERS"), out int parsedCap)
        && parsedCap > 0
        ? parsedCap : 0;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await stripe.VerifyMonthlyPriceAsync(plan, deadline.Token);
    await using var accounts = AccountGateway.Create(new AccountGatewayOptions(accountDatabase, plan, webhook, portal,
        origin is null ? null : new Uri(origin, UriKind.Absolute), allowanceMicros, maxActiveSubscribers), email, stripe, inference, port: port);
    await accounts.RunAsync();
    return 0;
}

if (args.Length > 0 && args[0] is "--probe-openrouter" or "--lookup-openrouter")
{
    bool lookupOnly = args[0] == "--lookup-openrouter";
    bool promptForKey = args.Length == 3 && args[2] == "--prompt-key";
    if ((!promptForKey && args.Length != 2)
        || (lookupOnly
            ? !args[1].StartsWith("gen-", StringComparison.Ordinal) || args[1].Length <= 4
            : !Enum.GetNames<ProbeKind>().Any(name => name.Equals(args[1], StringComparison.OrdinalIgnoreCase))))
    {
        Console.Error.WriteLine("Usage: --probe-openrouter text|tool|image [--prompt-key]");
        Console.Error.WriteLine("       --lookup-openrouter <generation-id> [--prompt-key]");
        return 2;
    }

    if (promptForKey && Console.IsInputRedirected)
    {
        Console.Error.WriteLine("The hidden key prompt requires an interactive terminal. No request was sent.");
        return 2;
    }

    var probeKey = promptForKey ? ReadProbeKey() : Environment.GetEnvironmentVariable("AXIOM_OPENROUTER_PROBE_KEY");
    if (string.IsNullOrWhiteSpace(probeKey))
    {
        Console.Error.WriteLine(promptForKey
            ? "No key was entered. No request was sent."
            : "Set AXIOM_OPENROUTER_PROBE_KEY or use --prompt-key with a dedicated, spend-limited OpenRouter key.");
        return 2;
    }

    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try
    {
        var probe = new OpenRouterRouteProbe(http, probeKey.Trim());
        object result = lookupOnly
            ? await probe.LookupAsync(args[1], timeout.Token)
            : await probe.RunAsync(Enum.Parse<ProbeKind>(args[1], ignoreCase: true), timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 0;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
    {
        Console.Error.WriteLine("The probe could not verify the response. Check OpenRouter activity before retrying.");
        return 1;
    }
}

var token = Environment.GetEnvironmentVariable("AXIOM_GATEWAY_TEST_TOKEN");
if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
    throw new InvalidOperationException("Set AXIOM_GATEWAY_TEST_TOKEN to a local test token of at least 16 characters.");

var databasePath = Environment.GetEnvironmentVariable("AXIOM_GATEWAY_TEST_DB");
if (string.IsNullOrWhiteSpace(databasePath))
    throw new InvalidOperationException("Set AXIOM_GATEWAY_TEST_DB to a path for the local test database.");

await using var app = LocalGateway.Create(token, databasePath, port: 5198,
    stripeWebhook: StripeTestWebhook.FromEnvironment(),
    stripeCheckout: StripeTestCheckout.FromEnvironment(),
    inference: OpenRouterInference.FromEnvironment());
await app.RunAsync();
return 0;

static string Required(string name)
    => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name}.");

static string ReadProbeKey()
{
    Console.Error.Write("Paste the dedicated OpenRouter key (hidden), then press Enter: ");
    var input = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            return input.ToString();
        }
        if (key.Key == ConsoleKey.Backspace && input.Length > 0)
            input.Length--;
        else if (!char.IsControl(key.KeyChar))
            input.Append(key.KeyChar);
    }
}
