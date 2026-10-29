using System.ComponentModel.DataAnnotations;

namespace CinemaApp.Models;

// Dữ liệu hiển thị trên trang hồ sơ
public class ProfileViewModel
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public bool EmailConfirmed { get; set; }
    public string? Phone { get; set; }
    public bool PhoneConfirmed { get; set; }
    public int Points { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateOnly? BirthDate { get; set; }
    public bool BirthLocked { get; set; }   // đã có ngày sinh thì khóa, không cho sửa

    // Hạng thành viên lấy từ MembershipRules, dùng chung với trang Thành viên và trang Ưu đãi
    public static readonly (string Name, int From)[] Tiers =
        MembershipRules.Tiers.Select(t => (t.Name, t.MinPoints)).ToArray();

    public string Tier => MembershipRules.StatusFor(Points).Tier.Name;

    public (string Name, int From)? Next
    {
        get
        {
            var n = MembershipRules.StatusFor(Points).Next;
            return n == null ? null : (n.Name, n.MinPoints);
        }
    }

    public int Percent => MembershipRules.StatusFor(Points).Percent;
}

// Dữ liệu người dùng gửi lên khi sửa hồ sơ
public class ProfileEditModel
{
    [Required(ErrorMessage = "Vui lòng nhập họ tên")]
    [StringLength(80, ErrorMessage = "Họ tên tối đa 80 ký tự")]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = "";

    public DateOnly? BirthDate { get; set; }
}