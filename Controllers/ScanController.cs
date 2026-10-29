using System.Security.Claims;
using System.Text.RegularExpressions;
using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class ScanSeat
{
    public string Label { get; set; } = "";
    public string TypeName { get; set; } = "";
    public bool IsChild { get; set; }
    public decimal Paid { get; set; }
    public decimal Diff { get; set; }   // chênh lệch so với giá người lớn (chỉ tính cho vé trẻ em)
}

public class ScanView
{
    public Booking? Booking { get; set; }
    public string Code { get; set; } = "";
    public string State { get; set; } = "notfound";   // ok, used, early, late, unpaid, wrongcinema, nocinema, notfound
    public List<ScanSeat> Seats { get; set; } = new();
    public decimal ChildDiff { get; set; }
    public string? WorkingCinema { get; set; }   // rạp đang làm việc của người quét
    public string? Message { get; set; }         // lý do không cho vào (nếu có)
    public bool CanChangeCinema { get; set; } = true;   // nhân viên rạp bị khóa theo rạp được gán, không tự đổi
}

[Authorize]
public class ScanController : Controller
{
    private static readonly Regex CodeRx = new(@"^SC[A-Z2-9]{8}$");

    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ScanController> _log;

    public ScanController(AppDbContext db, IConfiguration cfg, ILogger<ScanController> log)
    {
        _db = db;
        _cfg = cfg;
        _log = log;
    }

    private const string CinemaCookie = "scan_cinema";

    private int OpenMinutesBefore => _cfg.GetValue("Scan:OpenMinutesBefore", 60);

    // Nhân viên rạp (không phải admin): bị khóa theo rạp được gán
    private bool IsStaff => !User.IsInRole(AdminSeeder.RoleName) && User.IsInRole(AdminSeeder.StaffRoleName);

    // Rạp người quét đang làm việc. Nhân viên: rạp được admin gán. Admin: rạp tự chọn (cookie).
    private async Task<Cinema?> GetWorkingCinemaAsync()
    {
        if (IsStaff)
        {
            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(uid)) return null;
            var cid = await _db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.StaffCinemaId).FirstOrDefaultAsync();
            if (cid == null) return null;
            return await _db.Cinemas.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid.Value);
        }
        if (!Request.Cookies.TryGetValue(CinemaCookie, out var raw) || !int.TryParse(raw, out var id)) return null;
        return await _db.Cinemas.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    // Chỉ admin và nhân viên rạp được quét vé; người khác nhận 404, không biết trang này có tồn tại
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!User.IsInRole(AdminSeeder.RoleName) && !User.IsInRole(AdminSeeder.StaffRoleName)) context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    // Phán quyết cho một vé: trạng thái và lý do (null nếu hợp lệ)
    private (string State, string? Message) Judge(Booking b, Cinema? working, DateTime now)
    {
        if (b.Status != "Paid") return ("unpaid", "Vé chưa thanh toán hoặc đã hủy.");
        if (working == null)
            return ("nocinema", IsStaff
                ? "Tài khoản của bạn chưa được gán rạp. Hãy liên hệ quản trị viên."
                : "Bạn chưa chọn rạp đang làm việc. Vào trang Quét vé để chọn rạp.");
        // Vé của rạp khác thì không cho vào, dù vé hợp lệ
        if (b.Showtime.Room.CinemaId != working.Id)
            return ("wrongcinema", $"Vé này của rạp {b.Showtime.Room.Cinema?.Name}, bạn đang quét ở rạp {working.Name}.");
        if (b.CheckedInAt != null) return ("used", "Vé đã được sử dụng.");
        if (now < b.Showtime.StartTime.AddMinutes(-OpenMinutesBefore))
            return ("early", $"Chưa đến giờ vào rạp (mở cửa từ {OpenMinutesBefore} phút trước giờ chiếu).");
        if (now > b.Showtime.EndTime) return ("late", "Suất chiếu đã kết thúc.");
        return ("ok", null);
    }

    [HttpGet("Admin/Scan")]
    public async Task<IActionResult> Index()
    {
        var working = await GetWorkingCinemaAsync();
        ViewBag.IsStaff = IsStaff;
        ViewBag.Working = working?.Id;
        ViewBag.WorkingName = working?.Name;
        ViewBag.Cinemas = IsStaff
            ? new List<Cinema>()
            : await _db.Cinemas.AsNoTracking().Include(c => c.City)
                .OrderBy(c => c.City.Name).ThenBy(c => c.Name).ToListAsync();
        return View();
    }

    // Chọn rạp đang làm việc (lưu cookie 12 giờ). Máy quét chỉ cho vào vé của rạp này.
    [HttpPost("Admin/Scan/Cinema"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCinema(int cinemaId)
    {
        // Nhân viên không tự đổi rạp: rạp do admin gán
        if (IsStaff) return RedirectToAction(nameof(Index));

        if (await _db.Cinemas.AnyAsync(c => c.Id == cinemaId))
        {
            Response.Cookies.Append(CinemaCookie, cinemaId.ToString(), new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddHours(12)
            });
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Admin/Scan"), ValidateAntiForgeryToken]
    public IActionResult Find(string? code)
    {
        var c = (code ?? "").Trim().ToUpper();
        return RedirectToAction(nameof(Ticket), new { code = c });
    }

    [HttpGet("Admin/Ticket/{code}")]
    public async Task<IActionResult> Ticket(string code)
    {
        var c = (code ?? "").Trim().ToUpper();
        var working = await GetWorkingCinemaAsync();
        var vm = new ScanView { Code = c, WorkingCinema = working?.Name, CanChangeCinema = !IsStaff };
        if (!CodeRx.IsMatch(c)) return View("Ticket", vm);

        var b = await _db.Bookings.AsNoTracking()
            .Include(x => x.Showtime).ThenInclude(s => s.Movie)
            .Include(x => x.Showtime).ThenInclude(s => s.Room).ThenInclude(r => r.Cinema)
            .Include(x => x.Seats).ThenInclude(s => s.Seat)
            .Include(x => x.Items).ThenInclude(i => i.Product)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Code == c);
        if (b == null) return View("Ticket", vm);

        vm.Booking = b;
        var (state0, msg0) = Judge(b, working, DateTime.UtcNow);
        vm.State = state0;
        vm.Message = msg0;
        foreach (var s in b.Seats.OrderBy(x => x.Seat.RowLabel).ThenBy(x => x.Seat.SeatNumber))
        {
            bool child = s.TicketType == "Child";
            decimal adultPrice = TicketPricing.Price(b.Showtime.EffectivePrice, s.Seat.SeatType, "Adult");
            decimal diff = child ? Math.Max(0, adultPrice - s.Price) : 0;
            vm.Seats.Add(new ScanSeat
            {
                Label = s.Seat.RowLabel + s.Seat.SeatNumber,
                TypeName = TicketPricing.Get(s.TicketType).Name,
                IsChild = child,
                Paid = s.Price,
                Diff = diff
            });
            vm.ChildDiff += diff;
        }
        return View("Ticket", vm);
    }

    [HttpPost("Admin/Ticket/{code}/CheckIn"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckIn(string code)
    {
        var c = (code ?? "").Trim().ToUpper();
        if (!CodeRx.IsMatch(c)) return NotFound();

        var now = DateTime.UtcNow;
        var b = await _db.Bookings.AsNoTracking()
            .Include(x => x.Showtime).ThenInclude(s => s.Room).ThenInclude(r => r.Cinema)
            .FirstOrDefaultAsync(x => x.Code == c);
        if (b == null) return NotFound();

        var working = await GetWorkingCinemaAsync();
        var (state, message) = Judge(b, working, now);
        if (state != "ok")
        {
            if (state == "wrongcinema")
                _log.LogWarning("{Who} quét vé {Code} sai rạp ({Msg})", User.Identity?.Name, c, message);
            TempData["ScanMsg"] = message;
            return RedirectToAction(nameof(Ticket), new { code = c });
        }

        // Chỉ một lần: cập nhật có điều kiện, hai nhân viên quét cùng lúc thì chỉ một người thành công
        var who = User.Identity?.Name ?? "";
        int rows = await _db.Bookings
            .Where(x => x.Id == b.Id && x.Status == "Paid" && x.CheckedInAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CheckedInAt, (DateTime?)now)
                .SetProperty(x => x.CheckedInByEmail, (string?)who));

        string msg;
        if (rows == 1)
        {
            _log.LogInformation("{Who} cho vào vé {Code}", who, c);
            msg = "Đã cho vào.";

            // Cộng điểm thành viên cho khách. Lỗi ở đây không được làm hỏng việc cho vào rạp.
            int pts = MembershipRules.PointsFor(b.Total);
            if (pts > 0)
            {
                try
                {
                    _db.PointEntries.Add(new PointEntry
                    {
                        UserId = b.UserId,
                        BookingId = b.Id,
                        Points = pts,
                        Reason = "Xem phim, vé " + c,
                        CreatedAt = now
                    });
                    await _db.SaveChangesAsync();
                    msg = $"Đã cho vào. Khách được cộng {pts} điểm thành viên.";
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Không cộng được điểm cho vé {Code}", c);
                }
            }
        }
        else
        {
            msg = "Vé này vừa được quét bởi người khác.";
        }

        TempData["ScanMsg"] = msg;
        return RedirectToAction(nameof(Ticket), new { code = c });
    }
}