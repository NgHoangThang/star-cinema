using System.Security.Cryptography;
using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CinemaApp.Controllers;

[Authorize]
public class PaymentController : Controller
{
    private static readonly string[] SimMethods = { "Card", "Wallet", "QR", "Transfer", "Cash" };
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // bỏ O, 0, I, 1 cho khỏi nhầm

    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly IConfiguration _cfg;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PaymentController> _log;

    public PaymentController(AppDbContext db, UserManager<AppUser> users, IConfiguration cfg,
                             IWebHostEnvironment env, ILogger<PaymentController> log)
    {
        _db = db;
        _users = users;
        _cfg = cfg;
        _env = env;
        _log = log;
    }

    private string CurrentUserId => _users.GetUserId(User) ?? "";

    // Nút giả lập chỉ chạy ở máy phát triển, không bao giờ bật được ở máy chủ thật (tránh ai cũng lấy vé miễn phí)
    private bool SimulationOn => _env.IsDevelopment();

    private int TransferMinutes => _cfg.GetValue("Payment:TransferMinutes", 15);
    private int CashMaxMinutes => _cfg.GetValue("Payment:CashMaxMinutes", 60);
    private int CashStopBeforeMinutes => _cfg.GetValue("Payment:CashStopBeforeMinutes", 30);
    private int CashMaxSeats => _cfg.GetValue("Payment:CashMaxSeats", 6);

    private string BankBin => _cfg["Payment:BankBin"] ?? "";
    private string AccountNo => _cfg["Payment:AccountNo"] ?? "";
    private string AccountName => _cfg["Payment:AccountName"] ?? "";
    private bool BankReady => BankBin.Length == 6 && AccountNo.Length > 0 && AccountName.Length > 0;
    private string BankName => BankBin == "970436" ? "Vietcombank" : "Ngân hàng (mã " + BankBin + ")";

    private static DateTime Earlier(DateTime a, DateTime b) => a < b ? a : b;
    private DateTime TransferDeadline(DateTime now, DateTime startUtc) => Earlier(now.AddMinutes(TransferMinutes), startUtc.AddMinutes(-5));
    private DateTime CashDeadline(DateTime now, DateTime startUtc) => Earlier(now.AddMinutes(CashMaxMinutes), startUtc.AddMinutes(-CashStopBeforeMinutes));
    private static bool TransferTooClose(DateTime now, DateTime startUtc) => startUtc <= now.AddMinutes(10);
    private bool CashTooClose(DateTime now, DateTime startUtc) => startUtc <= now.AddMinutes(CashStopBeforeMinutes + 10);

    private Task<Booking?> LoadOwned(int id)
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

    private static string NewCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[8];
        for (int i = 0; i < 8; i++) chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];   // 256 chia hết cho 32 nên không lệch
        return "SC" + new string(chars);
    }

    private async Task<string> UniqueCode(string? existing)
    {
        if (!string.IsNullOrEmpty(existing)) return existing;
        var code = NewCode();
        while (await _db.Bookings.AnyAsync(x => x.Code == code)) code = NewCode();
        return code;
    }

    // ----- Ưu đãi theo hạng thành viên: giảm trên tiền bắp nước -----
    private async Task<(decimal Discount, string? Tier)> MemberDiscountFor(string userId, decimal itemsTotal)
    {
        var (percent, tierName) = await MemberPerks.TierAsync(_db, userId);
        var discount = MemberPerks.ItemsDiscount(itemsTotal, percent);
        return (discount, discount > 0 ? tierName : null);
    }

    // ----- Mã giảm giá -----
    // Gọi bên trong transaction: khóa dòng voucher rồi kiểm tra lại, để hai người không cùng lấy lượt cuối.
    private async Task<(string? Error, decimal Discount, int? VoucherId)> ResolveVoucher(Booking b, decimal subtotal)
    {
        if (string.IsNullOrEmpty(b.VoucherCode)) return (null, 0m, null);

        var vid = await _db.Vouchers.Where(x => x.Code == b.VoucherCode)
            .Select(x => (int?)x.Id).FirstOrDefaultAsync();
        if (vid != null)
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Vouchers\" WHERE \"Id\" = {vid.Value} FOR UPDATE");

        var chk = await VoucherRules.CheckAsync(_db, b.VoucherCode, b.UserId, subtotal, b.Id);
        if (!chk.Ok) return (chk.Error, 0m, null);
        return (null, chk.Discount, chk.Voucher!.Id);
    }

    // Gỡ mã khỏi đơn và trả tổng tiền về giá chưa có mã (đã trừ ưu đãi hạng)
    private Task ClearVoucher(int bookingId, decimal subtotal, decimal memberDisc, string? memberTier) =>
        _db.Bookings.Where(x => x.Id == bookingId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.VoucherCode, (string?)null)
                .SetProperty(x => x.DiscountAmount, 0m)
                .SetProperty(x => x.MemberDiscount, memberDisc)
                .SetProperty(x => x.MemberTierName, memberTier)
                .SetProperty(x => x.Total, subtotal));

    // ================= Trang chọn cách thanh toán =================
    [HttpGet]
    public async Task<IActionResult> Pay(int id)
    {
        var b = await LoadOwned(id);
        if (b == null) return NotFound();
        if (b.Status == "Paid") return RedirectToAction(nameof(Done), new { id });
        if (b.Status == "WaitingPayment" && b.ExpiresAt > DateTime.UtcNow) return RedirectToAction(nameof(Wait), new { id });
        if (b.Status != "Pending" || b.ExpiresAt <= DateTime.UtcNow)
            return RedirectToAction("Review", "Booking", new { id });

        var now = DateTime.UtcNow;
        var start = b.Showtime.StartTime;
        var cashDeadline = CashDeadline(now, start);

        // Tiền ghế + bắp nước, trừ ưu đãi hạng, rồi kiểm tra lại mã giảm giá nếu đơn đang áp mã
        decimal seatsSum = b.Seats.Sum(s => s.Price);
        decimal itemsSum = b.Items.Sum(i => i.UnitPrice * i.Quantity);
        var (memberDisc, memberTier) = await MemberDiscountFor(b.UserId, itemsSum);
        decimal subtotal = seatsSum + itemsSum - memberDisc;
        decimal discount = 0;
        if (!string.IsNullOrEmpty(b.VoucherCode))
        {
            var chk = await VoucherRules.CheckAsync(_db, b.VoucherCode, b.UserId, subtotal, b.Id);
            if (chk.Ok)
            {
                discount = chk.Discount;
            }
            else
            {
                TempData["PayErr"] = "Mã giảm giá không còn dùng được: " + chk.Error + " Đơn đã quay về giá chưa có mã.";
                b.VoucherCode = null;
            }
        }
        decimal finalTotal = subtotal - discount;

        if (b.DiscountAmount != discount || b.MemberDiscount != memberDisc || b.MemberTierName != memberTier
            || b.Total != finalTotal || (b.VoucherCode == null && b.DiscountAmount != 0))
        {
            await _db.Bookings.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.VoucherCode, b.VoucherCode)
                .SetProperty(x => x.DiscountAmount, discount)
                .SetProperty(x => x.MemberDiscount, memberDisc)
                .SetProperty(x => x.MemberTierName, memberTier)
                .SetProperty(x => x.Total, finalTotal));
            b.DiscountAmount = discount;
            b.MemberDiscount = memberDisc;
            b.MemberTierName = memberTier;
            b.Total = finalTotal;
        }

        ViewBag.Subtotal = seatsSum + itemsSum;
        ViewBag.MemberDiscount = memberDisc;
        ViewBag.MemberTier = memberTier;
        ViewBag.Discount = discount;
        ViewBag.VoucherCode = b.VoucherCode;
        ViewBag.FinalTotal = finalTotal;

        ViewBag.SimOn = SimulationOn;
        ViewBag.SeatsOk = b.Seats.Count > 0 && b.Seats.All(s => s.Status == "Held" && s.HoldExpiresAt > now);
        ViewBag.BankReady = BankReady;
        ViewBag.TransferOk = BankReady && !TransferTooClose(now, start);
        ViewBag.TransferMinutes = TransferMinutes;
        var me = await _users.GetUserAsync(User);
        bool cashBlocked = me?.CashBlockedUntil > now;
        if (cashBlocked && TempData["PayErr"] == null)
            TempData["PayErr"] = "Tài khoản của bạn tạm không được chọn trả tiền mặt đến " + me!.CashBlockedUntil!.Value.AddHours(7).ToString("dd/MM/yyyy") + " vì đã nhiều lần giữ vé mà không đến thanh toán. Bạn vẫn có thể thanh toán bằng chuyển khoản QR.";
        ViewBag.CashOk = b.Seats.Count <= CashMaxSeats && !CashTooClose(now, start) && !cashBlocked;
        ViewBag.CashBlocked = cashBlocked;
        ViewBag.CashUntil = cashDeadline;
        ViewBag.CashMaxSeats = CashMaxSeats;
        return View(b);
    }

    // ================= Áp dụng mã giảm giá =================
    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("voucher")]
    public async Task<IActionResult> ApplyVoucher(int id, string? code)
    {
        var userId = CurrentUserId;
        var b = await _db.Bookings.Include(x => x.Seats).Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (b == null) return NotFound();

        if (b.Status != "Pending" || b.ExpiresAt <= DateTime.UtcNow)
        {
            TempData["BookErr"] = "Đã hết thời gian giữ ghế.";
            return RedirectToAction("Review", "Booking", new { id });
        }

        decimal itemsSum = b.Items.Sum(i => i.UnitPrice * i.Quantity);
        var (memberDisc, memberTier) = await MemberDiscountFor(userId, itemsSum);
        decimal subtotal = b.Seats.Sum(s => s.Price) + itemsSum - memberDisc;
        var chk = await VoucherRules.CheckAsync(_db, code, userId, subtotal, id);
        if (!chk.Ok)
        {
            TempData["PayErr"] = chk.Error;
            return RedirectToAction(nameof(Pay), new { id });
        }

        b.VoucherCode = chk.Voucher!.Code;
        b.DiscountAmount = chk.Discount;
        b.MemberDiscount = memberDisc;
        b.MemberTierName = memberTier;
        b.Total = subtotal - chk.Discount;
        await _db.SaveChangesAsync();

        TempData["PayOk"] = $"Đã áp dụng mã {b.VoucherCode}: giảm {chk.Discount:N0} đ.";
        return RedirectToAction(nameof(Pay), new { id });
    }

    // ================= Gỡ mã giảm giá =================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveVoucher(int id)
    {
        var userId = CurrentUserId;
        var b = await _db.Bookings.Include(x => x.Seats).Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (b == null) return NotFound();

        if (b.Status == "Pending" && b.ExpiresAt > DateTime.UtcNow)
        {
            decimal itemsSum = b.Items.Sum(i => i.UnitPrice * i.Quantity);
            var (memberDisc, memberTier) = await MemberDiscountFor(userId, itemsSum);
            decimal subtotal = b.Seats.Sum(s => s.Price) + itemsSum - memberDisc;
            b.VoucherCode = null;
            b.DiscountAmount = 0;
            b.MemberDiscount = memberDisc;
            b.MemberTierName = memberTier;
            b.Total = subtotal;
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Pay), new { id });
    }

    // ================= Chọn chuyển khoản QR hoặc tiền mặt =================
    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("hold")]
    public async Task<IActionResult> Choose(int id, string? method, string? agree)
    {
        var userId = CurrentUserId;
        var b = await _db.Bookings.Include(x => x.Seats).Include(x => x.Showtime)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (b == null) return NotFound();
        if (b.Status == "Paid") return RedirectToAction(nameof(Done), new { id });

        var now = DateTime.UtcNow;
        if (b.Status == "WaitingPayment" && b.ExpiresAt > now) return RedirectToAction(nameof(Wait), new { id });

        IActionResult Back(string msg)
        {
            TempData["PayErr"] = msg;
            return RedirectToAction(nameof(Pay), new { id });
        }

        if (b.Status != "Pending" || b.ExpiresAt <= now)
        {
            TempData["BookErr"] = "Đã hết thời gian giữ ghế.";
            return RedirectToAction("Review", "Booking", new { id });
        }
        if (method != "Transfer" && method != "Cash") return Back("Vui lòng chọn cách thanh toán.");
        if (agree != "1") return Back("Bạn cần đồng ý với chính sách vé trước khi tiếp tục.");
        if (b.Seats.Count == 0 || b.Seats.Any(s => s.Status != "Held" || s.HoldExpiresAt <= now))
            return Back("Ghế của bạn không còn được giữ. Vui lòng chọn lại ghế.");

        // Mỗi tài khoản chỉ có một đơn đang chờ thanh toán (chống giữ ghế hàng loạt)
        if (await _db.Bookings.AnyAsync(x => x.UserId == userId && x.Id != id && x.Status == "WaitingPayment" && x.ExpiresAt > now))
            return Back("Bạn đang có một đơn chờ thanh toán khác. Hãy hoàn tất hoặc hủy đơn đó trước.");

        DateTime deadline;
        if (method == "Transfer")
        {
            if (!BankReady) return Back("Hệ thống chưa cấu hình tài khoản nhận tiền.");
            if (TransferTooClose(now, b.Showtime.StartTime)) return Back("Suất chiếu quá gần, không thể chọn chuyển khoản.");
            deadline = TransferDeadline(now, b.Showtime.StartTime);
        }
        else
        {
            var me = await _users.GetUserAsync(User);
            if (me?.CashBlockedUntil > now)
                return Back("Tài khoản của bạn tạm không được chọn trả tiền mặt đến " + me.CashBlockedUntil.Value.AddHours(7).ToString("dd/MM/yyyy") + ". Vui lòng thanh toán bằng chuyển khoản QR.");
            if (b.Seats.Count > CashMaxSeats) return Back($"Trả tiền mặt tối đa {CashMaxSeats} ghế mỗi đơn.");
            if (CashTooClose(now, b.Showtime.StartTime)) return Back("Suất chiếu quá gần, không thể giữ vé trả tiền mặt.");
            deadline = CashDeadline(now, b.Showtime.StartTime);
        }

        var code = await UniqueCode(b.Code);
        int seatCount = b.Seats.Count;

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1. Kéo dài thời gian giữ ghế đến hạn thanh toán (chỉ ghế còn đang được giữ)
            int moved = await _db.ShowtimeSeats
                .Where(x => x.BookingId == id && x.Status == "Held" && x.HoldExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.HoldExpiresAt, deadline));
            if (moved != seatCount)
            {
                await tx.RollbackAsync();
                return Back("Ghế của bạn không còn được giữ. Vui lòng chọn lại ghế.");
            }

            // 2. Tính lại tổng tiền từ database, trừ ưu đãi hạng, rồi kiểm tra lại mã giảm giá
            decimal seatsTotal = await _db.ShowtimeSeats.Where(x => x.BookingId == id).SumAsync(x => x.Price);
            decimal itemsTotal = await _db.BookingItems.Where(x => x.BookingId == id).SumAsync(x => x.UnitPrice * x.Quantity);
            var (memberDisc, memberTier) = await MemberDiscountFor(userId, itemsTotal);
            decimal subtotal = seatsTotal + itemsTotal - memberDisc;

            var (vErr, discount, voucherId) = await ResolveVoucher(b, subtotal);
            if (vErr != null)
            {
                await tx.RollbackAsync();
                await ClearVoucher(id, subtotal, memberDisc, memberTier);
                return Back("Mã giảm giá không còn dùng được: " + vErr + " Đơn đã quay về giá chưa có mã.");
            }

            // 3. Chuyển đơn sang chờ thanh toán, cấp mã đặt vé
            int rows = await _db.Bookings
                .Where(x => x.Id == id && x.UserId == userId && x.Status == "Pending")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "WaitingPayment")
                    .SetProperty(x => x.PaymentMethod, (string?)method)
                    .SetProperty(x => x.Code, (string?)code)
                    .SetProperty(x => x.ExpiresAt, deadline)
                    .SetProperty(x => x.DiscountAmount, discount)
                    .SetProperty(x => x.MemberDiscount, memberDisc)
                    .SetProperty(x => x.MemberTierName, memberTier)
                    .SetProperty(x => x.Total, subtotal - discount));
            if (rows != 1)
            {
                await tx.RollbackAsync();
                return Back("Đơn không còn ở trạng thái chờ thanh toán.");
            }

            // 4. Ghi lượt dùng mã (nếu có)
            await _db.VoucherUses.Where(u => u.BookingId == id).ExecuteDeleteAsync();
            if (voucherId != null)
            {
                _db.VoucherUses.Add(new VoucherUse { VoucherId = voucherId.Value, UserId = userId, BookingId = id, Amount = discount });
                await _db.SaveChangesAsync();
            }

            await tx.CommitAsync();
            return RedirectToAction(nameof(Wait), new { id });
        }
        catch (Exception ex) when (ex is DbUpdateException || ex is PostgresException)
        {
            _log.LogError(ex, "Chọn cách thanh toán thất bại cho đơn {BookingId}", id);
            try { await tx.RollbackAsync(); } catch { }
            return Back("Không hoàn tất được. Vui lòng thử lại.");
        }
    }

    // ================= Trang chờ xác nhận (hiện mã QR hoặc mã đặt vé) =================
    [HttpGet]
    public async Task<IActionResult> Wait(int id)
    {
        var b = await LoadOwned(id);
        if (b == null) return NotFound();
        if (b.Status == "Paid") return RedirectToAction(nameof(Done), new { id });

        var now = DateTime.UtcNow;
        if (b.Status == "WaitingPayment" && b.ExpiresAt <= now)
            b.Status = "Expired";   // chỉ để hiển thị: việc hết hạn thật (có ghi nhớ ghế) do dịch vụ dọn ghế làm
        bool wasWaiting = b.Status == "WaitingPayment" || (b.Status == "Expired" && b.PaymentMethod != null);
        if (!wasWaiting) return RedirectToAction("Review", "Booking", new { id });

        // Số tiền phải trả = Total đã chốt lúc chọn cách thanh toán (đã trừ ưu đãi hạng và mã giảm giá)
        decimal amount = b.Total;
        ViewBag.Amount = amount;
        ViewBag.Discount = b.DiscountAmount;
        ViewBag.VoucherCode = b.VoucherCode;
        ViewBag.MemberDiscount = b.MemberDiscount;
        ViewBag.MemberTier = b.MemberTierName;
        ViewBag.SimOn = SimulationOn;
        ViewBag.BankName = BankName;
        ViewBag.AccountNo = AccountNo;
        ViewBag.AccountName = AccountName;

        if (b.Status == "WaitingPayment" && b.PaymentMethod == "Transfer" && BankReady && !string.IsNullOrEmpty(b.Code))
        {
            var payload = VietQr.BuildPayload(BankBin, AccountNo, (long)amount, b.Code);
            ViewBag.QrUrl = VietQr.QrDataUrl(payload);
        }
        return View(b);
    }

    // Trang chờ hỏi trạng thái mỗi vài giây để tự chuyển khi đơn đã được duyệt
    [HttpGet]
    public async Task<IActionResult> Status(int id)
    {
        var userId = CurrentUserId;
        var st = await _db.Bookings.AsNoTracking()
            .Where(x => x.Id == id && x.UserId == userId)
            .Select(x => x.Status).FirstOrDefaultAsync();
        if (st == null) return NotFound();
        return Json(new { status = st });
    }

    // ================= Hủy đơn đang chờ thanh toán =================
    [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelWaiting(int id, string? notPaid)
    {
        var userId = CurrentUserId;
        var b = await _db.Bookings.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (b == null) return NotFound();

        // Đơn chuyển khoản: chỉ hủy khi khách xác nhận CHƯA chuyển tiền (hộp hỏi ở trang chờ gửi notPaid=1)
        if (b.Status == "WaitingPayment" && b.PaymentMethod == "Transfer" && notPaid != "1")
        {
            TempData["PayErr"] = "Hãy xác nhận bạn chưa chuyển khoản trước khi hủy đơn. Nếu đã chuyển tiền, hãy chờ quản trị viên xác nhận.";
            return RedirectToAction(nameof(Wait), new { id });
        }

        if (b.Status == "WaitingPayment")
        {
            if (b.PaymentMethod == "Transfer")
            {
                // Giống đơn hết hạn: ghi nhớ ghế rồi cho hết hạn ngay, để admin vẫn "Duyệt lại" được (trong 2 giờ) nếu tiền về trễ
                var snap = await _db.ShowtimeSeats.AsNoTracking().Where(x => x.BookingId == id)
                    .Select(x => new SeatSnap(x.SeatId, x.TicketType, x.Price)).ToListAsync();
                b.ExpiredSeats = System.Text.Json.JsonSerializer.Serialize(snap);
                b.ExpiresAt = DateTime.UtcNow;
                b.Status = "Expired";
            }
            else
            {
                b.Status = "Cancelled";
            }
            await _db.ShowtimeSeats.Where(x => x.BookingId == id && x.Status == "Held").ExecuteDeleteAsync();
            await _db.SaveChangesAsync();
        }
        return RedirectToAction("Seats", "Booking", new { id = b.ShowtimeId });
    }

    // ================= Thanh toán giả lập (chỉ máy phát triển) =================
    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("hold")]
    public async Task<IActionResult> Confirm(int id, string? method, string? outcome, string? agree)
    {
        if (!SimulationOn) return NotFound();

        var userId = CurrentUserId;
        var b = await _db.Bookings.Include(x => x.Seats)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (b == null) return NotFound();

        // Đã thanh toán rồi: bấm lần hai không trả tiền lại
        if (b.Status == "Paid") return RedirectToAction(nameof(Done), new { id });

        bool waiting = b.Status == "WaitingPayment";
        IActionResult Back(string msg)
        {
            TempData["PayErr"] = msg;
            return waiting ? RedirectToAction(nameof(Wait), new { id }) : RedirectToAction(nameof(Pay), new { id });
        }

        var now = DateTime.UtcNow;
        if ((b.Status != "Pending" && !waiting) || b.ExpiresAt <= now)
        {
            TempData["BookErr"] = "Đã hết thời gian giữ ghế.";
            return RedirectToAction("Review", "Booking", new { id });
        }

        if (method == null || !SimMethods.Contains(method)) return Back("Vui lòng chọn phương thức thanh toán.");
        if (agree != "1") return Back("Bạn cần đồng ý với chính sách vé trước khi thanh toán.");
        if (outcome == "fail")
            return Back("Thanh toán thất bại (giả lập). Ghế vẫn được giữ đến hết giờ, bạn có thể thử lại.");
        if (outcome != "success") return Back("Yêu cầu không hợp lệ.");

        var code = await UniqueCode(b.Code);
        int seatCount = b.Seats.Count;

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1. Giành ghế: chuyển Held thành Sold, chỉ khi ghế vẫn còn được giữ
            int sold = await _db.ShowtimeSeats
                .Where(x => x.BookingId == id && x.Status == "Held" && x.HoldExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Sold"));
            if (seatCount == 0 || sold != seatCount)
            {
                await tx.RollbackAsync();
                if (await _db.Bookings.AsNoTracking().AnyAsync(x => x.Id == id && x.Status == "Paid"))
                    return RedirectToAction(nameof(Done), new { id });
                return Back("Ghế của bạn không còn được giữ. Vui lòng chọn lại ghế.");
            }

            // 2. Tính lại tổng tiền từ database (không tin con số đã lưu), trừ ưu đãi hạng, rồi kiểm tra lại mã giảm giá
            decimal seatsTotal = await _db.ShowtimeSeats.Where(x => x.BookingId == id).SumAsync(x => x.Price);
            decimal itemsTotal = await _db.BookingItems.Where(x => x.BookingId == id).SumAsync(x => x.UnitPrice * x.Quantity);
            var (memberDisc, memberTier) = await MemberDiscountFor(userId, itemsTotal);
            decimal subtotal = seatsTotal + itemsTotal - memberDisc;

            var (vErr, discount, voucherId) = await ResolveVoucher(b, subtotal);
            if (vErr != null)
            {
                await tx.RollbackAsync();
                await ClearVoucher(id, subtotal, memberDisc, memberTier);
                return Back("Mã giảm giá không còn dùng được: " + vErr + " Đơn đã quay về giá chưa có mã.");
            }

            // 3. Chốt đơn
            int rows = await _db.Bookings
                .Where(x => x.Id == id && x.UserId == userId && (x.Status == "Pending" || x.Status == "WaitingPayment"))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "Paid")
                    .SetProperty(x => x.PaidAt, (DateTime?)now)
                    .SetProperty(x => x.PaymentMethod, (string?)method)
                    .SetProperty(x => x.Code, (string?)code)
                    .SetProperty(x => x.DiscountAmount, discount)
                    .SetProperty(x => x.MemberDiscount, memberDisc)
                    .SetProperty(x => x.MemberTierName, memberTier)
                    .SetProperty(x => x.Total, subtotal - discount));
            if (rows != 1)
            {
                await tx.RollbackAsync();
                return Back("Đơn không còn ở trạng thái chờ thanh toán.");
            }

            // 4. Ghi lượt dùng mã (nếu có)
            await _db.VoucherUses.Where(u => u.BookingId == id).ExecuteDeleteAsync();
            if (voucherId != null)
            {
                _db.VoucherUses.Add(new VoucherUse { VoucherId = voucherId.Value, UserId = userId, BookingId = id, Amount = discount });
                await _db.SaveChangesAsync();
            }

            await tx.CommitAsync();
            return RedirectToAction(nameof(Done), new { id });
        }
        catch (Exception ex) when (ex is DbUpdateException || ex is PostgresException)
        {
            _log.LogError(ex, "Thanh toán thất bại cho đơn {BookingId}", id);
            try { await tx.RollbackAsync(); } catch { }
            return Back("Không hoàn tất được thanh toán. Vui lòng thử lại.");
        }
    }

    // ================= Kết quả: đã thanh toán =================
    [HttpGet]
    public async Task<IActionResult> Done(int id)
    {
        var b = await LoadOwned(id);
        if (b == null) return NotFound();
        if (b.Status != "Paid") return RedirectToAction("Review", "Booking", new { id });
        return View(b);
    }
}