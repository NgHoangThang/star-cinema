using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;

namespace CinemaApp.Controllers;

// Dữ liệu form ở bước nhập mã khi đăng nhập (dùng bởi AccountController.LoginTwoFactor)
public class TwoFactorLoginForm
{
    public string? Code { get; set; }
    public bool UseRecovery { get; set; }
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

// Bật / tắt xác thực 2 lớp (mã 6 số từ ứng dụng Google Authenticator, Microsoft Authenticator, Authy...)
// cho tài khoản quản trị và nhân viên. Dùng cơ chế TOTP có sẵn của ASP.NET Core Identity.
[Authorize]
public class TwoFactorController : Controller
{
    private const string Issuer = "Star Cinema";
    private const string View_ = "~/Views/Account/TwoFactorSetup.cshtml";

    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly AppDbContext _db;

    public TwoFactorController(UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db)
    {
        _users = users;
        _signIn = signIn;
        _db = db;
    }

    // Chỉ quản trị viên và nhân viên dùng trang này
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!User.IsInRole(AdminSeeder.RoleName) && !User.IsInRole(AdminSeeder.StaffRoleName))
            context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    private static string Group(string key)
    {
        var s = key.Replace(" ", "").ToLowerInvariant();
        var parts = new List<string>();
        for (int i = 0; i < s.Length; i += 4) parts.Add(s.Substring(i, Math.Min(4, s.Length - i)));
        return string.Join(" ", parts);
    }

    private Task<bool> CodeOk(AppUser user, string? code)
    {
        var c = (code ?? "").Replace(" ", "").Replace("-", "");
        return _users.VerifyTwoFactorTokenAsync(user, _users.Options.Tokens.AuthenticatorTokenProvider, c);
    }

    private void Audit(string action, string email, string detail) =>
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = email,
            Action = action,
            Target = "User:" + email,
            Detail = detail
        });

    [HttpGet("Account/TwoFactor")]
    public async Task<IActionResult> Index()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        ViewBag.Enabled = user.TwoFactorEnabled;
        ViewBag.Codes = TempData["TfCodes"] as string;
        ViewBag.Msg = TempData["TfMsg"] as string;
        ViewBag.Err = TempData["TfErr"] as string;

        if (!user.TwoFactorEnabled)
        {
            var key = await _users.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                await _users.ResetAuthenticatorKeyAsync(user);
                key = await _users.GetAuthenticatorKeyAsync(user);
            }
            var email = user.Email ?? user.UserName ?? "user";
            var uri = $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(email)}?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";
            ViewBag.Key = Group(key ?? "");
            ViewBag.Qr = VietQr.QrDataUrl(uri);
        }
        else
        {
            ViewBag.Remaining = await _users.CountRecoveryCodesAsync(user);
        }
        return View(View_);
    }

    // Bật: nhập mã 6 số hiện trong ứng dụng để chứng minh đã quét đúng
    [HttpPost("Account/TwoFactor/Enable"), ValidateAntiForgeryToken, EnableRateLimiting("otpverify")]
    public async Task<IActionResult> Enable(string? code)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        if (user.TwoFactorEnabled) return RedirectToAction(nameof(Index));

        if (!await CodeOk(user, code))
        {
            TempData["TfErr"] = "Mã không đúng. Kiểm tra lại mã trong ứng dụng (mã đổi sau mỗi 30 giây) và giờ trên điện thoại.";
            return RedirectToAction(nameof(Index));
        }

        await _users.SetTwoFactorEnabledAsync(user, true);
        var codes = await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        await _signIn.RefreshSignInAsync(user);

        Audit("TwoFactorOn", user.Email ?? "", "Bật xác thực 2 lớp");
        await _db.SaveChangesAsync();

        TempData["TfCodes"] = string.Join(" ", codes ?? Array.Empty<string>());
        TempData["TfMsg"] = "Đã bật xác thực 2 lớp. Hãy lưu mã khôi phục bên dưới ở nơi an toàn.";
        return RedirectToAction(nameof(Index));
    }

    // Tắt: cần mật khẩu và một mã đang hiện trong ứng dụng
    [HttpPost("Account/TwoFactor/Disable"), ValidateAntiForgeryToken, EnableRateLimiting("otpverify")]
    public async Task<IActionResult> Disable(string? password, string? code)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!user.TwoFactorEnabled) return RedirectToAction(nameof(Index));

        if (string.IsNullOrEmpty(password) || !await _users.CheckPasswordAsync(user, password) || !await CodeOk(user, code))
        {
            TempData["TfErr"] = "Mật khẩu hoặc mã không đúng, chưa tắt được.";
            return RedirectToAction(nameof(Index));
        }

        await _users.SetTwoFactorEnabledAsync(user, false);
        await _users.ResetAuthenticatorKeyAsync(user);   // khóa cũ hết hiệu lực
        await _signIn.RefreshSignInAsync(user);

        Audit("TwoFactorOff", user.Email ?? "", "Tắt xác thực 2 lớp");
        await _db.SaveChangesAsync();

        TempData["TfMsg"] = "Đã tắt xác thực 2 lớp.";
        return RedirectToAction(nameof(Index));
    }

    // Tạo lại bộ mã khôi phục (bộ cũ mất hiệu lực)
    [HttpPost("Account/TwoFactor/NewCodes"), ValidateAntiForgeryToken, EnableRateLimiting("otpverify")]
    public async Task<IActionResult> NewCodes(string? code)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!user.TwoFactorEnabled) return RedirectToAction(nameof(Index));

        if (!await CodeOk(user, code))
        {
            TempData["TfErr"] = "Mã không đúng, chưa tạo được mã khôi phục mới.";
            return RedirectToAction(nameof(Index));
        }

        var codes = await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        TempData["TfCodes"] = string.Join(" ", codes ?? Array.Empty<string>());
        TempData["TfMsg"] = "Đã tạo bộ mã khôi phục mới. Các mã cũ không còn dùng được.";
        return RedirectToAction(nameof(Index));
    }
}