using System.Net;
using System.Net.Mail;
using Inmobiscrap.Infrastructure;

namespace Inmobiscrap.Services;

public interface IEmailService
{
    /// <returns>true si el email se envió; false si SMTP no está configurado o falló.</returns>
    Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody);
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
        _host   = EnvConfig.Get("SMTP_HOST") ?? "";
        _port   = int.TryParse(EnvConfig.Get("SMTP_PORT"), out var p) ? p : 587;
        _user   = EnvConfig.Get("SMTP_USER") ?? "";
        _pass   = EnvConfig.Get("SMTP_PASS") ?? "";
        _from   = EnvConfig.Get("SMTP_FROM") ?? "notificaciones@inmobiscrap.com";
    }

    public async Task<bool> SendAsync(string toEmail, string toName, string subject, string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(_host) || string.IsNullOrWhiteSpace(_user))
        {
            _logger.LogWarning("EmailService: SMTP_HOST o SMTP_USER no configurados. Email a {Email} no enviado.", toEmail);
            return false;
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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando email a {Email}", toEmail);
            return false;
        }
    }
}
