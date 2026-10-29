using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

// Chỉ chứa thông tin khách được phép thấy của một mã công khai (không có mô tả nội bộ)
public class OfferVoucher
{
    public string Code { get; set; } = "";
    public string DiscountType { get; set; } = "Percent";
    public decimal Value { get; set; }
    public decimal MaxDiscount { get; set; }
    public decimal MinOrder { get; set; }
    public DateTime EndsAt { get; set; }
    public int MaxUses { get; set; }
    public int MaxUsesPerUser { get; set; }
    public int Used { get; set; }
}

public class OffersView
{
    public List<Product> Combos { get; set; } = new();
    public List<OfferVoucher> Vouchers { get; set; } = new();
}

public class OffersController : Controller
{
    private readonly AppDbContext _db;

    public OffersController(AppDbContext db) => _db = db;

    [HttpGet("Offers")]
    public async Task<IActionResult> Index()
    {
        var combos = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .ToListAsync();

        // Mã công khai còn hiệu lực. Mã riêng (OwnerUserId có giá trị) không bao giờ được liệt kê.
        var now = DateTime.UtcNow;
        var vouchers = await _db.Vouchers.AsNoTracking()
            .Where(v => v.IsPublic && v.IsActive && v.OwnerUserId == null && v.StartsAt <= now && v.EndsAt > now)
            .Select(v => new OfferVoucher
            {
                Code = v.Code,
                DiscountType = v.DiscountType,
                Value = v.Value,
                MaxDiscount = v.MaxDiscount,
                MinOrder = v.MinOrder,
                EndsAt = v.EndsAt,
                MaxUses = v.MaxUses,
                MaxUsesPerUser = v.MaxUsesPerUser,
                // Cùng cách đếm với trang admin: đơn đã trả tiền, hoặc đang chờ thanh toán còn hạn
                Used = v.Uses.Count(u => u.Booking.Status == "Paid"
                                      || (u.Booking.Status == "WaitingPayment" && u.Booking.ExpiresAt > now))
            }).ToListAsync();

        // Bỏ mã đã hết lượt, xếp mã sắp hết hạn lên trước
        vouchers = vouchers
            .Where(v => v.MaxUses == 0 || v.Used < v.MaxUses)
            .OrderBy(v => v.EndsAt).ThenBy(v => v.Code)
            .ToList();

        return View(new OffersView { Combos = combos, Vouchers = vouchers });
    }
}