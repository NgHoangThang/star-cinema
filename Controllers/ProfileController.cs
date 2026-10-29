using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

[Authorize]
public class ProfileController : Controller
{
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly AppDbContext _db;

    public ProfileController(UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db)
    {
        _users = users;
        _signIn = signIn;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        return View(await Build(user, user.FullName, user.BirthDate));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(ProfileEditModel m)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        // Ngày sinh chỉ nhập được một lần
        DateOnly? newBirth = null;
        if (user.BirthDate == null && m.BirthDate != null)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            if (m.BirthDate > today)
                ModelState.AddModelError(nameof(m.BirthDate), "Ngày sinh không được ở tương lai.");
            else if (m.BirthDate.Value.Year < 1920)
                ModelState.AddModelError(nameof(m.BirthDate), "Ngày sinh không hợp lệ.");
            else
                newBirth = m.BirthDate;
        }

        if (!ModelState.IsValid)
            return View("Index", await Build(user, m.FullName, user.BirthDate ?? m.BirthDate));

        user.FullName = m.FullName.Trim();
        if (newBirth != null) user.BirthDate = newBirth;

        var result = await _users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", "Không lưu được thay đổi. Vui lòng thử lại.");
            return View("Index", await Build(user, m.FullName, user.BirthDate));
        }

        await _signIn.RefreshSignInAsync(user);
        TempData["ProfileMsg"] = "Đã lưu thay đổi.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<ProfileViewModel> Build(AppUser u, string name, DateOnly? birth)
    {
        // Điểm xét hạng: chỉ tính điểm nhận trong 12 tháng gần nhất, cùng cách với trang Thành viên
        var since = DateTime.UtcNow.AddDays(-MembershipRules.WindowDays);
        int points = await _db.PointEntries.AsNoTracking()
            .Where(p => p.UserId == u.Id && p.CreatedAt >= since)
            .SumAsync(p => (int?)p.Points) ?? 0;

        return new ProfileViewModel
        {
            FullName = name,
            Email = u.Email ?? "",
            EmailConfirmed = u.EmailConfirmed,
            Phone = u.PhoneNumber,
            PhoneConfirmed = u.PhoneNumberConfirmed,
            Points = points,
            CreatedAt = u.CreatedAt,
            BirthDate = birth,
            BirthLocked = u.BirthDate != null
        };
    }
}