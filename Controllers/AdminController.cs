using System.Globalization;
using System.Text.RegularExpressions;
using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CinemaApp.Controllers;

public class AdminOrderRow
{
    public int Id { get; set; }
    public string? Code { get; set; }
    public string? Email { get; set; }
    public string Movie { get; set; } = "";
    public string Cinema { get; set; } = "";
    public DateTime StartTime { get; set; }
    public int SeatCount { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = "";
    public string? Method { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? BankRef { get; set; }
    public DateTime? CancelRequestedAt { get; set; }
    public string? CancelReason { get; set; }
}

public class VoucherRow
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string DiscountType { get; set; } = "Percent";
    public decimal Value { get; set; }
    public decimal MaxDiscount { get; set; }
    public decimal MinOrder { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public int MaxUses { get; set; }
    public int MaxUsesPerUser { get; set; }
    public bool IsActive { get; set; }
    public int Used { get; set; }
    public bool IsPublic { get; set; }
    public bool IsPrivate { get; set; }   // mã riêng của một tài khoản (quà sinh nhật...)
}

public class DashDay
{
    public string Label { get; set; } = "";     // 18/10
    public string Weekday { get; set; } = "";   // CN, T2...
    public decimal Revenue { get; set; }
    public int Orders { get; set; }
    public int Tickets { get; set; }
}

public class DashMovie
{
    public string Title { get; set; } = "";
    public int Tickets { get; set; }
}

public class AdminDashboard
{
    public decimal RevenueToday { get; set; }
    public decimal Revenue7 { get; set; }
    public decimal RevenueTotal { get; set; }
    public int OrdersToday { get; set; }
    public int TicketsToday { get; set; }
    public int Waiting { get; set; }
    public int Pending { get; set; }
    public int PaidTotal { get; set; }
    public decimal Refund7 { get; set; }       // tiền đã hoàn trong 7 ngày qua
    public decimal RefundTotal { get; set; }   // tổng tiền đã hoàn từ trước đến nay
    public int RefundCount { get; set; }
    public List<DashDay> Days { get; set; } = new();
    public List<DashMovie> TopMovies { get; set; } = new();
    public List<AdminOrderRow> Recent { get; set; } = new();
}

[Authorize]
public class AdminController : Controller
{
    private static readonly string[] Statuses = { "Pending", "WaitingPayment", "Paid", "Expired", "Cancelled", "Refunded" };
    private static readonly string[] Weekdays = { "CN", "T2", "T3", "T4", "T5", "T6", "T7" };

    private readonly AppDbContext _db;
    private readonly ILogger<AdminController> _log;

    public AdminController(AppDbContext db, ILogger<AdminController> log)
    {
        _db = db;
        _log = log;
    }

    // Người không phải admin nhận 404, không biết trang này có tồn tại
    public override void OnActionExecuting(ActionExecutingContext context)
    {
                if (!User.IsInRole(AdminSeeder.RoleName))
            context.Result = User.IsInRole(AdminSeeder.StaffRoleName) ? Redirect("/Admin/Scan") : NotFound();
        base.OnActionExecuting(context);
    }

    // ================= Tổng quan =================
    public async Task<IActionResult> Index()
    {
        var now = DateTime.UtcNow;
        var today = now.AddHours(7).Date;                 // 0 giờ hôm nay theo giờ Việt Nam
        var startOfDay = today.AddHours(-7);              // đổi sang UTC
        var start7 = today.AddDays(-6).AddHours(-7);      // đầu ngày cách đây 6 ngày (đủ 7 ngày kể cả hôm nay)

        var paid7 = await _db.Bookings.AsNoTracking()
            .Where(b => b.Status == "Paid" && b.PaidAt >= start7)
            .Select(b => new { b.PaidAt, b.Total, Seats = b.Seats.Count })
            .ToListAsync();

        var days = new List<DashDay>();
        for (int i = 6; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            var from = d.AddHours(-7);
            var to = from.AddDays(1);
            var rows = paid7.Where(x => x.PaidAt >= from && x.PaidAt < to).ToList();
            days.Add(new DashDay
            {
                Label = d.ToString("dd/MM"),
                Weekday = Weekdays[(int)d.DayOfWeek],
                Revenue = rows.Sum(x => x.Total),
                Orders = rows.Count,
                Tickets = rows.Sum(x => x.Seats)
            });
        }

        var top = await _db.ShowtimeSeats.AsNoTracking()
            .Where(x => x.Status == "Sold" && x.Booking.Status == "Paid" && x.Booking.PaidAt >= start7)
            .GroupBy(x => x.Showtime.Movie.Title)
            .Select(g => new DashMovie { Title = g.Key, Tickets = g.Count() })
            .OrderByDescending(x => x.Tickets).ThenBy(x => x.Title)
            .Take(5).ToListAsync();

        // Hoàn tiền: đơn đã hoàn không còn tính vào doanh thu (thực thu), nhưng vẫn báo riêng để đối chiếu
        var refund7 = (await _db.Bookings.AsNoTracking()
            .Where(b => b.Status == "Refunded" && b.RefundedAt >= start7).SumAsync(b => (decimal?)b.Total)) ?? 0m;
        var refundTotal = (await _db.Bookings.AsNoTracking()
            .Where(b => b.Status == "Refunded").SumAsync(b => (decimal?)b.Total)) ?? 0m;
        var refundCount = await _db.Bookings.CountAsync(b => b.Status == "Refunded");

        var recent = await (from b in _db.Bookings.AsNoTracking()
                            join u in _db.Users on b.UserId equals u.Id
                            orderby b.Id descending
                            select new AdminOrderRow
                            {
                                Id = b.Id,
                                Code = b.Code,
                                Email = u.Email,
                                Movie = b.Showtime.Movie.Title,
                                Cinema = b.Showtime.Room.Cinema.Name,
                                StartTime = b.Showtime.StartTime,
                                SeatCount = b.Seats.Count,
                                Total = b.Total,
                                Status = b.Status,
                                Method = b.PaymentMethod,
                                CreatedAt = b.CreatedAt,
                                ExpiresAt = b.ExpiresAt
                            }).Take(6).ToListAsync();

        var vm = new AdminDashboard
        {
            RevenueToday = days[6].Revenue,
            Revenue7 = days.Sum(x => x.Revenue),
            RevenueTotal = (await _db.Bookings.Where(b => b.Status == "Paid").SumAsync(b => (decimal?)b.Total)) ?? 0m,
            OrdersToday = days[6].Orders,
            TicketsToday = days[6].Tickets,
            Waiting = await _db.Bookings.CountAsync(b => b.Status == "WaitingPayment" && b.ExpiresAt > now),
            Pending = await _db.Bookings.CountAsync(b => b.Status == "Pending" && b.ExpiresAt > now),
            PaidTotal = await _db.Bookings.CountAsync(b => b.Status == "Paid"),
            Refund7 = refund7,
            RefundTotal = refundTotal,
            RefundCount = refundCount,
            Days = days,
            TopMovies = top,
            Recent = recent
        };
        return View(vm);
    }

    private const int OrdersPageSize = 30;

    public async Task<IActionResult> Orders(string? status, string? code, int page = 1)
    {
        var q = _db.Bookings.AsNoTracking().AsQueryable();
        if (status == "CancelRequested") q = q.Where(b => b.Status == "Paid" && b.CancelRequestedAt != null);
        else if (!string.IsNullOrEmpty(status) && Statuses.Contains(status)) q = q.Where(b => b.Status == status);
        if (!string.IsNullOrWhiteSpace(code))
        {
            var t = code.Trim();
            if (t.Contains('@'))
            {
                // Có dấu @ thì tìm theo email khách
                var up = t.ToUpperInvariant();
                q = q.Where(b => _db.Users.Any(u => u.Id == b.UserId && u.NormalizedEmail != null && u.NormalizedEmail.Contains(up)));
            }
            else
            {
                var c = t.ToUpper();
                q = q.Where(b => b.Code == c);
            }
        }

        int total = await q.CountAsync();
        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)OrdersPageSize));
        if (page < 1) page = 1;
        if (page > pages) page = pages;

        var rows = await (from b in q
                          join u in _db.Users on b.UserId equals u.Id
                          orderby b.Id descending
                          select new AdminOrderRow
                          {
                              Id = b.Id,
                              Code = b.Code,
                              Email = u.Email,
                              Movie = b.Showtime.Movie.Title,
                              Cinema = b.Showtime.Room.Cinema.Name,
                              StartTime = b.Showtime.StartTime,
                              SeatCount = b.Seats.Count,
                              Total = b.Total,
                              Status = b.Status,
                              Method = b.PaymentMethod,
                              CreatedAt = b.CreatedAt,
                              ExpiresAt = b.ExpiresAt,
                              ApprovedBy = b.ApprovedByEmail,
                              ApprovedAt = b.ApprovedAt,
                              BankRef = b.BankRef,
                              CancelRequestedAt = b.CancelRequestedAt,
                              CancelReason = b.CancelRequestReason
                          }).Skip((page - 1) * OrdersPageSize).Take(OrdersPageSize).ToListAsync();

        ViewBag.Status = status;
        ViewBag.Code = code;
        ViewBag.CancelPending = await _db.Bookings.CountAsync(b => b.Status == "Paid" && b.CancelRequestedAt != null);
        ViewBag.Page = page;
        ViewBag.Pages = pages;
        ViewBag.Total = total;
        return View(rows);
    }

    // ================= Nhật ký thao tác của quản trị viên =================
    public async Task<IActionResult> Audit(int page = 1)
    {
        const int size = 50;
        int total = await _db.AdminAuditLogs.CountAsync();
        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        if (page < 1) page = 1;
        if (page > pages) page = pages;

        var rows = await _db.AdminAuditLogs.AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync();

        ViewBag.Page = page;
        ViewBag.Pages = pages;
        ViewBag.Total = total;
        return View(rows);
    }

    // ================= Duyệt: đã nhận tiền =================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, decimal? received, string? bankRef)
    {
        IActionResult Back(string msg)
        {
            TempData["AdminMsg"] = msg;
            return RedirectToAction(nameof(Orders), new { status = "WaitingPayment" });
        }

        var now = DateTime.UtcNow;
        var b = await _db.Bookings.AsNoTracking().Include(x => x.Seats).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();

        if (b.Status == "Paid") return Back($"Đơn {b.Code} đã được duyệt trước đó.");
        if (b.Status != "WaitingPayment") return Back("Đơn này không ở trạng thái chờ thanh toán.");
        if (b.ExpiresAt <= now)
            return Back("Đơn đã quá hạn và ghế đã được nhả nên không duyệt được. Nếu khách đã chuyển tiền thì hoàn tiền thủ công.");

        int seatCount = b.Seats.Count;
        if (seatCount == 0) return Back("Đơn không có ghế.");

        var admin = User.Identity?.Name ?? "";
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1. Chuyển ghế từ giữ sang bán, chỉ khi ghế vẫn còn được giữ
            int sold = await _db.ShowtimeSeats
                .Where(x => x.BookingId == id && x.Status == "Held" && x.HoldExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Sold"));
            if (sold != seatCount)
            {
                await tx.RollbackAsync();
                return Back("Ghế của đơn không còn được giữ, không duyệt được.");
            }

            // 2. Tính lại tổng tiền từ database, trừ số tiền giảm đã chốt lúc khách chọn cách thanh toán
            decimal seatsTotal = await _db.ShowtimeSeats.Where(x => x.BookingId == id).SumAsync(x => x.Price);
            decimal itemsTotal = await _db.BookingItems.Where(x => x.BookingId == id).SumAsync(x => x.UnitPrice * x.Quantity);
            decimal finalTotal = seatsTotal + itemsTotal - b.MemberDiscount - b.DiscountAmount;
            if (finalTotal < 0) finalTotal = 0;

            // 2b. Đối soát: tiền thực nhận phải khớp số cần thu; chuyển khoản phải có mã giao dịch ngân hàng chưa dùng cho đơn khác
            bool isTransfer = b.PaymentMethod == "Transfer" || b.PaymentMethod == "QR";
            bankRef = (bankRef ?? "").Trim().ToUpperInvariant();
            if (received == null || received.Value != finalTotal)
            {
                await tx.RollbackAsync();
                string got = received == null ? "chưa nhập" : received.Value.ToString("N0") + " đ";
                return Back($"Số tiền thực nhận ({got}) không khớp số cần thu ({finalTotal:N0} đ). Không duyệt. Nếu khách chuyển thiếu/thừa, hãy liên hệ khách trước.");
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

            // 3. Chốt đơn, ghi người duyệt và giờ duyệt
            int rows = await _db.Bookings
                .Where(x => x.Id == id && x.Status == "WaitingPayment")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "Paid")
                    .SetProperty(x => x.PaidAt, (DateTime?)now)
                    .SetProperty(x => x.ApprovedAt, (DateTime?)now)
                    .SetProperty(x => x.ApprovedByEmail, (string?)admin)
                    .SetProperty(x => x.Total, finalTotal)
                    .SetProperty(x => x.ReceivedAmount, (decimal?)received)
                    .SetProperty(x => x.BankRef, isTransfer ? bankRef : (string?)null));
            _db.AdminAuditLogs.Add(new AdminAuditLog { At = now, AdminEmail = admin, Action = "Approve", Target = "Booking:" + id, Detail = $"{b.Code}; nhận {received:N0} đ; mã GD {(isTransfer ? bankRef : "tiền mặt")}" });
            await _db.SaveChangesAsync();
            if (rows != 1)
            {
                await tx.RollbackAsync();
                return Back("Đơn vừa được xử lý bởi người khác.");
            }

            await tx.CommitAsync();
            _log.LogInformation("Admin {Admin} duyệt đơn {BookingId} ({Code})", admin, id, b.Code);
            return Back($"Đã duyệt đơn {b.Code}: {finalTotal:N0} đ.");
        }
        catch (Exception ex) when (ex is DbUpdateException || ex is PostgresException)
        {
            _log.LogError(ex, "Duyệt đơn {BookingId} thất bại", id);
            try { await tx.RollbackAsync(); } catch { }
            return Back("Không duyệt được. Vui lòng thử lại.");
        }
    }

    // ================= Hủy đơn đang chờ thanh toán =================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id)
    {
        var b = await _db.Bookings.FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();

        if (b.Status == "WaitingPayment")
        {
            await _db.ShowtimeSeats.Where(x => x.BookingId == id && x.Status == "Held").ExecuteDeleteAsync();
            b.Status = "Cancelled";
            await _db.SaveChangesAsync();
            _log.LogInformation("Admin {Admin} hủy đơn {BookingId} ({Code})", User.Identity?.Name, id, b.Code);
            _db.AdminAuditLogs.Add(new AdminAuditLog { At = DateTime.UtcNow, AdminEmail = User.Identity?.Name ?? "", Action = "Reject", Target = "Booking:" + id, Detail = b.Code ?? "" });
            await _db.SaveChangesAsync();
            TempData["AdminMsg"] = $"Đã hủy đơn {b.Code}, ghế đã được nhả.";
        }
        else
        {
            TempData["AdminMsg"] = "Chỉ hủy được đơn đang chờ thanh toán.";
        }
        return RedirectToAction(nameof(Orders), new { status = "WaitingPayment" });
    }

    // ================= Mã giảm giá: danh sách =================
    public async Task<IActionResult> Vouchers()
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Vouchers.AsNoTracking()
            .OrderByDescending(v => v.Id)
            .Select(v => new VoucherRow
            {
                Id = v.Id,
                Code = v.Code,
                Description = v.Description,
                DiscountType = v.DiscountType,
                Value = v.Value,
                MaxDiscount = v.MaxDiscount,
                MinOrder = v.MinOrder,
                StartsAt = v.StartsAt,
                EndsAt = v.EndsAt,
                MaxUses = v.MaxUses,
                MaxUsesPerUser = v.MaxUsesPerUser,
                IsActive = v.IsActive,
                IsPublic = v.IsPublic,
                IsPrivate = v.OwnerUserId != null,
                Used = v.Uses.Count(u => u.Booking.Status == "Paid"
                                      || (u.Booking.Status == "WaitingPayment" && u.Booking.ExpiresAt > now))
            }).ToListAsync();
        return View(rows);
    }

    // ================= Mã giảm giá: tạo mới =================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VoucherCreate(string? code, string? description, string? type,
        decimal value, decimal maxDiscount, decimal minOrder, string? startDate, string? endDate,
        int maxUses, int maxUsesPerUser)
    {
        IActionResult Back(string msg)
        {
            TempData["AdminMsg"] = msg;
            return RedirectToAction(nameof(Vouchers));
        }

        var c = VoucherRules.Normalize(code);
        if (!Regex.IsMatch(c, "^[A-Z0-9]{3,20}$")) return Back("Không tạo được: mã chỉ gồm chữ và số, dài 3 đến 20 ký tự.");
        if (await _db.Vouchers.AnyAsync(v => v.Code == c)) return Back($"Không tạo được: mã {c} đã tồn tại.");

        if (type != "Percent" && type != "Fixed") return Back("Không tạo được: loại giảm giá không hợp lệ.");
        if (type == "Percent")
        {
            if (value < 1 || value > 100) return Back("Không tạo được: giảm theo phần trăm phải từ 1 đến 100.");
        }
        else
        {
            if (value < 1000 || value > 10_000_000) return Back("Không tạo được: giảm số tiền cố định phải từ 1,000 đến 10,000,000 đ.");
            maxDiscount = 0;
        }
        if (maxDiscount < 0 || minOrder < 0 || maxUses < 0 || maxUsesPerUser < 0)
            return Back("Không tạo được: các số không được âm.");

        // Rào chắn doanh thu: mã giá trị lớn phải có giới hạn, tránh lỡ tay tạo mã cho giảm vô hạn
        if (type == "Percent" && value > 20 && maxDiscount <= 0)
            return Back("Không tạo được: mã giảm trên 20% phải đặt mức giảm tối đa (đ).");
        bool large = (type == "Percent" && value > 30) || (type == "Fixed" && value >= 100_000);
        if (large && (maxUses <= 0 || maxUsesPerUser <= 0))
            return Back("Không tạo được: mã giảm trên 30% hoặc từ 100,000 đ phải đặt số lượt tối đa và số lượt mỗi tài khoản (không để 0).");

        if (!DateTime.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sd) ||
            !DateTime.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ed))
            return Back("Không tạo được: ngày bắt đầu hoặc ngày kết thúc không hợp lệ.");

        // Ngày nhập là giờ Việt Nam: bắt đầu từ 0 giờ ngày đầu, hết hạn cuối ngày cuối; đổi sang UTC để lưu
        var startsAt = DateTime.SpecifyKind(sd.Date.AddHours(-7), DateTimeKind.Utc);
        var endsAt = DateTime.SpecifyKind(ed.Date.AddDays(1).AddHours(-7), DateTimeKind.Utc);
        if (endsAt <= startsAt) return Back("Không tạo được: ngày kết thúc phải sau hoặc bằng ngày bắt đầu.");

        var desc = (description ?? "").Trim();
        if (desc.Length > 200) desc = desc.Substring(0, 200);

        _db.Vouchers.Add(new Voucher
        {
            Code = c,
            Description = desc,
            DiscountType = type,
            Value = value,
            MaxDiscount = maxDiscount,
            MinOrder = minOrder,
            StartsAt = startsAt,
            EndsAt = endsAt,
            MaxUses = maxUses,
            MaxUsesPerUser = maxUsesPerUser,
            IsActive = true
        });
        await _db.SaveChangesAsync();
        _log.LogInformation("Admin {Admin} tạo mã giảm giá {Code}", User.Identity?.Name, c);
        _db.AdminAuditLogs.Add(new AdminAuditLog { At = DateTime.UtcNow, AdminEmail = User.Identity?.Name ?? "", Action = "VoucherCreate", Target = "Voucher:" + c, Detail = $"{type} {value:N0}; tổng {maxUses} lượt, mỗi người {maxUsesPerUser}" });
        await _db.SaveChangesAsync();
        return Back($"Đã tạo mã {c}.");
    }

    // ================= Mã giảm giá: công khai / ẩn khỏi trang Ưu đãi =================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VoucherPublic(int id)
    {
        var v = await _db.Vouchers.FirstOrDefaultAsync(x => x.Id == id);
        if (v == null) return NotFound();

        // Mã riêng của một tài khoản (quà sinh nhật...) không bao giờ được công khai
        if (v.OwnerUserId != null)
        {
            TempData["AdminMsg"] = $"Mã {v.Code} là mã riêng của một tài khoản, không thể công khai.";
            return RedirectToAction(nameof(Vouchers));
        }

        v.IsPublic = !v.IsPublic;
        await _db.SaveChangesAsync();
        _log.LogInformation("Admin {Admin} đổi công khai mã {Code} thành {Public}", User.Identity?.Name, v.Code, v.IsPublic);
        TempData["AdminMsg"] = v.IsPublic ? $"Mã {v.Code} đã hiện ở trang Ưu đãi." : $"Mã {v.Code} đã ẩn khỏi trang Ưu đãi.";
        return RedirectToAction(nameof(Vouchers));
    }

    // ================= Mã giảm giá: xóa (chỉ khi chưa có ai dùng) =================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VoucherDelete(int id)
    {
        var v = await _db.Vouchers.FirstOrDefaultAsync(x => x.Id == id);
        if (v == null) return NotFound();

        // Mã đã từng được áp vào đơn thì phải giữ lại để không mất lịch sử doanh thu: chỉ cho tắt
        if (await _db.VoucherUses.AnyAsync(u => u.VoucherId == id))
        {
            TempData["AdminMsg"] = $"Mã {v.Code} đã có người dùng nên không xóa được (để giữ lịch sử doanh thu). Hãy bấm Tắt để ngừng dùng.";
            return RedirectToAction(nameof(Vouchers));
        }

        _db.Vouchers.Remove(v);
        await _db.SaveChangesAsync();
        _log.LogInformation("Admin {Admin} xóa mã giảm giá {Code}", User.Identity?.Name, v.Code);
        _db.AdminAuditLogs.Add(new AdminAuditLog { At = DateTime.UtcNow, AdminEmail = User.Identity?.Name ?? "", Action = "VoucherDelete", Target = "Voucher:" + v.Code, Detail = v.Code });
        await _db.SaveChangesAsync();
        TempData["AdminMsg"] = $"Đã xóa mã {v.Code}.";
        return RedirectToAction(nameof(Vouchers));
    }

    // ================= Mã giảm giá: bật / tắt =================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VoucherToggle(int id)
    {
        var v = await _db.Vouchers.FirstOrDefaultAsync(x => x.Id == id);
        if (v == null) return NotFound();

        v.IsActive = !v.IsActive;
        await _db.SaveChangesAsync();
        _log.LogInformation("Admin {Admin} {Action} mã {Code}", User.Identity?.Name, v.IsActive ? "bật" : "tắt", v.Code);
        _db.AdminAuditLogs.Add(new AdminAuditLog { At = DateTime.UtcNow, AdminEmail = User.Identity?.Name ?? "", Action = "VoucherToggle", Target = "Voucher:" + v.Code, Detail = v.Code + (v.IsActive ? " bật" : " tắt") });
        await _db.SaveChangesAsync();
        TempData["AdminMsg"] = v.IsActive ? $"Đã bật mã {v.Code}." : $"Đã tắt mã {v.Code}.";
        return RedirectToAction(nameof(Vouchers));
    }
}