using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Services;

public record VoucherCheck(bool Ok, string Error, Voucher? Voucher, decimal Discount);

public static class VoucherRules
{
    public static string Normalize(string? code) => (code ?? "").Trim().ToUpperInvariant();

    // Giá sàn: sau mọi mã giảm, khách vẫn phải trả tối thiểu số này (không có đơn 0 đồng)
    public const decimal MinPayable = 10_000m;

    static VoucherCheck Fail(string message) => new(false, message, null, 0);

    // Lượt dùng "đang giữ chỗ": đơn đã thanh toán, hoặc đang chờ thanh toán còn hạn.
    // Đơn hết hạn hoặc đã hủy thì không tính, nên lượt dùng tự được trả lại.
    static IQueryable<VoucherUse> Active(AppDbContext db, DateTime now) =>
        db.VoucherUses.Where(u => u.Booking.Status == "Paid"
                               || (u.Booking.Status == "WaitingPayment" && u.Booking.ExpiresAt > now));

    // subtotal = tiền ghế + tiền bắp nước (chưa giảm). bookingId = đơn đang áp mã (không tự tính lượt của chính nó).
    public static async Task<VoucherCheck> CheckAsync(AppDbContext db, string? rawCode, string userId,
                                                      decimal subtotal, int bookingId)
    {
        var code = Normalize(rawCode);
        if (code.Length == 0) return Fail("Bạn chưa nhập mã giảm giá.");

        var now = DateTime.UtcNow;
        var v = await db.Vouchers.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code);
        if (v == null || !v.IsActive) return Fail("Mã giảm giá không tồn tại hoặc đã bị tắt.");
        // Mã riêng của người khác: báo y như không tồn tại để không lộ mã có thật
        if (v.OwnerUserId != null && v.OwnerUserId != userId) return Fail("Mã giảm giá không tồn tại hoặc đã bị tắt.");
        if (now < v.StartsAt) return Fail("Mã giảm giá không tồn tại hoặc đã bị tắt.");   // chưa đến ngày: giấu để không lộ mã sắp ra mắt
        if (now > v.EndsAt) return Fail("Mã giảm giá đã hết hạn.");
        if (subtotal < v.MinOrder) return Fail($"Đơn tối thiểu {v.MinOrder:N0} đ mới dùng được mã này.");

        var used = Active(db, now).Where(u => u.VoucherId == v.Id && u.BookingId != bookingId);
        if (v.MaxUses > 0 && await used.CountAsync() >= v.MaxUses)
            return Fail("Mã giảm giá đã hết lượt dùng.");
        if (v.MaxUsesPerUser > 0 && await used.CountAsync(u => u.UserId == userId) >= v.MaxUsesPerUser)
            return Fail("Bạn đã dùng hết số lần cho phép của mã này.");

        decimal discount;
        if (v.DiscountType == "Percent")
        {
            discount = decimal.Floor(subtotal * v.Value / 100m);
            if (v.MaxDiscount > 0 && discount > v.MaxDiscount) discount = v.MaxDiscount;
        }
        else
        {
            discount = v.Value;
        }
        decimal maxAllowed = subtotal - MinPayable;   // luôn chừa lại giá sàn
        if (discount > maxAllowed) discount = maxAllowed;
        if (discount <= 0) return Fail("Mã này không giảm được cho đơn của bạn.");

        return new VoucherCheck(true, "", v, discount);
    }
}