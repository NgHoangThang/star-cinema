using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class TicketRow
{
    public int Id { get; set; }
    public string? Code { get; set; }
    public string Title { get; set; } = "";
    public string Poster { get; set; } = "";
    public string Cinema { get; set; } = "";
    public DateTime Start { get; set; }
    public int Seats { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = "";
    public bool CancelRequested { get; set; }
}

[Authorize]
public class TicketsController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TicketsController> _log;

    public TicketsController(AppDbContext db, UserManager<AppUser> users, IConfiguration cfg, ILogger<TicketsController> log)
    {
        _db = db;
        _users = users;
        _cfg = cfg;
        _log = log;
    }

    private string CurrentUserId => _users.GetUserId(User) ?? "";

    // Khách chỉ gửi được yêu cầu hủy vé trước giờ chiếu ít nhất bấy nhiêu giờ (đổi ở Booking:CancelMinHoursBefore)
    private int CancelMinHours => _cfg.GetValue("Booking:CancelMinHoursBefore", 2);

    // ================= Danh sách vé của tôi =================
    public async Task<IActionResult> Index()
    {
        var userId = CurrentUserId;
        var now = DateTime.UtcNow;

        var rows = await _db.Bookings.AsNoTracking()
            .Where(b => b.UserId == userId &&
                        (b.Status == "Paid" || b.Status == "Refunded" || (b.Status == "WaitingPayment" && b.ExpiresAt > now)))
            .OrderByDescending(b => b.Showtime.StartTime)
            .Select(b => new TicketRow
            {
                Id = b.Id,
                Code = b.Code,
                Title = b.Showtime.Movie.Title,
                Poster = b.Showtime.Movie.PosterUrl,
                Cinema = b.Showtime.Room.Cinema.Name,
                Start = b.Showtime.StartTime,
                Seats = b.Seats.Count,
                Total = b.Total,
                Status = b.Status,
                CancelRequested = b.CancelRequestedAt != null
            })
            .Take(100)
            .ToListAsync();

        return View(rows);
    }

    // ================= Một vé có mã QR =================
    public async Task<IActionResult> Details(int id)
    {
        var userId = CurrentUserId;
        var b = await _db.Bookings
            .Include(x => x.Showtime).ThenInclude(s => s.Movie)
            .Include(x => x.Showtime).ThenInclude(s => s.Room).ThenInclude(r => r.Cinema)
            .Include(x => x.Seats).ThenInclude(s => s.Seat)
            .Include(x => x.Items).ThenInclude(i => i.Product)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);   // chỉ chủ vé mới xem được
        if (b == null) return NotFound();

        if (b.Status == "WaitingPayment") return RedirectToAction("Wait", "Payment", new { id });
        if (b.Status != "Paid" || string.IsNullOrEmpty(b.Code)) return NotFound();

        var url = $"{Request.Scheme}://{Request.Host}/Admin/Ticket/{b.Code}";
        ViewBag.QrUrl = VietQr.QrDataUrl(url);
        ViewBag.ScanUrl = url;

        int hours = CancelMinHours;
        ViewBag.CancelHours = hours;
        ViewBag.CanRequestCancel = b.CheckedInAt == null && b.CancelRequestedAt == null
                                   && b.Showtime.StartTime > DateTime.UtcNow.AddHours(hours);
        return View(b);
    }

    // ================= Khách gửi yêu cầu hủy vé (admin duyệt bằng nút Hoàn tiền) =================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestCancel(int id, string? reason)
    {
        var userId = CurrentUserId;
        IActionResult Back(string msg)
        {
            TempData["TkMsg"] = msg;
            return RedirectToAction(nameof(Details), new { id });
        }

        var b = await _db.Bookings.AsNoTracking().Include(x => x.Showtime)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (b == null) return NotFound();

        var why = (reason ?? "").Trim();
        if (why.Length < 3) return Back("Vui lòng ghi lý do hủy vé (ít nhất 3 ký tự).");
        if (why.Length > 200) why = why.Substring(0, 200);

        int hours = CancelMinHours;
        if (b.Status != "Paid") return Back("Chỉ gửi được yêu cầu hủy cho vé đã thanh toán.");
        if (b.CheckedInAt != null) return Back("Vé đã được quét vào rạp nên không hủy được.");
        if (b.CancelRequestedAt != null) return Back("Bạn đã gửi yêu cầu hủy vé này rồi.");
        if (b.Showtime.StartTime <= DateTime.UtcNow.AddHours(hours))
            return Back($"Đã quá hạn: chỉ hủy được vé trước giờ chiếu ít nhất {hours} giờ. Cần hỗ trợ, vui lòng liên hệ rạp.");

        // Cập nhật có điều kiện: bấm hai lần hoặc admin đang xử lý thì chỉ một thao tác thành công
        int rows = await _db.Bookings
            .Where(x => x.Id == id && x.UserId == userId && x.Status == "Paid" && x.CheckedInAt == null && x.CancelRequestedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CancelRequestedAt, (DateTime?)DateTime.UtcNow)
                .SetProperty(x => x.CancelRequestReason, (string?)why));
        if (rows != 1) return Back("Không gửi được yêu cầu, vé vừa thay đổi. Vui lòng tải lại trang.");

        _log.LogInformation("Khách {UserId} gửi yêu cầu hủy vé {Code}", userId, b.Code);
        return Back("Đã gửi yêu cầu hủy vé. Rạp sẽ xem xét và chuyển trả tiền cho bạn nếu chấp nhận.");
    }

    // ================= Khách rút lại yêu cầu hủy (trước khi admin xử lý) =================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawCancel(int id)
    {
        var userId = CurrentUserId;
        int rows = await _db.Bookings
            .Where(x => x.Id == id && x.UserId == userId && x.Status == "Paid" && x.CheckedInAt == null && x.CancelRequestedAt != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CancelRequestedAt, (DateTime?)null)
                .SetProperty(x => x.CancelRequestReason, (string?)null));
        TempData["TkMsg"] = rows == 1 ? "Đã rút lại yêu cầu hủy vé." : "Không rút lại được yêu cầu (vé đã được xử lý).";
        return RedirectToAction(nameof(Details), new { id });
    }
}