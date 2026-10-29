using System.Text.Json;
using CinemaApp.Data;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Services;

// Ghế của một đơn đã hết hạn, ghi nhớ để admin có thể duyệt lại nếu khách đã chuyển tiền
public record SeatSnap(int SeatId, string TicketType, decimal Price);

// Chạy ngầm: mỗi 30 giây nhả các ghế giữ quá hạn và đánh dấu đơn hết hạn
public class HoldCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HoldCleanupService> _log;
    private readonly IConfiguration _cfg;

    public HoldCleanupService(IServiceScopeFactory scopes, ILogger<HoldCleanupService> log, IConfiguration cfg)
    {
        _scopes = scopes;
        _log = log;
        _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;

                // 1. Đơn đang chờ tiền vừa quá hạn: chép danh sách ghế trước khi ghế bị xóa
                var late = await db.Bookings
                    .Where(b => b.Status == "WaitingPayment" && b.ExpiresAt <= now && b.ExpiredSeats == null)
                    .ToListAsync(ct);
                foreach (var b in late)
                {
                    var seats = await db.ShowtimeSeats.AsNoTracking()
                        .Where(x => x.BookingId == b.Id)
                        .Select(x => new SeatSnap(x.SeatId, x.TicketType, x.Price))
                        .ToListAsync(ct);
                    b.ExpiredSeats = JsonSerializer.Serialize(seats);
                }

                // Đơn TIỀN MẶT hết hạn mà khách không đến trả = một lần bỏ vé (no-show).
                // Đủ số lần quy định thì khóa lựa chọn tiền mặt một thời gian (chuyển khoản vẫn dùng được).
                int limit = _cfg.GetValue("Booking:CashNoShowLimit", 3);
                int blockDays = _cfg.GetValue("Booking:CashBlockDays", 30);
                if (limit > 0)
                {
                    foreach (var g in late.Where(b => b.PaymentMethod == "Cash").GroupBy(b => b.UserId))
                    {
                        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == g.Key, ct);
                        if (user == null) continue;
                        user.CashNoShowCount += g.Count();
                        if (user.CashNoShowCount >= limit)
                        {
                            user.CashBlockedUntil = now.AddDays(blockDays);
                            user.CashNoShowCount = 0;
                        }
                    }
                }
                if (late.Count > 0) await db.SaveChangesAsync(ct);

                // 2. Nhả ghế quá hạn
                await db.ShowtimeSeats
                    .Where(x => x.Status == "Held" && x.HoldExpiresAt <= now)
                    .ExecuteDeleteAsync(ct);

                // 3. Đổi đơn quá hạn sang Expired
                await db.Bookings
                    .Where(b => (b.Status == "Pending" || b.Status == "WaitingPayment") && b.ExpiresAt <= now)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Expired"), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Dọn ghế hết hạn thất bại");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { }
        }
    }
}