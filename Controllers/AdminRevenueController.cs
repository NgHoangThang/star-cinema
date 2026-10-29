using System.Globalization;
using System.Text;
using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class RevDay
{
    public string Label { get; set; } = "";
    public decimal Amount { get; set; }
    public int Orders { get; set; }
}

public class RevLine
{
    public string Name { get; set; } = "";
    public int Orders { get; set; }
    public int Qty { get; set; }
    public decimal Amount { get; set; }
}

public class RevenueReport
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int? CinemaId { get; set; }
    public string CinemaName { get; set; } = "Tất cả rạp";
    public int RangeDays { get; set; }

    public decimal Net { get; set; }             // thực thu = tổng tiền các đơn đã thanh toán
    public decimal TicketsGross { get; set; }    // tiền vé trước giảm giá
    public decimal FoodGross { get; set; }       // tiền bắp nước trước giảm giá
    public decimal Discounts { get; set; }       // giảm giá (mã + thành viên)
    public int Orders { get; set; }
    public int Tickets { get; set; }
    public decimal AvgOrder { get; set; }

    public decimal Refunded { get; set; }
    public int RefundCount { get; set; }
    public int ExpiredCount { get; set; }
    public int CancelledCount { get; set; }

    public int SoldSeats { get; set; }
    public int Capacity { get; set; }
    public double Occupancy { get; set; }

    public List<RevDay> Days { get; set; } = new();
    public List<RevLine> Movies { get; set; } = new();
    public List<RevLine> Cinemas { get; set; } = new();
    public List<RevLine> Methods { get; set; } = new();
    public List<RevLine> Products { get; set; } = new();
}

[Authorize]
[Route("Admin/Revenue")]
public class AdminRevenueController : Controller
{
    private const int MaxDays = 366;

    private readonly AppDbContext _db;

    public AdminRevenueController(AppDbContext db) => _db = db;

    // Báo cáo tiền chỉ dành cho admin; người khác nhận 404
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!User.IsInRole(AdminSeeder.RoleName)) context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    private class OrderRow
    {
        public int Id { get; set; }
        public string? Code { get; set; }
        public DateTime PaidAt { get; set; }
        public decimal Total { get; set; }
        public decimal SeatsSum { get; set; }
        public decimal ItemsSum { get; set; }
        public int SeatCount { get; set; }
        public string? Method { get; set; }
        public string Movie { get; set; } = "";
        public string Cinema { get; set; } = "";
    }

    // Đọc khoảng ngày theo giờ Việt Nam, đổi sang UTC để truy vấn
    private static (DateTime from, DateTime to, DateTime fromUtc, DateTime toUtc) ParseRange(string? from, string? to)
    {
        var today = DateTime.UtcNow.AddHours(7).Date;
        DateTime t = today, f = today.AddDays(-29);
        if (DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pt)) t = pt.Date;
        if (DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pf)) f = pf.Date;
        if (f > t) f = t;
        if ((t - f).TotalDays >= MaxDays) f = t.AddDays(-(MaxDays - 1));
        var fromUtc = DateTime.SpecifyKind(f.AddHours(-7), DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(t.AddDays(1).AddHours(-7), DateTimeKind.Utc);
        return (f, t, fromUtc, toUtc);
    }

    private IQueryable<Booking> PaidQuery(DateTime fromUtc, DateTime toUtc, int? cinemaId)
    {
        var q = _db.Bookings.AsNoTracking()
            .Where(b => b.Status == "Paid" && b.PaidAt >= fromUtc && b.PaidAt < toUtc);
        if (cinemaId.HasValue && cinemaId > 0) q = q.Where(b => b.Showtime.Room.CinemaId == cinemaId.Value);
        return q;
    }

    private Task<List<OrderRow>> LoadOrders(IQueryable<Booking> q) =>
        q.OrderBy(b => b.PaidAt).Select(b => new OrderRow
        {
            Id = b.Id,
            Code = b.Code,
            PaidAt = b.PaidAt!.Value,
            Total = b.Total,
            SeatsSum = b.Seats.Sum(s => (decimal?)s.Price) ?? 0m,
            ItemsSum = b.Items.Sum(i => (decimal?)(i.UnitPrice * i.Quantity)) ?? 0m,
            SeatCount = b.Seats.Count,
            Method = b.PaymentMethod,
            Movie = b.Showtime.Movie.Title,
            Cinema = b.Showtime.Room.Cinema.Name
        }).ToListAsync();

    private static string MethodText(string? m) => m switch
    {
        "Cash" => "Tiền mặt",
        "Transfer" => "Chuyển khoản",
        "QR" => "Chuyển khoản",
        _ => "Khác"
    };

    private static List<RevLine> Group(IEnumerable<OrderRow> rows, Func<OrderRow, string> key) =>
        rows.GroupBy(key)
            .Select(g => new RevLine { Name = g.Key, Orders = g.Count(), Qty = g.Sum(x => x.SeatCount), Amount = g.Sum(x => x.Total) })
            .OrderByDescending(x => x.Amount).ThenBy(x => x.Name).ToList();

    // ================= Báo cáo =================
    [HttpGet("")]
    public async Task<IActionResult> Index(string? from, string? to, int? cinemaId)
    {
        var (f, t, fromUtc, toUtc) = ParseRange(from, to);
        if (cinemaId.HasValue && cinemaId <= 0) cinemaId = null;

        var orders = await LoadOrders(PaidQuery(fromUtc, toUtc, cinemaId));

        var rep = new RevenueReport
        {
            From = f.ToString("yyyy-MM-dd"),
            To = t.ToString("yyyy-MM-dd"),
            CinemaId = cinemaId,
            RangeDays = (int)(t - f).TotalDays + 1,
            Orders = orders.Count,
            Net = orders.Sum(x => x.Total),
            TicketsGross = orders.Sum(x => x.SeatsSum),
            FoodGross = orders.Sum(x => x.ItemsSum),
            Tickets = orders.Sum(x => x.SeatCount)
        };
        rep.Discounts = Math.Max(0m, rep.TicketsGross + rep.FoodGross - rep.Net);
        rep.AvgOrder = rep.Orders > 0 ? Math.Round(rep.Net / rep.Orders) : 0m;

        if (cinemaId.HasValue)
            rep.CinemaName = await _db.Cinemas.Where(c => c.Id == cinemaId).Select(c => c.Name).FirstOrDefaultAsync() ?? "Rạp đã chọn";

        // Doanh thu từng ngày (giờ Việt Nam), kể cả ngày không có đơn để biểu đồ liền mạch
        var byDay = orders.GroupBy(x => x.PaidAt.AddHours(7).Date)
            .ToDictionary(g => g.Key, g => (Amount: g.Sum(x => x.Total), Count: g.Count()));
        for (var d = f; d <= t; d = d.AddDays(1))
        {
            byDay.TryGetValue(d, out var v);
            rep.Days.Add(new RevDay { Label = d.ToString("dd/MM"), Amount = v.Amount, Orders = v.Count });
        }

        rep.Movies = Group(orders, x => x.Movie).Take(10).ToList();
        rep.Cinemas = cinemaId.HasValue ? new() : Group(orders, x => x.Cinema).Take(10).ToList();
        rep.Methods = Group(orders, x => MethodText(x.Method))
            .GroupBy(x => x.Name)
            .Select(g => new RevLine { Name = g.Key, Orders = g.Sum(x => x.Orders), Qty = g.Sum(x => x.Qty), Amount = g.Sum(x => x.Amount) })
            .OrderByDescending(x => x.Amount).ToList();

        // Bắp nước theo sản phẩm (đơn giá chốt lúc mua)
        var ids = orders.Select(o => o.Id).ToList();
        if (ids.Count > 0)
        {
            rep.Products = await _db.BookingItems.AsNoTracking()
                .Where(i => ids.Contains(i.BookingId))
                .GroupBy(i => i.Product.Name)
                .Select(g => new RevLine
                {
                    Name = g.Key,
                    Orders = g.Select(i => i.BookingId).Distinct().Count(),
                    Qty = g.Sum(i => i.Quantity),
                    Amount = g.Sum(i => i.UnitPrice * i.Quantity)
                })
                .OrderByDescending(x => x.Amount).Take(10).ToListAsync();
        }

        // Hoàn tiền và đơn mất (theo ngày hoàn / ngày tạo)
        var refQ = _db.Bookings.AsNoTracking().Where(b => b.Status == "Refunded" && b.RefundedAt >= fromUtc && b.RefundedAt < toUtc);
        var expQ = _db.Bookings.AsNoTracking().Where(b => b.Status == "Expired" && b.CreatedAt >= fromUtc && b.CreatedAt < toUtc);
        var canQ = _db.Bookings.AsNoTracking().Where(b => b.Status == "Cancelled" && b.CreatedAt >= fromUtc && b.CreatedAt < toUtc);
        if (cinemaId.HasValue)
        {
            refQ = refQ.Where(b => b.Showtime.Room.CinemaId == cinemaId.Value);
            expQ = expQ.Where(b => b.Showtime.Room.CinemaId == cinemaId.Value);
            canQ = canQ.Where(b => b.Showtime.Room.CinemaId == cinemaId.Value);
        }
        rep.Refunded = (await refQ.SumAsync(b => (decimal?)b.Total)) ?? 0m;
        rep.RefundCount = await refQ.CountAsync();
        rep.ExpiredCount = await expQ.CountAsync();
        rep.CancelledCount = await canQ.CountAsync();

        // Tỷ lệ lấp đầy: vé đã bán / tổng ghế của các suất chiếu đã diễn ra trong khoảng ngày
        var nowUtc = DateTime.UtcNow;
        var endUtc = toUtc < nowUtc ? toUtc : nowUtc;
        var shows = _db.Showtimes.AsNoTracking().Where(s => s.StartTime >= fromUtc && s.StartTime < endUtc);
        var sold = _db.ShowtimeSeats.AsNoTracking()
            .Where(x => x.Status == "Sold" && x.Booking.Status == "Paid" && x.Showtime.StartTime >= fromUtc && x.Showtime.StartTime < endUtc);
        if (cinemaId.HasValue)
        {
            shows = shows.Where(s => s.Room.CinemaId == cinemaId.Value);
            sold = sold.Where(x => x.Showtime.Room.CinemaId == cinemaId.Value);
        }
        rep.Capacity = await shows.SumAsync(s => (int?)s.Room.Seats.Count) ?? 0;
        rep.SoldSeats = await sold.CountAsync();
        rep.Occupancy = rep.Capacity > 0 ? Math.Round(rep.SoldSeats * 100.0 / rep.Capacity, 1) : 0;

        ViewBag.Cinemas = await _db.Cinemas.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        ViewBag.Today = DateTime.UtcNow.AddHours(7).ToString("yyyy-MM-dd");
        return View("~/Views/Admin/Revenue.cshtml", rep);
    }

    // Tránh công thức độc hại khi mở CSV bằng Excel: ô bắt đầu bằng = + - @ thì thêm dấu nháy
    private static string Csv(string? s)
    {
        s ??= "";
        if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s;
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n')) s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // ================= Xuất CSV (danh sách đơn đã thanh toán để đối soát) =================
    [HttpGet("Csv")]
    public async Task<IActionResult> ExportCsv(string? from, string? to, int? cinemaId)
    {
        var (f, t, fromUtc, toUtc) = ParseRange(from, to);
        if (cinemaId.HasValue && cinemaId <= 0) cinemaId = null;
        var orders = await LoadOrders(PaidQuery(fromUtc, toUtc, cinemaId));

        var sb = new StringBuilder();
        sb.AppendLine("Mã vé,Thanh toán lúc,Phim,Rạp,Hình thức,Số vé,Tiền vé,Tiền bắp nước,Giảm giá,Thực thu");
        foreach (var o in orders)
        {
            decimal disc = Math.Max(0m, o.SeatsSum + o.ItemsSum - o.Total);
            sb.AppendLine(string.Join(",",
                Csv(o.Code),
                o.PaidAt.AddHours(7).ToString("yyyy-MM-dd HH:mm"),
                Csv(o.Movie),
                Csv(o.Cinema),
                Csv(MethodText(o.Method)),
                o.SeatCount,
                o.SeatsSum.ToString("0"),
                o.ItemsSum.ToString("0"),
                disc.ToString("0"),                o.Total.ToString("0")));
        }

        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = User.Identity?.Name ?? "",
            Action = "RevenueExport",
            Target = "Revenue",
            Detail = $"{f:yyyy-MM-dd} đến {t:yyyy-MM-dd}; {orders.Count} đơn" + (cinemaId.HasValue ? "; rạp " + cinemaId : "")
        });
        await _db.SaveChangesAsync();

        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"doanh-thu_{f:yyyyMMdd}_{t:yyyyMMdd}.csv");
    }
}