using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Services;

// Quyền lợi theo hạng thành viên, dùng ở bước thanh toán
public static class MemberPerks
{
    // Hạng hiện tại của khách và phần trăm giảm bắp nước của hạng đó
    public static async Task<(int Percent, string TierName)> TierAsync(AppDbContext db, string userId)
    {
        var since = DateTime.UtcNow.AddDays(-MembershipRules.WindowDays);
        int points = await db.PointEntries.AsNoTracking()
            .Where(p => p.UserId == userId && p.CreatedAt >= since)
            .SumAsync(p => (int?)p.Points) ?? 0;
        var tier = MembershipRules.StatusFor(points).Tier;
        return (tier.ItemsPercent, tier.Name);
    }

    // Số tiền giảm trên tiền bắp nước, làm tròn đến đồng
    public static decimal ItemsDiscount(decimal itemsTotal, int percent)
        => percent <= 0 || itemsTotal <= 0
            ? 0m
            : Math.Round(itemsTotal * percent / 100m, 0, MidpointRounding.AwayFromZero);
}