using System.Net;
using System.Text;
using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Services;

// Chạy ngầm: đơn vừa thanh toán xong thì gửi email vé cho khách.
// Gửi lỗi thì thử lại ở lần quét sau, tối đa trong 24 giờ kể từ lúc thanh toán.
public class TicketEmailService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TicketEmailService> _log;

    public TicketEmailService(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<TicketEmailService> log)
    {
        _scopes = scopes;
        _cfg = cfg;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Quét gửi email vé thất bại");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mail = scope.ServiceProvider.GetRequiredService<IEmailService>();
         var since = DateTime.UtcNow.AddHours(-24);

        var list = await db.Bookings.AsNoTracking().AsSplitQuery()
            .Where(b => b.Status == "Paid" && b.TicketEmailedAt == null && b.PaidAt >= since)
            .Include(b => b.Showtime).ThenInclude(s => s.Movie)
            .Include(b => b.Showtime).ThenInclude(s => s.Room).ThenInclude(r => r.Cinema)
            .Include(b => b.Seats).ThenInclude(x => x.Seat)
            .Include(b => b.Items).ThenInclude(i => i.Product)
            .OrderBy(b => b.PaidAt)
            .Take(10)
            .ToListAsync(ct);

        foreach (var b in list)
        {
            var email = await db.Users.AsNoTracking()
                .Where(u => u.Id == b.UserId).Select(u => u.Email).FirstOrDefaultAsync(ct);

            bool done;
            if (string.IsNullOrEmpty(email)) done = true;   // không có email thì bỏ qua, khỏi thử lại
            else
            {
                var title = b.Showtime.Movie.Title;
                done = await mail.SendAsync(email, $"Vé xem phim {title} - mã {b.Code}", Build(b));
            }

            if (done)
            {
                await db.Bookings.Where(x => x.Id == b.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.TicketEmailedAt, (DateTime?)DateTime.UtcNow), ct);
                _log.LogInformation("Đã xử lý email vé của đơn {Code}", b.Code);
            }
        }
    }

    private string Build(Booking b)
    {
        string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var st = b.Showtime;
        var start = st.StartTime.AddHours(7);
        var baseUrl = (_cfg["App:BaseUrl"] ?? "http://localhost:5255").TrimEnd('/');
        var link = baseUrl + "/Tickets/Details/" + b.Id;

        var seats = string.Join(", ", b.Seats
            .OrderBy(x => x.Seat.RowLabel).ThenBy(x => x.Seat.SeatNumber)
            .Select(x => H(x.Seat.RowLabel + x.Seat.SeatNumber + " (" + TicketPricing.Get(x.TicketType).Name + ")")));
        var items = b.Items.Count == 0
            ? "Không chọn"
            : string.Join(", ", b.Items.Select(i => H(i.Quantity + " × " + i.Product.Name)));

        decimal seatsTotal = b.Seats.Sum(s => s.Price);
        decimal itemsTotal = b.Items.Sum(i => i.UnitPrice * i.Quantity);

        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Arial,Helvetica,sans-serif;max-width:560px;margin:0 auto;color:#222\">");
        sb.Append("<h2 style=\"color:#b97a10;margin:0 0 4px\">Star Cinema</h2>");
        sb.Append("<p style=\"margin:0 0 16px\">Cảm ơn bạn đã đặt vé. Đơn của bạn đã được xác nhận thanh toán.</p>");
        sb.Append("<div style=\"border:2px dashed #b97a10;border-radius:8px;padding:14px;text-align:center;margin-bottom:16px\">");
        sb.Append("<div style=\"font-size:12px;color:#777\">Mã đặt vé</div>");
        sb.Append("<div style=\"font-size:26px;font-weight:bold;letter-spacing:3px;color:#b97a10\">" + H(b.Code) + "</div></div>");
        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:14px\">");
        void Row(string k, string v) =>
            sb.Append("<tr><td style=\"padding:6px 0;color:#777;width:120px;vertical-align:top\">" + k + "</td><td style=\"padding:6px 0\">" + v + "</td></tr>");
        Row("Phim", "<b>" + H(st.Movie.Title) + "</b>");
        Row("Rạp", H(st.Room.Cinema.Name));
        Row("Suất chiếu", start.ToString("HH:mm") + " · " + start.ToString("dd/MM/yyyy"));
        Row("Phòng", H(st.Room.Name + " (" + st.Room.Type + ") · " + st.Format + " · " + st.LanguageMode));
        Row("Ghế", seats);
        Row("Bắp nước", items);
        Row("Tiền vé", seatsTotal.ToString("N0") + " đ");
        Row("Tiền bắp nước", itemsTotal.ToString("N0") + " đ");
        if (b.MemberDiscount > 0) Row("Ưu đãi hạng", "− " + b.MemberDiscount.ToString("N0") + " đ");
        if (b.DiscountAmount > 0) Row("Giảm giá", "− " + b.DiscountAmount.ToString("N0") + " đ (mã " + H(b.VoucherCode) + ")");
        Row("Tổng cộng", "<b style=\"font-size:16px\">" + b.Total.ToString("N0") + " đ</b>");
        sb.Append("</table>");
        sb.Append("<p style=\"margin:20px 0\"><a href=\"" + H(link) + "\" style=\"background:#e6a23c;color:#1a1208;text-decoration:none;font-weight:bold;padding:12px 22px;border-radius:6px;display:inline-block\">Xem vé và mã QR</a></p>");
        sb.Append("<p style=\"font-size:12px;color:#777\">Đưa mã QR hoặc mã đặt vé cho nhân viên khi vào rạp. Cần đổi hoặc hoàn vé, vui lòng liên hệ rạp trước giờ chiếu.</p>");
        sb.Append("</div>");
        return sb.ToString();
    }
}