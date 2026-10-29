using System.Net;
using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CinemaApp.Controllers;

// Admin hoàn tiền cho đơn đã thanh toán (suất chưa chiếu, vé chưa quét). Tiền thật admin tự chuyển trả khách.
// Hoàn xong hệ thống gửi email báo cho khách (gửi lỗi thì không ảnh hưởng việc hoàn tiền).
[Authorize]
public class AdminRefundController : Controller
{
    private readonly AppDbContext _db;
    private readonly IEmailService _email;
    private readonly ILogger<AdminRefundController> _log;

    public AdminRefundController(AppDbContext db, IEmailService email, ILogger<AdminRefundController> log)
    {
        _db = db;
        _email = email;
        _log = log;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!User.IsInRole(AdminSeeder.RoleName)) context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    [HttpPost("Admin/Refund/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Refund(int id, string? reason)
    {
        IActionResult Back(string msg)
        {
            TempData["AdminMsg"] = msg;
            return Redirect("/Admin/Orders?status=Paid");
        }

        var now = DateTime.UtcNow;
        var why = (reason ?? "").Trim();
        if (why.Length < 3) return Back("Cần ghi lý do hoàn tiền (ít nhất 3 ký tự).");
        if (why.Length > 200) why = why.Substring(0, 200);

        var b = await _db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();

        if (b.Status == "Refunded") return Back($"Đơn {b.Code} đã được hoàn tiền trước đó.");
        if (b.Status != "Paid") return Back("Chỉ hoàn tiền được cho đơn đã thanh toán.");
        if (b.CheckedInAt != null) return Back($"Vé {b.Code} đã được quét vào rạp nên không hoàn tiền được.");

        var start = await _db.Bookings.Where(x => x.Id == id).Select(x => x.Showtime.StartTime).FirstAsync();
        if (start <= now) return Back($"Suất chiếu của đơn {b.Code} đã bắt đầu nên không hoàn tiền được.");

        var admin = User.Identity?.Name ?? "";
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            int rows = await _db.Bookings
                .Where(x => x.Id == id && x.Status == "Paid" && x.CheckedInAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "Refunded")
                    .SetProperty(x => x.RefundedAt, (DateTime?)now)
                    .SetProperty(x => x.RefundedByEmail, (string?)admin)
                    .SetProperty(x => x.RefundReason, (string?)why));
            if (rows != 1)
            {
                await tx.RollbackAsync();
                return Back("Đơn vừa được xử lý bởi người khác, hãy tải lại trang.");
            }

            // Trả ghế lại cho người khác đặt
            await _db.ShowtimeSeats.Where(x => x.BookingId == id).ExecuteDeleteAsync();
            _db.AdminAuditLogs.Add(new AdminAuditLog { At = now, AdminEmail = admin, Action = "Refund", Target = "Booking:" + id, Detail = $"{b.Code}; hoàn {b.Total:N0} đ; lý do: {why}" });
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            _log.LogInformation("Admin {Admin} hoàn tiền đơn {BookingId} ({Code}): {Reason}", admin, id, b.Code, why);
        }
        catch (Exception ex) when (ex is DbUpdateException || ex is PostgresException)
        {
            _log.LogError(ex, "Hoàn tiền đơn {BookingId} thất bại", id);
            try { await tx.RollbackAsync(); } catch { }
            return Back("Không hoàn tiền được. Vui lòng thử lại.");
        }

        // Đã hoàn xong: báo cho khách qua email (không làm hỏng việc hoàn tiền nếu gửi lỗi)
        bool mailed = false;
        try { mailed = await NotifyCustomerAsync(id, b.Code, b.Total, why); }
        catch (Exception ex) { _log.LogError(ex, "Gửi email hoàn tiền cho đơn {BookingId} thất bại", id); }

        return Back($"Đã hoàn tiền đơn {b.Code}, ghế đã được trả lại. Nhớ chuyển trả {b.Total:N0} đ cho khách."
                    + (mailed ? " Đã gửi email báo cho khách." : " Chưa gửi được email báo khách, hãy liên hệ khách trực tiếp."));
    }

    private async Task<bool> NotifyCustomerAsync(int id, string? code, decimal total, string why)
    {
        var info = await _db.Bookings.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new
            {
                Email = _db.Users.Where(u => u.Id == x.UserId).Select(u => u.Email).FirstOrDefault(),
                Movie = x.Showtime.Movie.Title,
                Cinema = x.Showtime.Room.Cinema.Name,
                Start = x.Showtime.StartTime
            }).FirstOrDefaultAsync();
        if (info == null || string.IsNullOrEmpty(info.Email)) return false;

        string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var local = info.Start.AddHours(7);
        var html =
            "<div style=\"font-family:Arial,Helvetica,sans-serif;max-width:560px;margin:0 auto;color:#222\">" +
            "<h2 style=\"color:#b97a10;margin:0 0 4px\">Star Cinema</h2>" +
            "<p style=\"margin:0 0 16px\">Đơn đặt vé của bạn đã được hoàn tiền. Vé này không còn hiệu lực.</p>" +
            "<table style=\"width:100%;border-collapse:collapse;font-size:14px\">" +
            "<tr><td style=\"padding:6px 0;color:#777;width:130px\">Mã đặt vé</td><td><b>" + H(code) + "</b></td></tr>" +
            "<tr><td style=\"padding:6px 0;color:#777\">Phim</td><td>" + H(info.Movie) + "</td></tr>" +
            "<tr><td style=\"padding:6px 0;color:#777\">Rạp</td><td>" + H(info.Cinema) + "</td></tr>" +
            "<tr><td style=\"padding:6px 0;color:#777\">Suất chiếu</td><td>" + local.ToString("HH:mm") + " · " + local.ToString("dd/MM/yyyy") + "</td></tr>" +
            "<tr><td style=\"padding:6px 0;color:#777\">Số tiền hoàn</td><td><b>" + total.ToString("N0") + " đ</b></td></tr>" +
            "<tr><td style=\"padding:6px 0;color:#777;vertical-align:top\">Lý do</td><td>" + H(why) + "</td></tr>" +
            "</table>" +
            "<p style=\"margin:18px 0 0\">Rạp sẽ chuyển trả số tiền trên cho bạn. Nếu chưa nhận được tiền hoặc cần hỗ trợ, vui lòng liên hệ rạp kèm mã đặt vé.</p>" +
            "</div>";

        return await _email.SendAsync(info.Email!, "Đã hoàn tiền đơn " + code + " - Star Cinema", html);
    }
}