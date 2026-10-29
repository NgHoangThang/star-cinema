namespace CinemaApp.Models;

public record TicketType(string Code, string Name, int DiscountPercent, string Note);

public static class TicketPricing
{
    // Mức giảm vé U22 tính trên giá gốc của suất chiếu (không cộng dồn với suất sớm / thứ Tư)
    public const int U22Percent = 30;
    public const int U22MaxAge = 22;          // từ 22 tuổi trở xuống
    public const int U22MaxPerOrder = 1;      // mỗi đơn tối đa 1 vé U22

    // Muốn đổi mức giảm vé trẻ em hay thêm loại vé thì sửa ở đây
    public static readonly TicketType[] All =
    {
        new("Adult", "Người lớn", 0, ""),
        new("Child", "Trẻ em", 30, "Dành cho trẻ em dưới 13 tuổi, đi cùng người lớn."),
        new("U22", "U22", U22Percent, "Dành cho khách từ 22 tuổi trở xuống. Vui lòng mang theo giấy tờ có ngày sinh để nhân viên kiểm tra khi vào rạp.")
    };

    public const decimal VipSurcharge = 15000;

    public static bool IsValid(string? code) => All.Any(t => t.Code == code);

    public static TicketType Get(string? code) => All.FirstOrDefault(t => t.Code == code) ?? All[0];

    // Khách có thuộc U22 không (tính theo ngày hiện tại ở Việt Nam). Không có ngày sinh thì không được.
    public static bool IsU22(DateOnly? birth, DateTime? nowUtc = null)
    {
        if (birth == null) return false;
        var today = DateOnly.FromDateTime((nowUtc ?? DateTime.UtcNow).AddHours(7));
        int age = today.Year - birth.Value.Year;
        if (birth.Value.AddYears(age) > today) age--;
        return age >= 0 && age <= U22MaxAge;
    }

    // Giá một vé = giá suất chiếu (đã ưu đãi theo giờ và ngày) trừ phần giảm theo loại vé, làm tròn đến nghìn,
    // rồi cộng phụ thu ghế VIP
    public static decimal Price(decimal basePrice, string seatType, string? ticketCode)
    {
        var t = Get(ticketCode);
        var ticket = Math.Round(basePrice * (100 - t.DiscountPercent) / 100000m, 0, MidpointRounding.AwayFromZero) * 1000m;
        return ticket + (seatType == "VIP" ? VipSurcharge : 0);
    }

    // Giá dùng khi đã biết cả giá đã ưu đãi (effective) lẫn giá gốc của suất chiếu (raw).
    // Vé U22 lấy giá thấp hơn giữa "giảm U22 trên giá gốc" và "giá đã ưu đãi giờ/ngày" (không cộng dồn).
    // Các loại vé khác tính như hàm trên.
    public static decimal Price(decimal effective, decimal raw, string seatType, string? ticketCode)
    {
        if (ticketCode != "U22") return Price(effective, seatType, ticketCode);
        var fromRaw = Math.Round(raw * (100 - U22Percent) / 100000m, 0, MidpointRounding.AwayFromZero) * 1000m;
        var fromEffective = Math.Round(effective / 1000m, 0, MidpointRounding.AwayFromZero) * 1000m;
        var ticket = Math.Min(fromRaw, fromEffective);
        return ticket + (seatType == "VIP" ? VipSurcharge : 0);
    }
}