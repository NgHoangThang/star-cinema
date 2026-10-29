using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class AccountController : Controller
{
    private const string ConfirmOtpPurpose = "ConfirmEmailOtp";
    private const string ResetOtpPurpose = "ResetPasswordOtp";
    private static readonly Regex PhoneRx = new(@"^0\d{9}$");
    private static readonly Regex CodeRx = new(@"^\d{6}$");

    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly IEmailService _email;
    private readonly ISmsService _sms;
    private readonly IWebHostEnvironment _env;

    public AccountController(UserManager<AppUser> users, SignInManager<AppUser> signIn,
                             IEmailService email, ISmsService sms, IWebHostEnvironment env)
    {
        _users = users;
        _signIn = signIn;
        _email = email;
        _sms = sms;
        _env = env;
    }

    // ================= Đăng ký =================
    [HttpGet]
    public IActionResult Register(string? returnUrl = null)
        => View(new RegisterViewModel { ReturnUrl = returnUrl });

    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("register")]
    public async Task<IActionResult> Register(RegisterViewModel m)
    {
        // Ô bẫy bị điền nghĩa là bot: lờ đi, không báo lý do
        if (!string.IsNullOrEmpty(m.HpCheck)) return Redirect("/");

        if (!ModelState.IsValid) return View(m);

        var user = new AppUser
        {
            UserName = m.Email.Trim(),
            Email = m.Email.Trim(),
            FullName = m.FullName.Trim(),
            PhoneNumber = string.IsNullOrWhiteSpace(m.Phone) ? null : NormalizePhone(m.Phone)
        };

        var result = await _users.CreateAsync(user, m.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError("", Translate(e.Code, e.Description));
            return View(m);
        }

        MailThrottle.TryAcquire("confirm:" + user.Email);   // ghi nhận lần gửi đầu: "Gửi lại" phải chờ 60 giây
        await SendConfirmation(user);
        TempData["Email"] = user.Email;
        return RedirectToAction(nameof(CheckEmail));
    }

    [HttpGet]
    public IActionResult CheckEmail()
    {
        TempData.Keep("Email");
        TempData.Keep("DevLink");
        TempData.Keep("DevCode");
        TempData.Keep("Msg");
        return View();
    }

    // Xác nhận email, cách 1: bấm liên kết trong email
    [HttpGet]
    public async Task<IActionResult> ConfirmEmail(string? userId, string? code)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code))
            return RedirectToAction(nameof(Login));

        var user = await _users.FindByIdAsync(userId);
        if (user == null) return View("ConfirmEmail", false);

        string token;
        try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch { return View("ConfirmEmail", false); }

        var result = await _users.ConfirmEmailAsync(user, token);
        return View("ConfirmEmail", result.Succeeded);
    }

    // Xác nhận email, cách 2: nhập mã 6 số
    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("otpverify")]
    public async Task<IActionResult> ConfirmEmailCode(string? email, string? code)
    {
        TempData["Email"] = email;
        code = (code ?? "").Trim();
        const string wrong = "Mã không đúng hoặc đã hết hạn. Hãy kiểm tra lại hoặc gửi lại mã mới.";

        // Đếm sai theo email đã nhập (không cần email có thật), tách khỏi bộ đếm khóa đăng nhập:
        // ai đó nhập sai mã cho email của người khác cũng không khóa được đăng nhập của họ.
        var key = "confirm:" + email;
        if (OtpGuard.IsBlocked(key))
        {
            TempData["CodeError"] = "Bạn nhập sai quá nhiều lần. Vui lòng thử lại sau 10 phút.";
            return RedirectToAction(nameof(CheckEmail));
        }

        var user = await _users.FindByEmailAsync(email ?? "");
        // Email không tồn tại cũng trả lời giống hệt, để không ai dò được email nào đã đăng ký
        if (user == null || !CodeRx.IsMatch(code))
        {
            OtpGuard.Fail(key);
            TempData["CodeError"] = wrong;
            return RedirectToAction(nameof(CheckEmail));
        }

        if (user.EmailConfirmed) return View("ConfirmEmail", true);

        bool ok = await _users.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, ConfirmOtpPurpose, code);
        if (!ok)
        {
            OtpGuard.Fail(key);   // nhập sai 5 lần thì chặn nhập mã 10 phút
            TempData["CodeError"] = wrong;
            return RedirectToAction(nameof(CheckEmail));
        }

        OtpGuard.Clear(key);
        user.EmailConfirmed = true;
        await _users.UpdateAsync(user);
        await _users.UpdateSecurityStampAsync(user);   // mã và liên kết cũ hết hiệu lực
        await _users.ResetAccessFailedCountAsync(user);
        return View("ConfirmEmail", true);
    }

    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("resend")]
    public async Task<IActionResult> Resend(string? email)
    {
        var user = await _users.FindByEmailAsync(email ?? "");
        // Mỗi email chỉ được gửi lại sau 60 giây và tối đa 5 lần mỗi giờ (chống spam và cạn hạn mức gửi mail)
        if (user != null && !user.EmailConfirmed && MailThrottle.TryAcquire("confirm:" + email))
            await SendConfirmation(user);

        // Luôn trả lời giống nhau, để không ai dò được email nào đã đăng ký
        TempData["Email"] = email;
        TempData["Msg"] = "Nếu email này đã đăng ký và chưa xác nhận, chúng tôi đã gửi lại liên kết và mã. Mỗi lần gửi cách nhau ít nhất 60 giây.";
        return RedirectToAction(nameof(CheckEmail));
    }

    // ================= Đăng nhập / đăng xuất =================
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
        => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel m)
    {
        if (!ModelState.IsValid) return View(m);

        var result = await _signIn.PasswordSignInAsync(
            m.Email.Trim(), m.Password, m.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded) return Back(m.ReturnUrl);
        if (result.RequiresTwoFactor) return RedirectToAction(nameof(LoginTwoFactor), new { rememberMe = m.RememberMe, returnUrl = m.ReturnUrl });

        if (result.IsNotAllowed)
        {
            ModelState.AddModelError("", "Tài khoản chưa xác nhận email. Hãy kiểm tra hộp thư hoặc gửi lại email xác nhận bên dưới.");
            ViewBag.ShowResend = true;
        }
        else if (result.IsLockedOut)
            ModelState.AddModelError("", "Bạn nhập sai quá nhiều lần. Tài khoản bị khóa tạm thời 10 phút.");
        else
            ModelState.AddModelError("", "Email hoặc mật khẩu không đúng.");
        return View(m);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return Redirect("/");
    }

    // ================= Đăng nhập 2 bước (mã từ ứng dụng Authenticator) =================
    [HttpGet]
    public async Task<IActionResult> LoginTwoFactor(bool rememberMe = false, bool useRecovery = false, string? returnUrl = null)
    {
        var user = await _signIn.GetTwoFactorAuthenticationUserAsync();
        if (user == null) return RedirectToAction(nameof(Login));
        return View(new TwoFactorLoginForm { RememberMe = rememberMe, UseRecovery = useRecovery, ReturnUrl = returnUrl });
    }

    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("login")]
    public async Task<IActionResult> LoginTwoFactor(TwoFactorLoginForm m)
    {
        var user = await _signIn.GetTwoFactorAuthenticationUserAsync();
        if (user == null) return RedirectToAction(nameof(Login));

        var raw = (m.Code ?? "").Trim();
        if (raw.Length == 0)
        {
            ModelState.AddModelError("", "Vui lòng nhập mã.");
            return View(m);
        }

        Microsoft.AspNetCore.Identity.SignInResult result = m.UseRecovery
            ? await _signIn.TwoFactorRecoveryCodeSignInAsync(raw.Replace(" ", ""))
            : await _signIn.TwoFactorAuthenticatorSignInAsync(raw.Replace(" ", "").Replace("-", ""), m.RememberMe, rememberClient: false);

        if (result.Succeeded) return Back(m.ReturnUrl);
        if (result.IsLockedOut)
            ModelState.AddModelError("", "Bạn nhập sai quá nhiều lần. Tài khoản bị khóa tạm thời 10 phút.");
        else
            ModelState.AddModelError("", "Mã không đúng hoặc đã hết hạn.");
        return View(m);
    }

    // ================= Quên mật khẩu: 3 trang =================
    // Trang 1: nhập email
    [HttpGet]
    public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("forgot")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel m)
    {
        if (!ModelState.IsValid) return View(m);

        var email = m.Email.Trim();
        var user = await _users.FindByEmailAsync(email);
        // Mỗi email chỉ được gửi mã sau 60 giây và tối đa 5 lần mỗi giờ (chống spam hộp thư và cạn hạn mức gửi mail)
        if (user != null && await _users.IsEmailConfirmedAsync(user) && MailThrottle.TryAcquire("forgot:" + email))
        {
            var otp = await _users.GenerateUserTokenAsync(user, TokenOptions.DefaultEmailProvider, ResetOtpPurpose);

            var html = $"<p>Chào {WebUtility.HtmlEncode(user.FullName)},</p>" +
                       "<p>Mã đặt lại mật khẩu Star Cinema của bạn là:</p>" +
                       $"<p style=\"font-size:28px;letter-spacing:6px\"><b>{otp}</b></p>" +
                       "<p>Nhập mã này vào trang web để tiếp tục. Mã chỉ có hiệu lực trong thời gian ngắn.</p>" +
                       "<p>Nếu bạn không yêu cầu, hãy bỏ qua email này, mật khẩu của bạn vẫn an toàn.</p>";

            bool sent = await _email.SendAsync(user.Email!, "Mã đặt lại mật khẩu Star Cinema", html);
            if (!sent && _env.IsDevelopment())
                TempData["DevCode"] = otp;   // chưa gửi được email thật: hiện mã ngay trên trang (chỉ ở máy phát triển)
        }

        // Luôn sang trang nhập mã, không tiết lộ email có tồn tại hay không
        return RedirectToAction(nameof(VerifyResetCode), new { email });
    }

    // Trang 2: nhập mã 6 số
    [HttpGet]
    public IActionResult VerifyResetCode(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return RedirectToAction(nameof(ForgotPassword));
        TempData.Keep("DevCode");
        return View(new VerifyResetCodeViewModel { Email = email.Trim() });
    }

    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("reset")]
    public async Task<IActionResult> VerifyResetCode(VerifyResetCodeViewModel m)
    {
        TempData.Keep("DevCode");
        if (!ModelState.IsValid) return View(m);

        const string wrong = "Mã không đúng hoặc đã hết hạn. Hãy kiểm tra lại hoặc gửi lại mã mới.";

        // Đếm sai theo email đã nhập (không cần email có thật), tách khỏi bộ đếm khóa đăng nhập
        var key = "reset:" + m.Email.Trim();
        if (OtpGuard.IsBlocked(key))
        {
            ModelState.AddModelError("", "Bạn nhập sai quá nhiều lần. Vui lòng thử lại sau 10 phút.");
            return View(m);
        }

        var user = await _users.FindByEmailAsync(m.Email.Trim());
        if (user == null)
        {
            OtpGuard.Fail(key);
            ModelState.AddModelError("", wrong);   // trả lời giống hệt khi sai mã, không lộ email có tồn tại
            return View(m);
        }

        bool ok = await _users.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, ResetOtpPurpose, m.Code.Trim());
        if (!ok)
        {
            OtpGuard.Fail(key);   // nhập sai 5 lần thì chặn nhập mã 10 phút
            ModelState.AddModelError("", wrong);
            return View(m);
        }

        OtpGuard.Clear(key);

        // Mã đúng: cấp "giấy phép" đặt mật khẩu, chỉ dùng được cho trang 3
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        TempData["ResetEmail"] = user.Email;
        TempData["ResetToken"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        TempData.Remove("DevCode");
        return RedirectToAction(nameof(ResetPassword));
    }

    // Trang 3: đặt mật khẩu mới (chưa nhập đúng mã thì bị đưa về trang 1)
    [HttpGet]
    public IActionResult ResetPassword()
    {
        var email = TempData.Peek("ResetEmail") as string;
        var token = TempData.Peek("ResetToken") as string;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(token))
            return RedirectToAction(nameof(ForgotPassword));

        return View(new ResetPasswordViewModel { Email = email, Token = token });
    }

    [HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("reset")]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel m)
    {
        if (!ModelState.IsValid) return View(m);

        const string expired = "Phiên đặt lại mật khẩu đã hết hạn. Hãy bắt đầu lại từ trang Quên mật khẩu.";

        var user = await _users.FindByEmailAsync(m.Email.Trim());
        if (user == null)
        {
            ModelState.AddModelError("", expired);
            return View(m);
        }

        string token;
        try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(m.Token)); }
        catch
        {
            ModelState.AddModelError("", expired);
            return View(m);
        }

        var result = await _users.ResetPasswordAsync(user, token, m.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError("", e.Code == "InvalidToken" ? expired : Translate(e.Code, e.Description));
            return View(m);
        }

        await _users.SetLockoutEndDateAsync(user, null);
        await _users.ResetAccessFailedCountAsync(user);
        TempData.Remove("ResetEmail");
        TempData.Remove("ResetToken");
        return RedirectToAction(nameof(ResetPasswordDone));
    }

    [HttpGet]
    public IActionResult ResetPasswordDone() => View();

    // ================= Đổi mật khẩu =================
    [Authorize, HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [Authorize, HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("login")]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel m)
    {
        if (!ModelState.IsValid) return View(m);

        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        var result = await _users.ChangePasswordAsync(user, m.CurrentPassword, m.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError("", Translate(e.Code, e.Description));
            return View(m);
        }

        await _signIn.RefreshSignInAsync(user);   // giữ phiên đăng nhập hiện tại
        ViewBag.Done = true;
        return View(new ChangePasswordViewModel());
    }

    // ================= Xác minh số điện thoại (OTP) =================
    [Authorize, HttpGet]
    public async Task<IActionResult> VerifyPhone(string? returnUrl = null)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();
        if (user.PhoneNumberConfirmed) return Back(returnUrl);

        return View(new VerifyPhoneViewModel { Phone = user.PhoneNumber ?? "", ReturnUrl = returnUrl });
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("otp")]
    public async Task<IActionResult> SendOtp(VerifyPhoneViewModel m)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        ModelState.Remove(nameof(m.Code));
        if (!ModelState.IsValid) return View("VerifyPhone", m);

        var phone = NormalizePhone(m.Phone);
        if (await PhoneTaken(phone, user.Id))
        {
            ModelState.AddModelError(nameof(m.Phone), "Số điện thoại này đã được dùng cho tài khoản khác.");
            return View("VerifyPhone", m);
        }

        if (!MailThrottle.TryAcquire("phone:" + user.Id))
        {
            ModelState.AddModelError(nameof(m.Phone), "Vui lòng đợi khoảng 1 phút trước khi gửi lại mã (tối đa 5 lần mỗi giờ).");
            return View("VerifyPhone", m);
        }

        var code = await _users.GenerateChangePhoneNumberTokenAsync(user, phone);
        bool sent = await _sms.SendAsync(phone, $"Ma xac minh Star Cinema: {code}");
        if (!sent)
        {
            // Chưa có nhà cung cấp SMS: gửi mã về email của tài khoản
            bool mailed = await _email.SendAsync(user.Email!, "Mã xác minh Star Cinema",
                $"<p>Mã xác minh số điện thoại <b>{WebUtility.HtmlEncode(phone)}</b> của bạn là:</p>" +
                $"<p style=\"font-size:24px;letter-spacing:4px\"><b>{code}</b></p>" +
                "<p>Mã chỉ có hiệu lực trong thời gian ngắn. Nếu bạn không yêu cầu, hãy bỏ qua email này.</p>");
            ViewBag.CodeByEmail = mailed;
            if (!mailed && _env.IsDevelopment())
                ViewBag.DevOtp = code;   // chưa cấu hình email: hiện mã trên trang (chỉ ở máy phát triển)
        }

        ModelState.Remove(nameof(m.Phone));
        m.Phone = phone;
        m.Sent = true;
        return View("VerifyPhone", m);
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken, EnableRateLimiting("otpverify")]
    public async Task<IActionResult> VerifyOtp(VerifyPhoneViewModel m)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Challenge();

        var phone = NormalizePhone(m.Phone ?? "");
        ModelState.Remove(nameof(m.Phone));
        m.Phone = phone;
        m.Sent = true;

        if (!PhoneRx.IsMatch(phone))
            ModelState.AddModelError(nameof(m.Phone), "Số điện thoại không hợp lệ.");
        if (string.IsNullOrWhiteSpace(m.Code))
            ModelState.AddModelError(nameof(m.Code), "Vui lòng nhập mã xác minh.");
        if (!ModelState.IsValid) return View("VerifyPhone", m);

        if (await PhoneTaken(phone, user.Id))
        {
            ModelState.AddModelError(nameof(m.Phone), "Số điện thoại này đã được dùng cho tài khoản khác.");
            return View("VerifyPhone", m);
        }

        var result = await _users.ChangePhoneNumberAsync(user, phone, m.Code!.Trim());
        if (!result.Succeeded)
        {
            ModelState.AddModelError(nameof(m.Code), "Mã không đúng hoặc đã hết hạn. Hãy gửi lại mã mới.");
            return View("VerifyPhone", m);
        }

        await _signIn.RefreshSignInAsync(user);
        return Back(m.ReturnUrl);
    }

    // ================= Hàm dùng chung =================
    private Task<bool> PhoneTaken(string phone, string userId)
        => _users.Users.AnyAsync(u => u.PhoneNumber == phone && u.PhoneNumberConfirmed && u.Id != userId);

    private static string NormalizePhone(string phone)
    {
        var p = phone.Trim().Replace(" ", "").Replace(".", "").Replace("-", "");
        return p.StartsWith("+84") ? "0" + p[3..] : p;
    }

    // Gửi một email chứa CẢ HAI cách xác nhận: liên kết và mã 6 số.
    // Chưa cấu hình mail (chỉ ở máy dev) thì hiện cả hai ngay trên trang.
    private async Task SendConfirmation(AppUser user)
    {
        var token = await _users.GenerateEmailConfirmationTokenAsync(user);
        var linkCode = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        // Địa chỉ gốc lấy từ cấu hình App:BaseUrl (không tin tên miền trong yêu cầu, tránh bị giả mạo Host).
        // Chưa cấu hình (máy phát triển) thì dùng địa chỉ của yêu cầu hiện tại.
        var baseUrl = (HttpContext.RequestServices.GetRequiredService<IConfiguration>()["App:BaseUrl"] ?? "").TrimEnd('/');
        var link = baseUrl.Length > 0
            ? baseUrl + Url.Action("ConfirmEmail", "Account", new { userId = user.Id, code = linkCode })
            : Url.Action("ConfirmEmail", "Account", new { userId = user.Id, code = linkCode }, Request.Scheme)!;
        var otp = await _users.GenerateUserTokenAsync(user, TokenOptions.DefaultEmailProvider, ConfirmOtpPurpose);

        var html = $"<p>Chào {WebUtility.HtmlEncode(user.FullName)},</p>" +
                   "<p>Chọn một trong hai cách để kích hoạt tài khoản Star Cinema:</p>" +
                   "<p><b>Cách 1:</b> bấm vào liên kết</p>" +
                   $"<p><a href=\"{link}\">{link}</a></p>" +
                   "<p><b>Cách 2:</b> nhập mã 6 số này vào trang web</p>" +
                   $"<p style=\"font-size:26px;letter-spacing:6px\"><b>{otp}</b></p>" +
                   "<p>Nếu bạn không đăng ký, hãy bỏ qua email này.</p>";

        bool sent = await _email.SendAsync(user.Email!, "Xác nhận tài khoản Star Cinema", html);
        if (!sent && _env.IsDevelopment())
        {
            TempData["DevLink"] = link;
            TempData["DevCode"] = otp;
        }
    }

    // Chỉ cho quay lại địa chỉ trong web của mình (chặn bị đẩy sang web lạ)
    private IActionResult Back(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : Redirect("/");

    private static string Translate(string code, string fallback) => code switch
    {
        "DuplicateUserName" or "DuplicateEmail" => "Email này đã được đăng ký.",
        "PasswordTooShort" => "Mật khẩu phải có ít nhất 8 ký tự.",
        "PasswordRequiresDigit" => "Mật khẩu phải có ít nhất 1 chữ số.",
        "PasswordRequiresLower" => "Mật khẩu phải có ít nhất 1 chữ thường.",
        "PasswordRequiresUpper" => "Mật khẩu phải có ít nhất 1 chữ hoa.",
        "PasswordRequiresNonAlphanumeric" => "Mật khẩu phải có ít nhất 1 ký tự đặc biệt.",
        "PasswordMismatch" => "Mật khẩu hiện tại không đúng.",
        "InvalidToken" => "Mã đã hết hạn hoặc đã được dùng. Hãy yêu cầu mã mới.",
        "InvalidEmail" => "Email không hợp lệ.",
        _ => fallback
    };
}