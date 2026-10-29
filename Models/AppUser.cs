using Microsoft.AspNetCore.Identity;

namespace CinemaApp.Models;

public class AppUser : IdentityUser
{
    public string FullName { get; set; } = "";
    public int Points { get; set; }
    public DateOnly? BirthDate { get; set; }
    public string MemberTier { get; set; } = "Thường";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? StaffCinemaId { get; set; }   // rạp được gán cho nhân viên (vai trò Staff); khách thường để trống
    public int CashNoShowCount { get; set; }          // số lần giữ vé trả tiền mặt nhưng không đến trả (đếm dở, về 0 khi bị khóa)
    public DateTime? CashBlockedUntil { get; set; }   // bị khóa lựa chọn tiền mặt đến thời điểm này (UTC); trống = không bị khóa
}