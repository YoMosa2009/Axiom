using System.Net;
using System.Net.Mail;

namespace AxiomSubscriptionGateway;

internal interface IAxiomSignInEmail
{
    Task SendCodeAsync(string email, string code, CancellationToken cancellationToken);
}

internal sealed class SmtpAxiomSignInEmail(
    string host, int port, string user, string password, string from) : IAxiomSignInEmail
{
    public static SmtpAxiomSignInEmail FromEnvironment()
    {
        string host = Required("AXIOM_SMTP_HOST");
        string user = Environment.GetEnvironmentVariable("AXIOM_SMTP_USER") is { Length: > 0 } smtpUser
            ? smtpUser
            : Environment.GetEnvironmentVariable("AXIOM_SMTP_USERNAME") is { Length: > 0 } smtpUsername
                ? smtpUsername
                : throw new InvalidOperationException("Set AXIOM_SMTP_USER for Axiom account email delivery.");
        string password = Required("AXIOM_SMTP_PASSWORD");
        string from = Required("AXIOM_SMTP_FROM");
        string? configuredPort = Environment.GetEnvironmentVariable("AXIOM_SMTP_PORT");
        int port = configuredPort is null ? 587 : int.Parse(configuredPort, System.Globalization.CultureInfo.InvariantCulture);
        return new SmtpAxiomSignInEmail(host, port, user, password, from);
    }

    public async Task SendCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        using var smtp = new SmtpClient(host, port) { EnableSsl = true, Credentials = new NetworkCredential(user, password) };
        using var message = new MailMessage(from, email, "Your Axiom sign-in code",
            $"Enter this code in Axiom to create or recover access to your account:\r\n\r\n{code}\r\n\r\n"
            + "The code expires in 15 minutes and can only be used once. Use it only if you requested it.");
        await smtp.SendMailAsync(message, cancellationToken);
    }

    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Set {name} for Axiom account email delivery.");
}
