using System.Net;
using System.Net.Mail;

namespace Inmobiscrap.Services;

public interface IEmailService
{
    Task SendAsync(string toEmail, string toName, string subject, string htmlBody);
}

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;

    private readonly string _host;
    private readonly int    _port;
    private readonly string _user;
    private readonly string _pass;
    private readonly string _from;

    public EmailService(ILogger<EmailService> logger)
    {
        _logger = logger;
        _host   = Environment.GetEnvironmentVariable("SMTP_HOST") ?? "";
        _port   = int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) ? p : 587;
        _user   = Environment.GetEnvironmentVariable("SMTP_USER") ?? "";
        _pass   = Environment.GetEnvironmentVariable("SMTP_PASS") ?? "";
        _from   = Environment.GetEnvironmentVariable("SMTP_FROM") ?? "notificaciones@inmobiscrap.com";
    }

    public async Task SendAsync(string toEmail, string toName, string subject, string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(_host) || string.IsNullOrWhiteSpace(_user))
        {
            _logger.LogWarning("EmailService: SMTP_HOST o SMTP_USER no configurados. Email a {Email} no enviado.", toEmail);
            return;
        }

        using var client = new SmtpClient(_host, _port)
        {
            Credentials  = new NetworkCredential(_user, _pass),
            EnableSsl    = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        using var message = new MailMessage
        {
            From       = new MailAddress(_from, "InmobiScrap"),
            Subject    = subject,
            Body       = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(new MailAddress(toEmail, toName));

        try
        {
            await client.SendMailAsync(message);
            _logger.LogInformation("Email enviado a {Email}: {Subject}", toEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando email a {Email}", toEmail);
        }
    }
}
