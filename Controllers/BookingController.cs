using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CinemaApp.Controllers;

[Authorize]
public class BookingController : Controller
{
    private const int MaxPerProduct = 10;

    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly IConfiguration _cfg;

    public BookingController(AppDbContext db, UserManager<AppUser> users, IConfiguration cfg)
    {
        _db = db;
        _users = users;
        _cfg = cfg;
    }

    private string CurrentUserId => _users.GetUserId(User) ?? "";
    private int MaxSeats => _cfg.GetValue("Booking:MaxSeats", 20);                    // 0 = không giới hạn
    private int HoldMinutes => _cfg.GetValue("Booking:HoldMinutes", 10);
    private int MaxChildrenPerAdult => _cfg.GetValue("Booking:MaxChildrenPerAdult", 2);
    private int MaxHoldsPerHour => _cfg.GetValue("Booking:MaxHoldsPerHour", 5);       // 0 = không giới hạn
    private int StopSalesMinutes => _cfg.GetValue("Booking:StopSalesMinutes", 30);    // 0 = bán đến giờ chiếu

    // Vé trẻ em chỉ dành cho phim P hoặc K
    private static bool ChildAllowed(string? rating) => rating == "P" || rating == "K";

    // Chỉ bắt xác minh số điện thoại khi Security:RequirePhoneForBooking = "true"
    private async Task<IActionResult?> RequirePhone(string returnUrl)
    {
        if (_cfg["Security:RequirePhoneForBooking"] != "true") return null;
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!user.PhoneNumberConfirmed)
            return Redirect("/Account/VerifyPhone?returnUrl=" + Uri.EscapeDataString(returnUrl));
        return null;
    }

    private Task<Booking?> LoadOwnedBooking(int id)
    {
        var userId = CurrentUserId;
        return _db.Bookings
            .Include(x => x.Showtime).ThenInclude(s => s.Movie)
            .Include(x => x.Showtime).ThenInclude(s => s.Room).ThenInclude(r => r.Cinema)
            .Include(x => x.Seats).ThenInclude(s => s.Seat)
            .Include(x => x.Items).ThenInclude(i => i.Product)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);   // chỉ chủ đơn mới xem được
    }

    private static bool IsActive(Booking b) => b.Status == "Pending" && b.ExpiresAt > DateTime.UtcNow;

    // ================= Trang chọn ghế =================
    public async Task<IActionResult> Seats(int id)
    {
        var gate = await RequirePhone($"/Booking/Seats/{id}");
        if (gate != null) return gate;

        var showtime = await _db.Showtimes
            .Include(s => s.Movie)
            .Include(s => s.Room).ThenInclude(r => r.Cinema)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (showtime == null) return NotFound();

        var now = DateTime.UtcNow;
        var userId = CurrentUserId;

        ViewBag.Seats = await _db.Seats.AsNoTracking()
            .Where(x => x.RoomId == showtime.RoomId)
            .OrderBy(x => x.PosY).ThenBy(x => x.PosX)
            .ToListAsync();

        // Trạng thái từng ghế: đã bán, đang được người khác giữ, hoặc do chính bạn giữ
        var taken = await _db.ShowtimeSeats.AsNoTracking()
            .Where(x => x.ShowtimeId == id && (x.Status == "Sold" || x.HoldExpiresAt > now))
            .Select(x => new { x.SeatId, x.Status, x.UserId, x.TicketType })
            .ToListAsync();
        var state = new Dictionary<int, string>();
        var myTypes = new Dictionary<int, string>();
        foreach (var t in taken)
        {
            state[t.SeatId] = t.Status == "Sold" ? "sold" : (t.UserId == userId ? "mine" : "held");
            if (t.UserId == userId && t.Status == "Held") myTypes[t.SeatId] = t.TicketType;
        }
        ViewBag.State = state;
        ViewBag.MyTypes = myTypes;

        ViewBag.MyBooking = await _db.Bookings.AsNoTracking()
            .Where(b => b.UserId == userId && b.ShowtimeId == id && b.Status == "Pending" && b.ExpiresAt > now)
            .Select(b => (int?)b.Id)
            .FirstOrDefaultAsync();

        // Đơn đang giữ ghế ở một suất KHÁC (giữ ghế suất này thì ghế suất kia sẽ được nhả)
        ViewBag.OtherHold = await _db.Bookings.AsNoTracking()
            .Where(b => b.UserId == userId && b.ShowtimeId != id && b.Status == "Pending" && b.ExpiresAt > now)
            .Select(b => (int?)b.Id)
            .FirstOrDefaultAsync();

        // Lý do đóng bán: "started" = đã bắt đầu chiếu, "stopped" = đã quá giờ ngừng bán online
        string? closedReason = null;
        if (showtime.StartTime <= now) closedReason = "started";
        else if (StopSalesMinutes > 0 && showtime.StartTime <= now.AddMinutes(StopSalesMinutes)) closedReason = "stopped";
        ViewBag.ClosedReason = closedReason;
        ViewBag.StopMinutes = StopSalesMinutes;

        ViewBag.MaxSeats = MaxSeats;
        ViewBag.Closed = closedReason == "started";   // giữ cho trang chọn ghế bản cũ; bản mới dùng ClosedReason
        ViewBag.ChildOk = ChildAllowed(showtime.Movie.AgeRating);
        ViewBag.ChildPerAdult = MaxChildrenPerAdult;
        var meU = await _users.GetUserAsync(User);
        ViewBag.U22Ok = TicketPricing.IsU22(meU?.BirthDate);
        ViewBag.HasBirth = meU?.BirthDate != null;
        ViewBag.MaxStudent = 0;                        // vé HSSV đã bỏ; giữ để trang chọn ghế bản cũ không lỗi
        return View(showtime);
    }

    // ================= Giữ ghế =================
    // seats = "mãGhế:LoạiVé,mãGhế:LoạiVé" (ví dụ "120:Adult,121:Child"). seatIds là dạng cũ, coi như vé người lớn.
    // ack = "1" khi người mua đã tick xác nhận điều kiện vé trẻ em.
    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("hold")]
    public async Task<IActionResult> Hold(int showtimeId, string? seats, string? seatIds, string? ack)
    {
        IActionResult Fail(string msg)
        {
            TempData["BookErr"] = msg;
            return RedirectToAction(nameof(Seats), new { id = showtimeId });
        }

        var gate = await RequirePhone($"/Booking/Seats/{showtimeId}");
        if (gate != null) return gate;

        var want = new Dictionary<int, string>();
        foreach (var part in (seats ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2 || !int.TryParse(kv[0], out var sid) || sid <= 0) continue;
            var code = kv[1].Trim();
            if (!TicketPricing.IsValid(code)) return Fail("Loại vé không hợp lệ.");
            want[sid] = code;
        }
        if (want.Count == 0)
        {
            foreach (var part in (seatIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, out var sid2) && sid2 > 0) want[sid2] = "Adult";
        }
        if (want.Count == 0) return Fail("Bạn chưa chọn ghế.");

        var ids = want.Keys.ToList();
        if (MaxSeats > 0 && ids.Count > MaxSeats) return Fail($"Mỗi lần đặt tối đa {MaxSeats} ghế.");

        var showtime = await _db.Showtimes.Include(s => s.Movie).FirstOrDefaultAsync(s => s.Id == showtimeId);
        if (showtime == null) return NotFound();

        var now = DateTime.UtcNow;
        if (showtime.StartTime <= now) return Fail("Suất chiếu này đã bắt đầu, không thể đặt thêm.");
        if (StopSalesMinutes > 0 && showtime.StartTime <= now.AddMinutes(StopSalesMinutes))
            return Fail($"Đã ngừng bán vé online cho suất này (ngừng trước giờ chiếu {StopSalesMinutes} phút). Bạn có thể mua vé tại quầy.");

        // ----- Luật vé trẻ em (kiểm tra ở server) -----
        int adults = want.Values.Count(x => x == "Adult" || x == "U22");
        int children = want.Values.Count(x => x == "Child");
        if (children > 0)
        {
            if (ack != "1")
                return Fail("Bạn cần tick xác nhận điều kiện áp dụng vé trẻ em.");
            if (!ChildAllowed(showtime.Movie.AgeRating))
                return Fail("Vé trẻ em chỉ áp dụng cho phim dành cho mọi lứa tuổi (P) hoặc trẻ em có người bảo hộ (K).");
            if (adults == 0)
                return Fail("Vé trẻ em phải mua kèm ít nhất một vé người lớn.");
            if (children > adults * MaxChildrenPerAdult)
                return Fail($"Mỗi người lớn đi cùng tối đa {MaxChildrenPerAdult} trẻ em.");
        }

        // Chỉ nhận ghế thuộc đúng phòng của suất chiếu (không tin dữ liệu từ trình duyệt)
        var rows = await _db.Seats.Where(s => ids.Contains(s.Id) && s.RoomId == showtime.RoomId).ToListAsync();
        if (rows.Count != ids.Count) return Fail("Ghế được chọn không hợp lệ.");

        // ----- Luật vé U22 (kiểm tra ở server, không tin dữ liệu từ trình duyệt) -----
        int u22 = want.Values.Count(x => x == "U22");
        if (u22 > 0)
        {
            var me = await _users.GetUserAsync(User);
            if (!TicketPricing.IsU22(me?.BirthDate))
                return Fail("Vé U22 chỉ dành cho khách từ 22 tuổi trở xuống. Hãy cập nhật ngày sinh trong Hồ sơ cá nhân.");
            if (u22 > TicketPricing.U22MaxPerOrder)
                return Fail($"Mỗi đơn chỉ được mua {TicketPricing.U22MaxPerOrder} vé U22.");
            if (rows.Any(r => want[r.Id] == "U22" && r.SeatType != "Standard"))
                return Fail("Vé U22 chỉ áp dụng cho ghế thường.");
        }

        var userId = CurrentUserId;

        // ----- Giới hạn số lần giữ ghế một suất trong một giờ (chống giữ ghế vô hạn bằng cách bấm lại) -----
        if (await _db.Bookings.AnyAsync(b => b.UserId == userId && b.Status == "WaitingPayment" && b.ExpiresAt > now)) return Fail("Bạn đang có một đơn chờ thanh toán. Hãy hoàn tất hoặc hủy đơn đó trước khi chọn ghế mới."); if (MaxHoldsPerHour > 0)
        {
            var since = now.AddMinutes(-60);
            var recent = await _db.Bookings.AsNoTracking()
                .Where(b => b.UserId == userId && b.ShowtimeId == showtimeId && b.CreatedAt > since)
                .OrderBy(b => b.CreatedAt)
                .Select(b => b.CreatedAt)
                .ToListAsync();
            if (recent.Count >= MaxHoldsPerHour)
            {
                int wait = (int)Math.Ceiling((recent[0].AddMinutes(60) - now).TotalMinutes);
                return Fail($"Bạn đã giữ ghế cho suất này {MaxHoldsPerHour} lần trong một giờ. Vui lòng thử lại sau khoảng {Math.Max(wait, 1)} phút.");
            }
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1. Mỗi tài khoản chỉ giữ ghế ở MỘT suất: nhả mọi đơn đang giữ của bạn (kể cả suất khác)
            var oldIds = await _db.Bookings
                .Where(b => b.UserId == userId && b.Status == "Pending")
                .Select(b => b.Id).ToListAsync();
            if (oldIds.Count > 0)
            {
                await _db.ShowtimeSeats.Where(x => oldIds.Contains(x.BookingId) && x.Status == "Held").ExecuteDeleteAsync();
                await _db.Bookings.Where(b => oldIds.Contains(b.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Cancelled"));
            }

            // 2. Xóa dòng giữ đã quá hạn của đúng các ghế này
            await _db.ShowtimeSeats
                .Where(x => x.ShowtimeId == showtimeId && ids.Contains(x.SeatId) && x.Status == "Held" && x.HoldExpiresAt <= now)
                .ExecuteDeleteAsync();

            // 3. Tạo đơn và giữ ghế. Giá tính ở server theo loại vé và ưu đãi giờ/ngày
            decimal PriceOf(Seat s) => TicketPricing.Price(showtime.EffectivePrice, showtime.BasePrice, s.SeatType, want[s.Id]);
            var booking = new Booking
            {
                UserId = userId,
                ShowtimeId = showtimeId,
                Status = "Pending",
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(HoldMinutes),
                Total = rows.Sum(PriceOf)
            };
            _db.Bookings.Add(booking);
            await _db.SaveChangesAsync();

            foreach (var s in rows)
            {
                _db.ShowtimeSeats.Add(new ShowtimeSeat
                {
                    ShowtimeId = showtimeId,
                    SeatId = s.Id,
                    Status = "Held",
                    TicketType = want[s.Id],
                    UserId = userId,
                    BookingId = booking.Id,
                    HoldExpiresAt = booking.ExpiresAt,
                    Price = PriceOf(s)
                });
            }
            await _db.SaveChangesAsync();   // trùng ghế sẽ ném lỗi ở đây
            await tx.CommitAsync();
            return RedirectToAction(nameof(Combos), new { id = booking.Id });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync();
            return Fail("Một hoặc nhiều ghế vừa được người khác chọn. Hãy chọn ghế khác.");
        }
    }

    // ================= Chọn bắp nước =================
    public async Task<IActionResult> Combos(int id)
    {
        var b = await LoadOwnedBooking(id);
        if (b == null) return NotFound();
        if (!IsActive(b)) return RedirectToAction(nameof(Review), new { id });

        ViewBag.Products = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive).OrderBy(p => p.SortOrder).ToListAsync();
        return View(b);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCombos(int id, string? items)
    {
        var userId = CurrentUserId;
        var b = await _db.Bookings.Include(x => x.Seats)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (b == null) return NotFound();

        if (!IsActive(b))
        {
            TempData["BookErr"] = "Đã hết thời gian giữ ghế.";
            return RedirectToAction(nameof(Review), new { id });
        }

        // Dữ liệu gửi lên có dạng "mãSảnPhẩm:sốLượng,mãSảnPhẩm:sốLượng"
        var wanted = new Dictionary<int, int>();
        foreach (var part in (items ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2 || !int.TryParse(kv[0], out var pid) || !int.TryParse(kv[1], out var qty)) continue;
            if (pid <= 0 || qty <= 0) continue;
            wanted[pid] = Math.Min(qty, MaxPerProduct);
        }

        // Giá lấy từ database, không tin giá do trình duyệt gửi
        var ids = wanted.Keys.ToList();
        var products = await _db.Products.Where(p => p.IsActive && ids.Contains(p.Id)).ToListAsync();

        await _db.BookingItems.Where(i => i.BookingId == id).ExecuteDeleteAsync();
        foreach (var p in products)
        {
            _db.BookingItems.Add(new BookingItem
            {
                BookingId = id,
                ProductId = p.Id,
                Quantity = wanted[p.Id],
                UnitPrice = p.Price
            });
        }
        b.Total = b.Seats.Sum(x => x.Price) + products.Sum(p => p.Price * wanted[p.Id]);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Review), new { id });
    }

    // ================= Xem đơn đang giữ ghế =================
    public async Task<IActionResult> Review(int id)
    {
        var b = await LoadOwnedBooking(id);
        if (b == null) return NotFound();

        if (b.Status == "Pending" && b.ExpiresAt <= DateTime.UtcNow)
        {
            await _db.ShowtimeSeats.Where(x => x.BookingId == b.Id && x.Status == "Held").ExecuteDeleteAsync();
            await _db.Bookings.Where(x => x.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Expired"));
            b.Status = "Expired";
        }
        return View(b);
    }

    // ================= Hủy giữ ghế =================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        var userId = CurrentUserId;
        var b = await _db.Bookings.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (b == null) return NotFound();

        if (b.Status == "Pending")
        {
            await _db.ShowtimeSeats.Where(x => x.BookingId == b.Id && x.Status == "Held").ExecuteDeleteAsync();
            b.Status = "Cancelled";
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Seats), new { id = b.ShowtimeId });
    }
}