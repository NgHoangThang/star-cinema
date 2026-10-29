using System.Threading.RateLimiting;
using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Mọi yêu cầu POST/PUT/DELETE đều bắt buộc có mã chống giả mạo (CSRF), kể cả action quên gắn thuộc tính
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddMemoryCache();

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ISmsService, SmsService>();
builder.Services.AddHostedService<HoldCleanupService>();
builder.Services.AddHostedService<TicketEmailService>();

builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
{
    o.Password.RequiredLength = 8;
    o.Password.RequireDigit = true;
    o.Password.RequireLowercase = true;
    o.Password.RequireUppercase = false;
    o.Password.RequireNonAlphanumeric = false;
    o.User.RequireUniqueEmail = true;
    o.SignIn.RequireConfirmedEmail = true;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// Liên kết và mã xác nhận hết hạn sau 3 giờ
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(3));
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Account/Login";
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    // Khi chạy thật (có HTTPS) cookie đăng nhập chỉ được gửi qua HTTPS
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

// Khi đặt sau reverse proxy (IIS/Nginx/Cloudflare): bật App:BehindProxy=true để lấy đúng IP, http/https, tên miền
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Giới hạn tốc độ theo địa chỉ IP: (tên, số lần tối đa, trong bao nhiêu phút)
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "text/html; charset=utf-8";
        await ctx.HttpContext.Response.WriteAsync(
            "<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>Thao tác quá nhanh</title><style>" +
            "body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b0907;color:#f3e9d8;font-family:system-ui,sans-serif;padding:16px}" +
            ".b{max-width:440px;text-align:center;border:1px solid #6b5a3a;border-radius:14px;padding:32px;background:#16120d}" +
            "h1{font-size:22px;margin:0 0 10px;color:#e6a23c}p{margin:0 0 20px;color:#b9ad9a;line-height:1.5}" +
            "a{display:inline-block;padding:10px 22px;border-radius:8px;background:#e6a23c;color:#1a1208;font-weight:700;text-decoration:none}" +
            "</style></head><body><div class=\"b\"><h1>Bạn thao tác quá nhanh</h1>" +
            "<p>Hệ thống tạm dừng yêu cầu này để bảo vệ tài khoản và cửa hàng. Vui lòng thử lại sau vài phút.</p>" +
            "<a href=\"/\">Về trang chủ</a></div></body></html>", ct);
    };

    var policies = new (string Name, int Limit, int Minutes)[]
    {
        ("register", 5, 10), ("login", 10, 1), ("resend", 3, 10),
        ("forgot", 3, 10), ("reset", 10, 10), ("otp", 3, 10), ("otpverify", 10, 10),
        ("hold", 20, 1)
    };
    foreach (var p in policies)
    {
        var (name, limit, minutes) = p;
        o.AddPolicy(name, ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(minutes) }));
    }

    // Nhập mã giảm giá: giới hạn theo từng tài khoản (10 lần mỗi 10 phút) để không ai dò mã hàng loạt
    o.AddPolicy("voucher", ctx => RateLimitPartition.GetFixedWindowLimiter(
        "voucher:" + (ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        bool freshDb = !db.Cities.Any(); bool demo = app.Configuration.GetValue<bool>("Seed:Demo", app.Environment.IsDevelopment()); DbSeeder.Seed(db);
    if (freshDb) DbSeeder.SeedMoreCities(db);
    BannerSeeder.Seed(db, app.Environment.WebRootPath);

    var tmdbKey = app.Configuration["Tmdb:ApiKey"];
    if (demo && !string.IsNullOrEmpty(tmdbKey))
        await TmdbImporter.ImportAsync(db, tmdbKey);

    if (demo) ShowtimeSeeder.Seed(db, freshDb);
    if (freshDb) ProductSeeder.Seed(db);
    ProductSeeder.AttachImages(db, app.Environment.WebRootPath); await AdminSeeder.EnsureAdminAsync(scope.ServiceProvider, app.Configuration);

    // Dữ liệu bán vé mẫu cho báo cáo (chỉ ở máy phát triển và chỉ khi đặt Seed__DemoSales)
    var demoSales = app.Configuration["Seed:DemoSales"];
    if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(demoSales))
        await DemoSalesSeeder.RunAsync(db, scope.ServiceProvider, demoSales, app.Configuration);

    // Dọn tài khoản đăng ký quá 24 giờ mà chưa xác nhận email
    var cutoff = DateTime.UtcNow.AddDays(-1);
    db.Users.Where(u => !u.EmailConfirmed && u.CreatedAt < cutoff).ExecuteDelete();
}

if (app.Configuration.GetValue<bool>("App:BehindProxy")) app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/Home/StatusPage", "?code={0}");

// Tiêu đề bảo mật cơ bản cho mọi trang
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=(), payment=()";
    // CSP chạy chế độ báo cáo (Report-Only): chỉ ghi cảnh báo ở Console trình duyệt, KHÔNG chặn gì
    ctx.Response.Headers["Content-Security-Policy-Report-Only"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval' https:; style-src 'self' 'unsafe-inline' https:; " +
        "img-src 'self' data: blob: https:; font-src 'self' data: https:; connect-src 'self' https:; " +
        "frame-ancestors 'self'; base-uri 'self'; form-action 'self'; object-src 'none'";
    await next();
});

app.UseStaticFiles();   // phục vụ ảnh admin tải lên (wwwroot/uploads)
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();   // đặt sau xác thực để giới hạn được theo từng tài khoản
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();