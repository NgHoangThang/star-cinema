namespace CinemaApp.Models;

public class Voucher
{
    public int Id { get; set; }

    // Mã khách gõ, luôn lưu chữ HOA, không trùng nhau
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";

    // "Percent" = giảm theo %, "Fixed" = giảm số tiền cố định
    public string DiscountType { get; set; } = "Percent";
    public decimal Value { get; set; }

    // Với loại Percent: số tiền giảm tối đa (0 = không giới hạn)
    public decimal MaxDiscount { get; set; }

    // Đơn phải từ mức này mới được dùng (0 = không yêu cầu)
    public decimal MinOrder { get; set; }

    // Thời hạn, lưu giờ UTC
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }

    // Tổng số lượt dùng cho phép (0 = không giới hạn)
    public int MaxUses { get; set; }
    // Mỗi tài khoản được dùng tối đa bao nhiêu lần
    public int MaxUsesPerUser { get; set; } = 1;

    public bool IsActive { get; set; } = true;

    // Hiện trong danh sách mã ở trang Ưu đãi. Mặc định ẩn: mã vẫn dùng được nhưng khách phải biết mã.
    public bool IsPublic { get; set; }

    // Mã riêng của một tài khoản (quà sinh nhật...). Để trống = mã công khai, ai cũng dùng được
    public string? OwnerUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<VoucherUse> Uses { get; set; } = new();
}

public class VoucherUse
{
    public int Id { get; set; }
    public int VoucherId { get; set; }
    public Voucher Voucher { get; set; } = null!;

    public string UserId { get; set; } = "";
    public int BookingId { get; set; }
    public Booking Booking { get; set; } = null!;

    // Số tiền đã giảm trong lần dùng này
    public decimal Amount { get; set; }
    public DateTime UsedAt { get; set; } = DateTime.UtcNow;
}