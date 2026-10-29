namespace CinemaApp.Models;

// Giảm giá theo GIỜ và NGÀY của suất chiếu (không cần kiểm tra danh tính ai)
public static class ShowtimePricing
{
    public const int EarlyBeforeHour = 12;                  // suất bắt đầu trước 12:00
    public const int EarlyPercent = 20;
    public const DayOfWeek PromoDay = DayOfWeek.Wednesday;  // ngày ưu đãi trong tuần
    public const int PromoDayPercent = 25;

    // startUtc là giờ UTC trong database; giờ Việt Nam = UTC + 7
    private static (int Percent, string? Label) Rule(DateTime startUtc)
    {
        var local = startUtc.AddHours(7);
        int early = local.Hour < EarlyBeforeHour ? EarlyPercent : 0;
        int day = local.DayOfWeek == PromoDay ? PromoDayPercent : 0;

        if (day > 0 && day >= early) return (day, $"Thứ Tư ưu đãi -{day}%");
        if (early > 0) return (early, $"Suất sớm -{early}%");
        return (0, null);
    }

    public static decimal Effective(decimal basePrice, DateTime startUtc)
    {
        var (pct, _) = Rule(startUtc);
        if (pct == 0) return basePrice;
        return Math.Round(basePrice * (100 - pct) / 100000m, 0, MidpointRounding.AwayFromZero) * 1000m;
    }

    public static string? Label(DateTime startUtc) => Rule(startUtc).Label;
}