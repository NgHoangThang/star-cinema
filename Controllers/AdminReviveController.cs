using System.Text.Json;
using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CinemaApp.Controllers;

// Duyệt lại đơn đã hết hạn trong vòng 2 giờ nếu khách đã chuyển tiền và các ghế vẫn còn trống
[Authorize]
public class AdminReviveController : Controller
{
    private const int GraceHours = 2;

    private readonly AppDbContext _db;
    private readonly ILogger<AdminReviveController> _log;

    public AdminReviveController(AppDbContext db, ILogger<AdminReviveController> log)
    {
        _db = db;
        _log = log;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!User.IsInRole(AdminSeeder.RoleName)) context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    [HttpPost("Admin/Revive/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Revive(int id, decimal? received, string? bankRef)
    {
        IActionResult Back(string msg)
        {
            TempData["AdminMsg"] = msg;
            return Redirect("/Admin/Orders?status=Expired");
        }

        var now = DateTime.UtcNow;
        var b = await _db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();

        if (b.Status == "Paid") return Back($"Đơn {b.Code} đã được duyệt trước đó.");
        if (b.Status != "Expired") return Back("Chỉ duyệt lại được đơn đã hết hạn.");

        if (string.IsNullOrEmpty(b.ExpiredSeats))
            return Back($"Đơn {b.Code} hết hạn trước khi hệ thống ghi nhớ ghế nên không khôi phục được. Nếu khách đã chuyển tiền, hãy hoàn tiền thủ công.");

        if (b.ExpiresAt < now.AddHours(-GraceHours))
            return Back($"Đơn {b.Code} đã quá hạn hơn {GraceHours} giờ. Nếu khách đã chuyển tiền, hãy hoàn tiền thủ công.");

        var start = await _db.Bookings.Where(x => x.Id == id).Select(x => x.Showtime.StartTime).FirstAsync();
        if (start <= now)
            return Back($"Suất chiếu của đơn {b.Code} đã bắt đầu. Nếu khách đã chuyển tiền, hãy hoàn tiền thủ công.");

        List<SeatSnap>? snaps;
        try { snaps = JsonSerializer.Deserialize<List<SeatSnap>>(b.ExpiredSeats); }
        catch (JsonException) { snaps = null; }
        if (snaps == null || snaps.Count == 0)
            return Back($"Đơn {b.Code} không có dữ liệu ghế để khôi phục. Hãy hoàn tiền thủ công.");

        var admin = User.Identity?.Name ?? "";
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1. Còn ghế nào đang có người giữ hoặc đã bán không
            var seatIds = snaps.Select(s => s.SeatId).ToList();
            bool taken = await _db.ShowtimeSeats.AnyAsync(x => x.ShowtimeId == b.ShowtimeId && seatIds.Contains(x.SeatId));
            if (taken)
            {
                await tx.RollbackAsync();
                return Back($"Ghế của đơn {b.Code} đã có người khác lấy. Cần hoàn tiền thủ công cho khách.");
            }

            // 2. Đặt lại đúng các ghế, trạng thái Đã bán (database từ chối nếu trùng ghế)
            foreach (var s in snaps)
            {
                _db.ShowtimeSeats.Add(new ShowtimeSeat
                {
                    ShowtimeId = b.ShowtimeId,
                    SeatId = s.SeatId,
                    Status = "Sold",
                    TicketType = s.TicketType,
                    UserId = b.UserId,
                    BookingId = id,
                    HoldExpiresAt = now,
                    Price = s.Price
                });
            }
            await _db.SaveChangesAsync();

            // 3. Tính lại tổng tiền từ giá ghế đã chép
            decimal seatsTotal = snaps.Sum(s => s.Price);
            decimal itemsTotal = await _db.BookingItems.Where(x => x.BookingId == id).SumAsync(x => x.UnitPrice * x.Quantity);
            decimal finalTotal = seatsTotal + itemsTotal - b.MemberDiscount - b.DiscountAmount;
                        if (finalTotal < 0) finalTotal = 0;

            // 3b. Đối soát: tiền thực nhận phải khớp số cần thu; chuyển khoản phải có mã giao dịch ngân hàng chưa dùng cho đơn khác
            bool isTransfer = b.PaymentMethod == "Transfer" || b.PaymentMethod == "QR";
            bankRef = (bankRef ?? "").Trim().ToUpperInvariant();
            if (received == null || received.Value != finalTotal)
            {
                await tx.RollbackAsync();
                string got = received == null ? "chưa nhập" : received.Value.ToString("N0") + " đ";
                return Back($"Số tiền thực nhận ({got}) không khớp số cần thu ({finalTotal:N0} đ). Không duyệt lại. Nếu khách chuyển thiếu/thừa, hãy liên hệ khách trước.");
            }
            if (isTransfer)
            {
                if (bankRef.Length < 4 || bankRef.Length > 40)
                {
                    await tx.RollbackAsync();
                    return Back("Nhập mã giao dịch ngân hàng (4 đến 40 ký tự) lấy từ sao kê.");
                }
                if (await _db.Bookings.AnyAsync(x => x.BankRef == bankRef && x.Id != id))
                {
                    await tx.RollbackAsync();
                    return Back("Mã giao dịch này đã được dùng cho đơn khác.");
                }
            }

            // 4. Chốt đơn
            int rows = await _db.Bookings
                .Where(x => x.Id == id && x.Status == "Expired")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "Paid")
                    .SetProperty(x => x.PaidAt, (DateTime?)now)
                    .SetProperty(x => x.ApprovedAt, (DateTime?)now)
                    .SetProperty(x => x.ApprovedByEmail, (string?)admin)
                    .SetProperty(x => x.ExpiredSeats, (string?)null)
                                        .SetProperty(x => x.Total, finalTotal)
                    .SetProperty(x => x.ReceivedAmount, (decimal?)received)
                    .SetProperty(x => x.BankRef, isTransfer ? bankRef : (string?)null));
            _db.AdminAuditLogs.Add(new AdminAuditLog { At = now, AdminEmail = admin, Action = "Revive", Target = "Booking:" + id, Detail = $"{b.Code}; nhận {received:N0} đ; mã GD {(isTransfer ? bankRef : "tiền mặt")}" });
            await _db.SaveChangesAsync();
            if (rows != 1)
            {
                await tx.RollbackAsync();
                return Back("Đơn vừa được xử lý bởi người khác.");
            }

            await tx.CommitAsync();
            _log.LogInformation("Admin {Admin} duyệt lại đơn hết hạn {BookingId} ({Code})", admin, id, b.Code);
            return Back($"Đã duyệt lại đơn {b.Code}: {finalTotal:N0} đ.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            try { await tx.RollbackAsync(); } catch { }
            return Back($"Ghế của đơn {b.Code} đã có người khác lấy. Cần hoàn tiền thủ công cho khách.");
        }
        catch (Exception ex) when (ex is DbUpdateException || ex is PostgresException)
        {
            _log.LogError(ex, "Duyệt lại đơn {BookingId} thất bại", id);
            try { await tx.RollbackAsync(); } catch { }
            return Back("Không duyệt lại được. Vui lòng thử lại.");
        }
    }
}