using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class AdminCustomerRow
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public string Tier { get; set; } = "";
    public int Points { get; set; }
    public int NoShow { get; set; }
    public DateTime? BlockedUntil { get; set; }
    public int PaidOrders { get; set; }
}

// Quản lý khách hàng: xem khách bị khóa trả tiền mặt (do giữ vé mà không đến thanh toán), mở khóa hoặc khóa tay.
[Authorize]
[Route("Admin/Customers")]
public class AdminCustomersController : Controller
{
    private const int PageSize = 30;
    private const int BlockDays = 30;

    private readonly AppDbContext _db;
    private readonly ILogger<AdminCustomersController> _log;

    public AdminCustomersController(AppDbContext db, ILogger<AdminCustomersController> log)
    {
        _db = db;
        _log = log;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!User.IsInRole(AdminSeeder.RoleName)) context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    // view: blocked = đang bị khóa, warn = có lần vi phạm, all = tất cả. Có từ khóa tìm kiếm thì tìm trong tất cả khách.
    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, string? view, int page = 1)
    {
        var now = DateTime.UtcNow;
        view = view == "warn" || view == "all" ? view : "blocked";
        q = (q ?? "").Trim();

        var query = _db.Users.AsNoTracking().AsQueryable();
        if (q.Length > 0)
        {
            var up = q.ToUpperInvariant();
            query = query.Where(u => (u.NormalizedEmail != null && u.NormalizedEmail.Contains(up))
                                     || u.FullName.ToUpper().Contains(up));
        }
        else if (view == "blocked") query = query.Where(u => u.CashBlockedUntil > now);
        else if (view == "warn") query = query.Where(u => u.CashNoShowCount > 0 || u.CashBlockedUntil > now);

        int total = await query.CountAsync();
        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pages);

        var rows = await query
            .OrderByDescending(u => u.CashBlockedUntil).ThenByDescending(u => u.CashNoShowCount).ThenBy(u => u.Email)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(u => new AdminCustomerRow
            {
                Id = u.Id,
                Email = u.Email ?? "",
                FullName = u.FullName,
                Phone = u.PhoneNumber,
                Tier = u.MemberTier,
                Points = u.Points,
                NoShow = u.CashNoShowCount,
                BlockedUntil = u.CashBlockedUntil,
                PaidOrders = _db.Bookings.Count(b => b.UserId == u.Id && b.Status == "Paid")
            }).ToListAsync();

        ViewBag.Q = q;
        ViewBag.View = view;
        ViewBag.Page = page;
        ViewBag.Pages = pages;
        ViewBag.Total = total;
        return View("~/Views/Admin/Customers.cshtml", rows);
    }

    private void Audit(string action, string email, string detail) =>
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = User.Identity?.Name ?? "",
            Action = action,
            Target = "User:" + email,
            Detail = detail
        });

    // ================= Mở khóa tiền mặt =================
    [HttpPost("Unblock/{id}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Unblock(string id, string? q, string? view)
    {
        var email = await _db.Users.Where(x => x.Id == id).Select(x => x.Email).FirstOrDefaultAsync();
        if (email == null)
        {
            TempData["AdminMsg"] = "Không tìm thấy khách.";
            return RedirectToAction(nameof(Index), new { q, view });
        }

        await _db.Users.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.CashBlockedUntil, (DateTime?)null)
            .SetProperty(x => x.CashNoShowCount, 0));
        Audit("CashUnblock", email, "Mở khóa trả tiền mặt");
        await _db.SaveChangesAsync();
        _log.LogInformation("Admin {Admin} mở khóa tiền mặt cho {Email}", User.Identity?.Name, email);

        TempData["AdminMsg"] = "Đã mở khóa trả tiền mặt cho " + email + ".";
        return RedirectToAction(nameof(Index), new { q, view });
    }

    // ================= Khóa tay (khách hay giữ vé không đến) =================
    [HttpPost("Block/{id}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Block(string id, string? q, string? view)
    {
        var email = await _db.Users.Where(x => x.Id == id).Select(x => x.Email).FirstOrDefaultAsync();
        if (email == null)
        {
            TempData["AdminMsg"] = "Không tìm thấy khách.";
            return RedirectToAction(nameof(Index), new { q, view });
        }

        var until = DateTime.UtcNow.AddDays(BlockDays);
        await _db.Users.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.CashBlockedUntil, (DateTime?)until)
            .SetProperty(x => x.CashNoShowCount, 0));
        Audit("CashBlock", email, $"Khóa trả tiền mặt {BlockDays} ngày");
        await _db.SaveChangesAsync();
        _log.LogInformation("Admin {Admin} khóa tiền mặt {Email} {Days} ngày", User.Identity?.Name, email, BlockDays);

        TempData["AdminMsg"] = $"Đã khóa trả tiền mặt {BlockDays} ngày cho {email}. Khách vẫn thanh toán được bằng chuyển khoản.";
        return RedirectToAction(nameof(Index), new { q, view });
    }
}