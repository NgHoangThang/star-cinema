using System.Globalization;
using System.Text.RegularExpressions;
using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class AdminShowtimeRow
{
    public int Id { get; set; }
    public string MovieTitle { get; set; } = "";
    public string CinemaName { get; set; } = "";
    public string RoomName { get; set; } = "";
    public string RoomType { get; set; } = "";
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    public string Format { get; set; } = "";
    public string Language { get; set; } = "";
    public decimal BasePrice { get; set; }
    public decimal EffectivePrice { get; set; }
    public string? Promo { get; set; }
    public int ActiveBookings { get; set; }   // đơn đang giữ ghế, chờ tiền hoặc đã thanh toán
    public int TotalBookings { get; set; }    // mọi đơn đã từng gắn với suất này
    public bool Past { get; set; }
}

[Authorize]
[Route("Admin/Showtimes")]
public class AdminShowtimesController : Controller
{
    private const int AdMinutes = 15;        // quảng cáo trước phim (cộng vào giờ kết thúc, giống bộ tạo dữ liệu mẫu)
    private const int CleanMinutes = 10;     // nghỉ dọn phòng giữa hai suất
    private const decimal MinPrice = 30000m;
    private const decimal MaxPrice = 1000000m;
    private static readonly string[] Languages = { "Phụ đề Anh & Việt", "Lồng tiếng Việt" };
    private static readonly Regex TimeRx = new(@"^\d{1,2}:\d{2}$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ILogger<AdminShowtimesController> _log;

    public AdminShowtimesController(AppDbContext db, ILogger<AdminShowtimesController> log)
    {
        _db = db;
        _log = log;
    }

    // Người không phải admin nhận 404, giống các trang quản trị khác
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!User.IsInRole(AdminSeeder.RoleName)) context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    // Việt Nam UTC+7. Database lưu UTC.
    private static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local.AddHours(-7), DateTimeKind.Utc);
    private static DateTime NowVn => DateTime.UtcNow.AddHours(7);
    private static string FormatOf(string roomType) => roomType switch { "4DX" => "4DX2D", "SCREENX" => "SCREENX", _ => "2D" };

    private IActionResult Back(string msg, string? date, int? cinemaId, int? movieId)
    {
        TempData["AdminMsg"] = msg;
        return RedirectToAction(nameof(Index), new { date, cinemaId, movieId });
    }

    // ================= Danh sách và form tạo =================
    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, int? cinemaId, int? movieId)
    {
        var day = NowVn.Date;
        if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) day = d.Date;

        var from = ToUtc(day);
        var to = from.AddDays(1);

        var query = _db.Showtimes.AsNoTracking()
            .Include(s => s.Movie)
            .Include(s => s.Room).ThenInclude(r => r.Cinema)
            .Where(s => s.StartTime >= from && s.StartTime < to);
        if (cinemaId.HasValue) query = query.Where(s => s.Room.CinemaId == cinemaId.Value);
        if (movieId.HasValue) query = query.Where(s => s.MovieId == movieId.Value);

        var list = await query.OrderBy(s => s.Room.Cinema.Name).ThenBy(s => s.Room.Name).ThenBy(s => s.StartTime).ToListAsync();

        var ids = list.Select(s => s.Id).ToList();
        var counts = await _db.Bookings.AsNoTracking()
            .Where(b => ids.Contains(b.ShowtimeId))
            .GroupBy(b => b.ShowtimeId)
            .Select(g => new
            {
                Id = g.Key,
                Total = g.Count(),
                Active = g.Count(b => b.Status == "Pending" || b.Status == "WaitingPayment" || b.Status == "Paid")
            })
            .ToDictionaryAsync(x => x.Id);

        var now = DateTime.UtcNow;
        var rows = list.Select(s => new AdminShowtimeRow
        {
            Id = s.Id,
            MovieTitle = s.Movie.Title,
            CinemaName = s.Room.Cinema.Name,
            RoomName = s.Room.Name,
            RoomType = s.Room.Type,
            Start = s.StartTime.AddHours(7).ToString("HH:mm"),
            End = s.EndTime.AddHours(7).ToString("HH:mm"),
            Format = s.Format,
            Language = s.LanguageMode,
            BasePrice = s.BasePrice,
            EffectivePrice = s.EffectivePrice,
            Promo = s.PromoLabel,
            ActiveBookings = counts.TryGetValue(s.Id, out var c) ? c.Active : 0,
            TotalBookings = counts.TryGetValue(s.Id, out var c2) ? c2.Total : 0,
            Past = s.StartTime <= now
        }).ToList();

        ViewBag.Date = day.ToString("yyyy-MM-dd");
        ViewBag.CinemaId = cinemaId;
        ViewBag.MovieId = movieId;
        ViewBag.Today = NowVn.Date.ToString("yyyy-MM-dd");
        ViewBag.Cinemas = await _db.Cinemas.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        ViewBag.Rooms = await _db.Rooms.AsNoTracking().Include(r => r.Cinema)
            .OrderBy(r => r.Cinema.Name).ThenBy(r => r.Name).ToListAsync();
        ViewBag.Movies = await _db.Movies.AsNoTracking().Where(m => m.Status == "NowShowing")
            .OrderBy(m => m.Title).Select(m => new { m.Id, m.Title, m.DurationMin }).ToListAsync();
        ViewBag.Languages = Languages;
        return View("~/Views/Admin/Showtimes.cshtml", rows);
    }

    // Giá gợi ý theo loại phòng, cuối tuần cộng 20.000 đ (giống bộ tạo dữ liệu mẫu). Dùng khi tạo cho nhiều phòng một lúc.
    private static decimal DefaultPrice(string roomType, DateTime localDay)
    {
        decimal p = roomType switch { "4DX" => 153000m, "GOLDCLASS" => 180000m, "SCREENX" => 130000m, _ => 90000m };
        if (localDay.DayOfWeek == DayOfWeek.Saturday || localDay.DayOfWeek == DayOfWeek.Sunday) p += 20000m;
        return p;
    }

    // ================= Tạo suất chiếu (nhiều giờ, nhiều ngày, một phòng hoặc cả rạp / mọi rạp một lần) =================
    // roomId > 0: một phòng, trùng giờ thì không tạo suất nào.
    // roomId = 0: mọi phòng của rạp đã chọn (cinemaId > 0) hoặc mọi phòng của mọi rạp (cinemaId = 0); giá tự theo loại phòng; suất nào trùng giờ thì bỏ qua và báo số suất bị bỏ.
    [HttpPost("Create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int movieId, int cinemaId, int roomId, string? fromDate, string? toDate,
                                            string? times, string? language, decimal basePrice)
    {
        string? back = fromDate;
        bool multi = roomId == 0;

        var movie = await _db.Movies.FirstOrDefaultAsync(m => m.Id == movieId);
        if (movie == null) return Back("Chưa chọn phim.", back, null, null);
        if (movie.Status != "NowShowing")
            return Back("Chỉ tạo suất chiếu cho phim đang chiếu. Hãy đổi trạng thái phim sang Đang chiếu trước.", back, null, null);

        // Các phòng sẽ tạo suất (chỉ lấy phòng đã có ghế)
        List<Room> targets;
        if (!multi)
        {
            var one = await _db.Rooms.AsNoTracking().Include(r => r.Cinema).FirstOrDefaultAsync(r => r.Id == roomId);
            if (one == null) return Back("Chưa chọn phòng chiếu.", back, null, null);
            if (!await _db.Seats.AnyAsync(s => s.RoomId == roomId))
                return Back("Phòng này chưa có ghế nên chưa tạo được suất chiếu.", back, null, null);
            targets = new List<Room> { one };
        }
        else
        {
            var rq = _db.Rooms.AsNoTracking().Include(r => r.Cinema).AsQueryable();
            if (cinemaId > 0) rq = rq.Where(r => r.CinemaId == cinemaId);
            targets = await rq.Where(r => _db.Seats.Any(s => s.RoomId == r.Id))
                .OrderBy(r => r.Cinema.Name).ThenBy(r => r.Name).ToListAsync();
            if (targets.Count == 0) return Back("Không có phòng nào (có ghế) để tạo suất chiếu.", back, null, null);
        }

        if (language == null || !Languages.Contains(language)) return Back("Chưa chọn ngôn ngữ.", back, null, null);
        if (!multi && (basePrice < MinPrice || basePrice > MaxPrice || basePrice % 1000 != 0))
            return Back($"Giá cơ bản phải là bội số của 1.000 đ, từ {MinPrice:N0} đến {MaxPrice:N0} đ.", back, null, null);

        if (!DateTime.TryParseExact(fromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)
            || !DateTime.TryParseExact(string.IsNullOrWhiteSpace(toDate) ? fromDate : toDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return Back("Ngày không hợp lệ.", back, null, null);
        d1 = d1.Date; d2 = d2.Date;
        if (d2 < d1) return Back("Ngày kết thúc phải sau hoặc bằng ngày bắt đầu.", back, null, null);
        if ((d2 - d1).Days > 30) return Back("Mỗi lần tạo tối đa 31 ngày.", back, null, null);

        // Giờ chiếu: "09:00, 12:30, 16:00"
        var slots = new SortedSet<TimeSpan>();
        foreach (var part in (times ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TimeRx.IsMatch(part) || !TimeSpan.TryParse(part, CultureInfo.InvariantCulture, out var ts) || ts.TotalHours >= 24 || ts.Minutes % 5 != 0)
                return Back("Giờ chiếu \"" + part + "\" không hợp lệ (dạng 09:00, phút chia hết cho 5).", back, null, null);
            slots.Add(ts);
        }
        if (slots.Count == 0) return Back("Chưa nhập giờ chiếu.", back, null, null);
        if (slots.Count > 12) return Back("Mỗi ngày tối đa 12 suất mỗi lần tạo.", back, null, null);

        // Các thời điểm bắt đầu (giờ Việt Nam), kiểm tra tương lai và ngày khởi chiếu
        var locals = new List<DateTime>();
        var nowUtc = DateTime.UtcNow;
        for (var day = d1; day <= d2; day = day.AddDays(1))
        {
            foreach (var ts in slots)
            {
                var local = day.Add(ts);
                if (ToUtc(local) <= nowUtc) return Back("Giờ chiếu " + local.ToString("dd/MM HH:mm") + " đã qua, chỉ tạo suất trong tương lai.", back, null, null);
                if (local.Date < movie.ReleaseDate.Date)
                    return Back("Phim khởi chiếu ngày " + movie.ReleaseDate.ToString("dd/MM/yyyy") + ", không tạo suất trước ngày đó.", back, null, null);
                locals.Add(local);
            }
        }
        if ((long)locals.Count * targets.Count > 10000) return Back("Mỗi lần tạo tối đa 10.000 suất. Hãy giảm số ngày, số giờ hoặc chọn ít phòng hơn.", back, null, null);

        int dur = movie.DurationMin > 0 ? movie.DurationMin : 110;
        var minStart = ToUtc(locals.Min()).AddDays(-1);
        var maxEnd = ToUtc(locals.Max()).AddMinutes(dur + AdMinutes).AddDays(1);

        // Các suất đã có trong những phòng này, để chặn chồng giờ (kể cả thời gian nghỉ dọn phòng)
        var roomIds = targets.Select(r => r.Id).ToList();
        var existing = await _db.Showtimes.AsNoTracking()
            .Where(s => roomIds.Contains(s.RoomId) && s.StartTime < maxEnd && s.EndTime > minStart)
            .Select(s => new { s.RoomId, s.StartTime, s.EndTime })
            .ToListAsync();
        var takenByRoom = existing.GroupBy(e => e.RoomId)
            .ToDictionary(g => g.Key, g => g.Select(e => (Start: e.StartTime, End: e.EndTime)).ToList());

        var toAdd = new List<Showtime>();
        var conflicts = new List<string>();
        int skipped = 0;
        foreach (var room in targets)
        {
            if (!takenByRoom.TryGetValue(room.Id, out var taken)) taken = new List<(DateTime Start, DateTime End)>();
            string format = FormatOf(room.Type);

            foreach (var local in locals)
            {
                var start = ToUtc(local);
                var end = start.AddMinutes(dur + AdMinutes);
                bool clash = taken.Any(e => start < e.End.AddMinutes(CleanMinutes) && e.Start < end.AddMinutes(CleanMinutes));
                if (clash)
                {
                    skipped++;
                    if (!multi) conflicts.Add(local.ToString("dd/MM HH:mm"));
                    continue;
                }
                taken.Add((start, end));
                toAdd.Add(new Showtime
                {
                    MovieId = movie.Id,
                    RoomId = room.Id,
                    StartTime = start,
                    EndTime = end,
                    Format = format,
                    LanguageMode = language,
                    BasePrice = multi ? DefaultPrice(room.Type, local.Date) : basePrice
                });
            }
        }

        if (!multi && conflicts.Count > 0)
            return Back($"Trùng giờ với suất khác trong {targets[0].Name} ({targets[0].Cinema.Name}): " + string.Join(", ", conflicts.Take(6))
                        + (conflicts.Count > 6 ? "..." : "") + ". Chưa tạo suất nào.", back, null, null);
        if (toAdd.Count == 0)
            return Back("Không tạo được suất nào vì tất cả đều trùng giờ với suất đã có.", back, null, null);

        _db.Showtimes.AddRange(toAdd);
        string scope = !multi ? $"{targets[0].Cinema.Name} {targets[0].Name}" : (cinemaId > 0 ? $"mọi phòng {targets[0].Cinema.Name}" : $"mọi rạp ({targets.Count} phòng)");
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = User.Identity?.Name ?? "",
            Action = "ShowtimeCreate",
            Target = "Movie:" + movie.Id,
            Detail = $"{toAdd.Count} suất; {movie.Title}; {scope}; {d1:dd/MM}–{d2:dd/MM}; {string.Join(", ", slots.Select(t => t.ToString(@"hh\:mm")))}"
                     + (multi ? "; giá theo loại phòng" : $"; {basePrice:N0} đ") + (skipped > 0 ? $"; bỏ {skipped} suất trùng giờ" : "")
        });
        await _db.SaveChangesAsync();

        string msg = $"Đã tạo {toAdd.Count} suất chiếu cho \"{movie.Title}\" ({scope})." + (skipped > 0 ? $" Bỏ qua {skipped} suất vì trùng giờ." : "");
        return Back(msg, d1.ToString("yyyy-MM-dd"), multi && cinemaId == 0 ? null : targets[0].CinemaId, null);
    }

    // ================= Đổi giá cơ bản =================
    // Giá ghế đã giữ hoặc đã bán được chốt lúc giữ ghế nên đổi giá không ảnh hưởng đơn cũ
    [HttpPost("Price/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPrice(int id, decimal basePrice, string? date, int? cinemaId, int? movieId)
    {
        if (basePrice < MinPrice || basePrice > MaxPrice || basePrice % 1000 != 0)
            return Back($"Giá cơ bản phải là bội số của 1.000 đ, từ {MinPrice:N0} đến {MaxPrice:N0} đ.", date, cinemaId, movieId);

        var s = await _db.Showtimes.Include(x => x.Movie).FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return Back("Không tìm thấy suất chiếu.", date, cinemaId, movieId);

        decimal old = s.BasePrice;
        s.BasePrice = basePrice;
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = User.Identity?.Name ?? "",
            Action = "ShowtimePrice",
            Target = "Showtime:" + id,
            Detail = $"{s.Movie.Title} {s.StartTime.AddHours(7):dd/MM HH:mm}; {old:N0} → {basePrice:N0} đ"
        });
        await _db.SaveChangesAsync();
        return Back("Đã đổi giá cơ bản thành " + basePrice.ToString("N0") + " đ.", date, cinemaId, movieId);
    }

    // ================= Dọn lịch: xóa hàng loạt suất tương lai chưa có đơn =================
    [HttpGet("DeleteCount")]
    public async Task<IActionResult> DeleteCount(string? fromDate, string? toDate, int? cinemaId, int? movieId)
    {
        const string fmt = "yyyy-MM-dd";
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (!DateTime.TryParseExact(fromDate, fmt, ci, System.Globalization.DateTimeStyles.None, out var f))
            return Json(new { total = 0, deletable = 0 });
        if (!DateTime.TryParseExact(toDate, fmt, ci, System.Globalization.DateTimeStyles.None, out var t) || t < f) t = f;

        var from = DateTime.SpecifyKind(f.Date.AddHours(-7), DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(t.Date.AddDays(1).AddHours(-7), DateTimeKind.Utc);
        var now = DateTime.UtcNow;

        var q = _db.Showtimes.Where(s => s.StartTime >= from && s.StartTime < to && s.StartTime > now);
        if (cinemaId.HasValue && cinemaId > 0) q = q.Where(s => s.Room.CinemaId == cinemaId.Value);
        if (movieId.HasValue && movieId > 0) q = q.Where(s => s.MovieId == movieId.Value);

        int total = await q.CountAsync();
        int deletable = await q.CountAsync(s => !_db.Bookings.Any(b => b.ShowtimeId == s.Id) && !_db.ShowtimeSeats.Any(x => x.ShowtimeId == s.Id));
        return Json(new { total, deletable });
    }

    [HttpPost("DeleteMany"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMany(string? fromDate, string? toDate, int? cinemaId, int? movieId)
    {
        if (!DateTime.TryParseExact(fromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)
            || !DateTime.TryParseExact(string.IsNullOrWhiteSpace(toDate) ? fromDate : toDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return Back("Ngày không hợp lệ.", fromDate, cinemaId, movieId);
        d1 = d1.Date; d2 = d2.Date;
        if (d2 < d1 || (d2 - d1).Days > 30)
            return Back("Khoảng ngày không hợp lệ (tối đa 31 ngày).", fromDate, cinemaId, movieId);

        var from = ToUtc(d1);
        var to = ToUtc(d2).AddDays(1);
        var now = DateTime.UtcNow;

        var q = _db.Showtimes.Where(s => s.StartTime >= from && s.StartTime < to && s.StartTime > now);
        if (cinemaId.HasValue && cinemaId > 0) q = q.Where(s => s.Room.CinemaId == cinemaId.Value);
        if (movieId.HasValue && movieId > 0) q = q.Where(s => s.MovieId == movieId.Value);

        int total = await q.CountAsync();
        if (total == 0) return Back("Không có suất chiếu tương lai nào phù hợp để dọn.", fromDate, cinemaId, movieId);

        int deleted = await q
            .Where(s => !_db.Bookings.Any(b => b.ShowtimeId == s.Id) && !_db.ShowtimeSeats.Any(x => x.ShowtimeId == s.Id))
            .ExecuteDeleteAsync();
        int kept = total - deleted;

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = User.Identity?.Name ?? "",
            Action = "ShowtimeDeleteMany",
            Target = "Showtime:many",
            Detail = $"Xóa {deleted} suất, giữ {kept} suất đã có đơn; {d1:dd/MM}–{d2:dd/MM}; rạp {(cinemaId > 0 ? cinemaId.ToString() : "tất cả")}; phim {(movieId > 0 ? movieId.ToString() : "tất cả")}"
        });
        await _db.SaveChangesAsync();

        return Back($"Đã xóa {deleted} suất chiếu chưa có đơn." + (kept > 0 ? $" Giữ lại {kept} suất vì đã có đơn đặt vé." : ""),
                    d1.ToString("yyyy-MM-dd"), cinemaId, movieId);
    }

    // ================= Xóa =================
    // Suất đã có đơn đặt vé (kể cả đơn đã hủy hoặc hết hạn) thì không xóa được, để giữ lịch sử và đối soát
    [HttpPost("Delete/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, string? date, int? cinemaId, int? movieId)
    {
        var s = await _db.Showtimes.Include(x => x.Movie).FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return Back("Không tìm thấy suất chiếu.", date, cinemaId, movieId);

        if (await _db.Bookings.AnyAsync(b => b.ShowtimeId == id) || await _db.ShowtimeSeats.AnyAsync(x => x.ShowtimeId == id))
            return Back("Không xóa được suất này vì đã có đơn đặt vé gắn với nó.", date, cinemaId, movieId);

        try
        {
            string detail = $"{s.Movie.Title} {s.StartTime.AddHours(7):dd/MM HH:mm}";
            _db.Showtimes.Remove(s);
            _db.AdminAuditLogs.Add(new AdminAuditLog
            {
                At = DateTime.UtcNow,
                AdminEmail = User.Identity?.Name ?? "",
                Action = "ShowtimeDelete",
                Target = "Showtime:" + id,
                Detail = detail
            });
            await _db.SaveChangesAsync();
            return Back("Đã xóa suất " + detail + ".", date, cinemaId, movieId);
        }
        catch (DbUpdateException ex)
        {
            _log.LogError(ex, "Xóa suất chiếu {Id} thất bại", id);
            return Back("Không xóa được suất chiếu này vì còn dữ liệu liên quan.", date, cinemaId, movieId);
        }
    }
}