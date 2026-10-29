using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Models;

// Mỗi dòng là một lần cộng điểm. BookingId duy nhất nên một đơn chỉ được cộng một lần.
[Index(nameof(BookingId), IsUnique = true)]
[Index(nameof(UserId))]
public class PointEntry
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int? BookingId { get; set; }
    public int Points { get; set; }
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

// ItemsPercent: phần trăm giảm trên tiền bắp nước của hạng này
public record MemberTier(string Name, int MinPoints, string Color, string Perk, int ItemsPercent);

public record MemberStatus(int Points, MemberTier Tier, MemberTier? Next, int ToNext, int Percent);

public static class MembershipRules
{
    // Cứ bao nhiêu đồng thực trả thì được 1 điểm
    public const int VndPerPoint = 1000;

    // Hạng xét theo điểm nhận được trong số ngày gần nhất này (12 tháng)
    public const int WindowDays = 365;

    // Mốc hạng theo điểm trong 12 tháng. 1 điểm = 1,000 đ nên Vàng = 2,500,000 đ, Kim cương = 5,000,000 đ như CGV
    public static readonly MemberTier[] Tiers =
    {
        new("Thường", 0, "#b9ad9a", "Tích điểm cho mỗi đơn đã vào rạp", 0),
        new("Bạc", 1000, "#c9d1d9", "Giảm 5% tiền bắp nước mỗi đơn", 5),
        new("Vàng", 2500, "#e6a23c", "Giảm 10% tiền bắp nước mỗi đơn", 10),
        new("Kim cương", 5000, "#8fd3ff", "Giảm 15% tiền bắp nước mỗi đơn", 15),
    };

    public static int PointsFor(decimal paidAmount)
        => paidAmount <= 0 ? 0 : (int)(paidAmount / VndPerPoint);

    // points ở đây là điểm trong 12 tháng gần nhất
    public static MemberStatus StatusFor(int points)
    {
        if (points < 0) points = 0;
        var tier = Tiers[0];
        foreach (var t in Tiers) if (points >= t.MinPoints) tier = t;

        var idx = Array.IndexOf(Tiers, tier);
        var next = idx + 1 < Tiers.Length ? Tiers[idx + 1] : null;
        int toNext = next == null ? 0 : next.MinPoints - points;
        int percent = 100;
        if (next != null)
        {
            int span = next.MinPoints - tier.MinPoints;
            percent = Math.Clamp((int)((long)(points - tier.MinPoints) * 100 / span), 0, 100);
        }
        return new MemberStatus(points, tier, next, toNext, percent);
    }
}