using System.Net;
using System.Net.Mail;

namespace CinemaApp.Services;

public interface IEmailService
{
    // true = đã gửi email thật, false = chưa cấu hình hoặc gửi thất bại
    Task<bool> SendAsync(string to, string subject, string htmlBody);
}

public class EmailService : IEmailService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<EmailService> _log;

    public EmailService(IConfiguration cfg, ILogger<EmailService> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public async Task<bool> SendAsync(string to, string subject, string htmlBody)
    {
        var host = _cfg["Smtp:Host"];
        if (string.IsNullOrEmpty(host))
        {
            _log.LogInformation("Chưa cấu hình Smtp, bỏ qua việc gửi email tới {To}", to);
            return false;
        }
        try
        {
            using var client = new SmtpClient(host, int.Parse(_cfg["Smtp:Port"] ?? "587"))
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(_cfg["Smtp:User"], _cfg["Smtp:Password"])
            };
            var from = _cfg["Smtp:From"] ?? _cfg["Smtp:User"]!;
            using var msg = new MailMessage(from, to, subject, htmlBody) { IsBodyHtml = true };
            await client.SendMailAsync(msg);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Gửi email thất bại");
            return false;
        }
    }
}