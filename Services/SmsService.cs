namespace CinemaApp.Services;

public interface ISmsService
{
    // true = đã gửi SMS thật, false = chưa có nhà cung cấp SMS
    Task<bool> SendAsync(string phone, string message);
}

public class SmsService : ISmsService
{
    private readonly ILogger<SmsService> _log;

    public SmsService(ILogger<SmsService> log)
    {
        _log = log;
    }

    public Task<bool> SendAsync(string phone, string message)
    {
        // Chưa kết nối nhà cung cấp SMS. Khi có, gọi API của họ ở đây rồi return true.
        _log.LogInformation("Chưa cấu hình nhà cung cấp SMS, không gửi tin nhắn.");
        return Task.FromResult(false);
    }
}