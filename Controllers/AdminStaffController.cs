using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class StaffRow
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? CinemaName { get; set; }
}

[Authorize]
public class AdminStaffController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly ILogger<AdminStaffController> _log;

    public AdminStaffController(AppDbContext db, UserManager<AppUser> users, ILogger<AdminStaffController> log)
    {
        _db = db;
        _users = users;
        _log = log;
    }

    // Chỉ quản trị viên được quản lý nhân viên; người khác nhận 404
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!User.IsInRole(AdminSeeder.RoleName)) context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    [HttpGet("Admin/Staff")]
    public async Task<IActionResult> Index()
    {
        var cinemas = await _db.Cinemas.AsNoTracking().Include(c => c.City)
            .OrderBy(c => c.City.Name).ThenBy(c => c.Name).ToListAsync();
        var names = cinemas.ToDictionary(c => c.Id, c => c.Name);

        var staff = await _users.GetUsersInRoleAsync(AdminSeeder.StaffRoleName);
        var rows = staff.OrderBy(u => u.Email).Select(u => new StaffRow
        {
            Id = u.Id,
            Email = u.Email ?? "",
            FullName = u.FullName,
            CinemaName = u.StaffCinemaId.HasValue && names.TryGetValue(u.StaffCinemaId.Value, out var n) ? n : null
        }).ToList();

        ViewBag.Cinemas = cinemas;
        return View(rows);
    }

    // Thêm nhân viên mới hoặc đổi rạp cho nhân viên đã có (theo email tài khoản)
    [HttpPost("Admin/Staff/Save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string? email, int cinemaId)
    {
        IActionResult Back(string msg)
        {
            TempData["AdminMsg"] = msg;
            return RedirectToAction(nameof(Index));
        }

        var e = (email ?? "").Trim();
        if (e.Length == 0) return Back("Nhập email của tài khoản nhân viên.");
        var cinema = await _db.Cinemas.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cinemaId);
        if (cinema == null) return Back("Rạp không hợp lệ.");

        var u = await _users.FindByEmailAsync(e);
        if (u == null || !u.EmailConfirmed)
            return Back("Không có tài khoản đã xác nhận email này. Nhân viên phải đăng ký và xác nhận email trước.");
        if (await _users.IsInRoleAsync(u, AdminSeeder.RoleName))
            return Back("Tài khoản này là quản trị viên, không đặt làm nhân viên được.");

        bool isNew = !await _users.IsInRoleAsync(u, AdminSeeder.StaffRoleName);
        if (isNew)
        {
            var add = await _users.AddToRoleAsync(u, AdminSeeder.StaffRoleName);
            if (!add.Succeeded) return Back("Không thêm được nhân viên. Vui lòng thử lại.");
        }

        u.StaffCinemaId = cinemaId;
        var upd = await _users.UpdateAsync(u);
        if (!upd.Succeeded) return Back("Không lưu được rạp của nhân viên. Vui lòng thử lại.");

        // Đổi stamp để phiên đăng nhập cũ của người này hết hiệu lực, đăng nhập lại sẽ nhận quyền mới
        await _users.UpdateSecurityStampAsync(u);

        var admin = User.Identity?.Name ?? "";
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = admin,
            Action = "StaffSave",
            Target = "User:" + u.Email,
            Detail = (isNew ? "Thêm nhân viên" : "Đổi rạp nhân viên") + ", rạp " + cinema.Name
        });
        await _db.SaveChangesAsync();
        _log.LogInformation("Admin {Admin} {Act} nhân viên {Email} rạp {Cinema}", admin, isNew ? "thêm" : "đổi rạp", u.Email, cinema.Name);
        return Back(isNew ? $"Đã thêm nhân viên {u.Email} cho rạp {cinema.Name}." : $"Đã đổi rạp của {u.Email} sang {cinema.Name}.");
    }

    // Gỡ vai trò nhân viên: người này trở lại tài khoản khách thường
    [HttpPost("Admin/Staff/Remove"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(string? id)
    {
        IActionResult Back(string msg)
        {
            TempData["AdminMsg"] = msg;
            return RedirectToAction(nameof(Index));
        }

        var u = string.IsNullOrEmpty(id) ? null : await _users.FindByIdAsync(id);
        if (u == null || !await _users.IsInRoleAsync(u, AdminSeeder.StaffRoleName)) return Back("Không tìm thấy nhân viên này.");

        await _users.RemoveFromRoleAsync(u, AdminSeeder.StaffRoleName);
        u.StaffCinemaId = null;
        await _users.UpdateAsync(u);
        await _users.UpdateSecurityStampAsync(u);

        var admin = User.Identity?.Name ?? "";
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = admin,
            Action = "StaffRemove",
            Target = "User:" + u.Email,
            Detail = "Gỡ quyền nhân viên"
        });
        await _db.SaveChangesAsync();
        _log.LogInformation("Admin {Admin} gỡ nhân viên {Email}", admin, u.Email);
        return Back($"Đã gỡ quyền nhân viên của {u.Email}.");
    }
}