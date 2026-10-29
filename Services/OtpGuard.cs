using System.Collections.Concurrent;

namespace CinemaApp.Services;

// Đếm số lần nhập sai mã xác minh (OTP) theo từng email, TÁCH khỏi bộ đếm khóa đăng nhập của Identity.
// Lý do: nếu dùng chung, ai cũng có thể cố tình nhập sai mã cho email của người khác để khóa đăng nhập của họ (kể cả admin).
// Đếm theo chuỗi email người ta nhập, không phụ thuộc email có tồn tại hay không, nên cũng không lộ email nào đã đăng ký.
public static class OtpGuard
{
    private const int MaxFails = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (int Fails, DateTime Until)> Map = new();

    private static string Norm(string? key) => (key ?? "").Trim().ToLowerInvariant();

    // Đã sai đủ 5 lần trong 10 phút thì bị chặn nhập mã đến hết 10 phút kể từ lần sai đầu
    public static bool IsBlocked(string? key)
    {
        var k = Norm(key);
        if (!Map.TryGetValue(k, out var e)) return false;
        if (e.Until <= DateTime.UtcNow)
        {
            Map.TryRemove(k, out _);
            return false;
        }
        return e.Fails >= MaxFails;
    }

    public static void Fail(string? key)
    {
        var k = Norm(key);
        var now = DateTime.UtcNow;
        Map.AddOrUpdate(k,
            _ => (1, now + Window),
            (_, e) => e.Until <= now ? (1, now + Window) : (e.Fails + 1, e.Until));
        if (Map.Count > 20000) Sweep(now);
    }

    public static void Clear(string? key) => Map.TryRemove(Norm(key), out _);

    private static void Sweep(DateTime now)
    {
        foreach (var kv in Map)
            if (kv.Value.Until <= now) Map.TryRemove(kv.Key, out _);
    }
}

// Giới hạn tần suất gửi email/mã: mỗi khóa (email) cách nhau tối thiểu 60 giây và tối đa 5 lần mỗi giờ.
// Chống spam hộp thư người khác và chống làm cạn hạn mức gửi mail (mail vé dùng chung đường gửi).
public static class MailThrottle
{
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private const int MaxPerHour = 5;
    private static readonly ConcurrentDictionary<string, List<DateTime>> Map = new();

    // true = được gửi (và ghi nhận lần gửi này); false = phải chờ
    public static bool TryAcquire(string? key)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;
        var list = Map.GetOrAdd(k, _ => new List<DateTime>());
        lock (list)
        {
            list.RemoveAll(t => now - t > Hour);
            if (list.Count > 0 && now - list[^1] < MinGap) return false;
            if (list.Count >= MaxPerHour) return false;
            list.Add(now);
        }
        if (Map.Count > 20000) Sweep(now);
        return true;
    }

    private static void Sweep(DateTime now)
    {
        foreach (var kv in Map)
        {
            lock (kv.Value)
            {
                if (kv.Value.Count == 0 || now - kv.Value[^1] > Hour) Map.TryRemove(kv.Key, out _);
            }
        }
    }
}