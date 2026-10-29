using CinemaApp.Models;
using Microsoft.AspNetCore.Identity;

namespace CinemaApp.Data;

public static class AdminSeeder
{
    public const string RoleName = "Admin";
    public const string StaffRoleName = "Staff";   // nhân viên rạp: chỉ quét vé, gắn với một rạp

    // Tạo vai trò Admin, Staff và cấp quyền Admin cho đúng tài khoản có email trong cấu hình Admin:Email
    public static async Task EnsureAdminAsync(IServiceProvider sp, IConfiguration cfg)
    {
        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(RoleName))
            await roles.CreateAsync(new IdentityRole(RoleName));
        if (!await roles.RoleExistsAsync(StaffRoleName))
            await roles.CreateAsync(new IdentityRole(StaffRoleName));

        var email = cfg["Admin:Email"];
        if (string.IsNullOrWhiteSpace(email)) return;

        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email.Trim());

        // Chỉ cấp quyền cho tài khoản đã xác nhận email
        if (user == null || !user.EmailConfirmed) return;
        if (!await users.IsInRoleAsync(user, RoleName))
            await users.AddToRoleAsync(user, RoleName);
    }
}