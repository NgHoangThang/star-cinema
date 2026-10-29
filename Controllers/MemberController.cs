using System.Security.Claims;
using System.Security.Cryptography;
using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class MemberEntryRow
{
    public string Reason { get; set; } = "";
    public string? MovieTitle { get; set; }
    public int Points { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class MemberView
{
    public MemberStatus Status { get; set; } = MembershipRules.StatusFor(0);
    public int TotalPoints { get; set; }
    public List<MemberEntryRow> Entries { get; set; } = new();
    public int TotalEntries { get; set; }

    // Quà sinh nhật: NoBirth, Claimed, NotMonth, LowTier, Available
    public string GiftState { get; set; } = "NoBirth";
    public decimal GiftAmount { get; set; }
    public decimal GiftMin { get; set; }
    public string? GiftCode { get; set; }
    public DateTime? GiftEnds { get; set; }
}

[Authorize]
public class MemberController : Controller
{
    private const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly AppDbContext _db;

    public MemberController(AppDbContext db) => _db = db;

    private record GiftInfo(string State, decimal Amount, string? Code, DateTime? Ends);

    // Số tiền quà theo hạng, hạng Thường không có quà
    private static decimal GiftAmountFor(string tier) => tier switch
    {
        "Bạc" => 50_000m,
        "Vàng" => 100_000m,
        "Kim cương" => 200_000m,
        _ => 0m
    };

    // Đơn tối thiểu = giá trị quà + 50,000 đ để đơn không bao giờ về 0 đồng
    private static decimal GiftMinOrder(decimal amount) => amount + 50_000m;

    // Hết hạn: 0 giờ ngày 1 tháng sau (giờ Việt Nam), lưu UTC
    private static DateTime GiftEnds(DateTime nowVn) =>
        DateTime.SpecifyKind(new DateTime(nowVn.Year, nowVn.Month, 1).AddMonths(1).AddHours(-7), DateTimeKind.Utc);

    private async Task<int> WindowPointsAsync(string uid)
    {
        var since = DateTime.UtcNow.AddDays(-MembershipRules.WindowDays);
        return await _db.PointEntries.AsNoTracking()
            .Where(p => p.UserId == uid && p.CreatedAt >= since)
            .SumAsync(p => (int?)p.Points) ?? 0;
    }

    private async Task<GiftInfo> GiftAsync(string uid, int windowPoints)
    {
        var nowVn = DateTime.UtcNow.AddHours(7);
        var birth = await _db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.BirthDate).FirstOrDefaultAsync();
        var tier = MembershipRules.StatusFor(windowPoints).Tier.Name;
        decimal amount = GiftAmountFor(tier);
        string prefix = "SN" + nowVn.Year;

        var existing = await _db.Vouchers.AsNoTracking()
            .Where(v => v.OwnerUserId == uid && v.Code.StartsWith(prefix))
            .Select(v => new { v.Code, v.EndsAt })
            .FirstOrDefaultAsync();

        if (birth == null) return new GiftInfo("NoBirth", amount, null, null);
        if (existing != null) return new GiftInfo("Claimed", amount, existing.Code, existing.EndsAt);
        if (birth.Value.Month != nowVn.Month) return new GiftInfo("NotMonth", amount, null, null);
        if (amount <= 0) return new GiftInfo("LowTier", amount, null, null);
        return new GiftInfo("Available", amount, null, GiftEnds(nowVn));
    }

    [HttpGet("Member")]
    public async Task<IActionResult> Index()
    {
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(uid)) return Challenge();

        var since = DateTime.UtcNow.AddDays(-MembershipRules.WindowDays);
        var q = _db.PointEntries.AsNoTracking().Where(p => p.UserId == uid);
        int total = await q.SumAsync(p => (int?)p.Points) ?? 0;
        int window = await q.Where(p => p.CreatedAt >= since).SumAsync(p => (int?)p.Points) ?? 0;
        int count = await q.CountAsync();

        var rows = await (
            from p in q
            join b in _db.Bookings on p.BookingId equals b.Id into bj
            from b in bj.DefaultIfEmpty()
            orderby p.CreatedAt descending
            select new MemberEntryRow
            {
                Reason = p.Reason,
                MovieTitle = b == null ? null : b.Showtime.Movie.Title,
                Points = p.Points,
                CreatedAt = p.CreatedAt
            })
            .Take(30)
            .ToListAsync();

        var gift = await GiftAsync(uid, window);

        return View(new MemberView
        {
            Status = MembershipRules.StatusFor(window),
            TotalPoints = total,
            Entries = rows,
            TotalEntries = count,
            GiftState = gift.State,
            GiftAmount = gift.Amount,
            GiftMin = GiftMinOrder(gift.Amount),
            GiftCode = gift.Code,
            GiftEnds = gift.Ends
        });
    }

    [HttpPost("Member/Birthday"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Birthday()
    {
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(uid)) return Challenge();

        IActionResult Back(string msg)
        {
            TempData["MemberMsg"] = msg;
            return Redirect("/Member");
        }

        int window = await WindowPointsAsync(uid);

        await using var tx = await _db.Database.BeginTransactionAsync();
        // Khóa theo tài khoản: bấm đúp hay mở hai tab cũng chỉ nhận được một lần
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({uid}))");

        var gift = await GiftAsync(uid, window);
        if (gift.State != "Available")
        {
            return Back(gift.State switch
            {
                "NoBirth" => "Bạn chưa cập nhật ngày sinh trong Hồ sơ.",
                "Claimed" => "Bạn đã nhận quà sinh nhật năm nay rồi.",
                "NotMonth" => "Quà sinh nhật chỉ nhận được trong tháng sinh nhật của bạn.",
                _ => "Quà sinh nhật dành cho thành viên từ hạng Bạc trở lên."
            });
        }

        var nowVn = DateTime.UtcNow.AddHours(7);
        string prefix = "SN" + nowVn.Year;
        string code = "";
        for (int i = 0; i < 5; i++)
        {
            var chars = new char[6];
            for (int k = 0; k < chars.Length; k++) chars[k] = CodeChars[RandomNumberGenerator.GetInt32(CodeChars.Length)];
            code = prefix + new string(chars);
            if (!await _db.Vouchers.AnyAsync(v => v.Code == code)) break;
        }

        _db.Vouchers.Add(new Voucher
        {
            Code = code,
            Description = "Quà sinh nhật Star Cinema",
            DiscountType = "Fixed",
            Value = gift.Amount,
            MaxDiscount = 0,
            MinOrder = GiftMinOrder(gift.Amount),
            StartsAt = DateTime.UtcNow,
            EndsAt = gift.Ends ?? GiftEnds(nowVn),
            MaxUses = 1,
            MaxUsesPerUser = 1,
            IsActive = true,
            OwnerUserId = uid
        });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return Back($"Đã nhận quà sinh nhật! Mã của bạn: {code}. Nhập mã này ở bước thanh toán.");
    }
}