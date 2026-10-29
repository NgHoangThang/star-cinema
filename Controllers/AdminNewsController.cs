using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CinemaApp.Controllers;

[Authorize]
[Route("Admin/News")]
public class AdminNewsController : Controller
{
    public static readonly string[] Categories = { "Khuyến mãi", "Tin phim", "Sự kiện" };

    private readonly AppDbContext _db;
    private readonly ILogger<AdminNewsController> _log;

    public AdminNewsController(AppDbContext db, ILogger<AdminNewsController> log)
    {
        _db = db;
        _log = log;
    }

    // Người không phải admin nhận 404, giống các trang quản trị khác
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!User.IsInRole(AdminSeeder.RoleName)) context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    // Đổi tiêu đề tiếng Việt thành đường dẫn không dấu: "Khuyến mãi tháng 11" -> "khuyen-mai-thang-11"
    private static string Slugify(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant().Replace('đ', 'd');
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        var t = Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), "[^a-z0-9]+", "-").Trim('-');
        return t.Length > 80 ? t.Substring(0, 80).Trim('-') : t;
    }

    // ================= Danh sách =================
    [HttpGet("")]
    public async Task<IActionResult> Index(string? status, string? q)
    {
        var query = _db.Articles.AsNoTracking().AsQueryable();
        if (status == "published") query = query.Where(a => a.IsPublished);
        else if (status == "draft") query = query.Where(a => !a.IsPublished);
        else status = null;

        if (!string.IsNullOrWhiteSpace(q))
        {
            var key = q.Trim().ToLower();
            query = query.Where(a => a.Title.ToLower().Contains(key));
        }

        var rows = await query
            .OrderByDescending(a => a.UpdatedAt)
            .Select(a => new Article
            {
                Id = a.Id,
                Title = a.Title,
                Slug = a.Slug,
                Category = a.Category,
                IsPublished = a.IsPublished,
                PublishedAt = a.PublishedAt,
                UpdatedAt = a.UpdatedAt
            })
            .Take(200).ToListAsync();

        ViewBag.Status = status;
        ViewBag.Q = q;
        ViewBag.CountAll = await _db.Articles.CountAsync();
        ViewBag.CountPub = await _db.Articles.CountAsync(a => a.IsPublished);
        ViewBag.CountDraft = await _db.Articles.CountAsync(a => !a.IsPublished);
        return View("~/Views/Admin/News.cshtml", rows);
    }

    // ================= Viết bài mới / Sửa bài =================
    [HttpGet("Create")]
    public IActionResult Create() => View("~/Views/Admin/NewsEdit.cshtml", new Article());

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        var a = await _db.Articles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        return View("~/Views/Admin/NewsEdit.cshtml", a);
    }

    [HttpPost("Save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, string? title, string? slug, string? category,
        string? summary, string? body, string? coverUrl, bool publish)
    {
        var draft = new Article
        {
            Id = id,
            Title = (title ?? "").Trim(),
            Slug = (slug ?? "").Trim(),
            Category = (category ?? "").Trim(),
            Summary = (summary ?? "").Trim(),
            Body = (body ?? "").Trim(),
            CoverUrl = (coverUrl ?? "").Trim(),
            IsPublished = publish
        };

        IActionResult Fail(string msg)
        {
            ViewBag.Err = msg;
            return View("~/Views/Admin/NewsEdit.cshtml", draft);
        }

        if (draft.Title.Length < 5 || draft.Title.Length > 200) return Fail("Tiêu đề phải từ 5 đến 200 ký tự.");
        if (!Categories.Contains(draft.Category)) return Fail("Nhóm bài không hợp lệ.");
        if (draft.Summary.Length > 300) return Fail("Mô tả ngắn tối đa 300 ký tự.");
        if (draft.Body.Length < 10) return Fail("Nội dung quá ngắn.");
        if (draft.Body.Length > 20000) return Fail("Nội dung tối đa 20,000 ký tự.");
        if (draft.CoverUrl.Length > 500) return Fail("Đường dẫn ảnh bìa quá dài.");
        if (draft.CoverUrl.Length > 0 && !(draft.CoverUrl.StartsWith("https://") || draft.CoverUrl.StartsWith("http://") || draft.CoverUrl.StartsWith("/")))
            return Fail("Ảnh bìa phải là đường dẫn bắt đầu bằng https:// hoặc /.");

        var s = Slugify(draft.Slug.Length > 0 ? draft.Slug : draft.Title);
        if (s.Length == 0) return Fail("Không tạo được đường dẫn. Hãy nhập đường dẫn gồm chữ và số.");
        draft.Slug = s;
        if (await _db.Articles.AnyAsync(x => x.Slug == s && x.Id != id))
            return Fail($"Đường dẫn \"{s}\" đã có bài khác dùng. Hãy sửa ô Đường dẫn.");

        var now = DateTime.UtcNow;
        Article a;
        if (id == 0)
        {
            a = new Article { AuthorEmail = User.Identity?.Name ?? "", CreatedAt = now };
            _db.Articles.Add(a);
        }
        else
        {
            var found = await _db.Articles.FirstOrDefaultAsync(x => x.Id == id);
            if (found == null) return NotFound();
            a = found;
        }

        a.Title = draft.Title;
        a.Slug = s;
        a.Category = draft.Category;
        a.Summary = draft.Summary;
        a.Body = draft.Body;
        a.CoverUrl = draft.CoverUrl;
        a.UpdatedAt = now;
        if (publish)
        {
            a.IsPublished = true;
            a.PublishedAt ??= now;     // lần đầu đăng thì ghi giờ đăng
        }
        else a.IsPublished = false;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Fail("Đường dẫn vừa được bài khác dùng. Hãy sửa ô Đường dẫn.");
        }

        _log.LogInformation("Admin {Admin} lưu bài {Id} ({Slug}), đăng: {Pub}", User.Identity?.Name, a.Id, a.Slug, a.IsPublished);
        TempData["AdminMsg"] = a.IsPublished ? $"Đã lưu và đăng bài \"{a.Title}\"." : $"Đã lưu bài \"{a.Title}\" ở dạng nháp.";
        return RedirectToAction(nameof(Index));
    }

    // ================= Đăng / Ẩn =================
    [HttpPost("Toggle/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var a = await _db.Articles.FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();

        a.IsPublished = !a.IsPublished;
        if (a.IsPublished) a.PublishedAt ??= DateTime.UtcNow;
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _log.LogInformation("Admin {Admin} {Act} bài {Id}", User.Identity?.Name, a.IsPublished ? "đăng" : "ẩn", id);
        TempData["AdminMsg"] = a.IsPublished ? $"Đã đăng bài \"{a.Title}\"." : $"Đã ẩn bài \"{a.Title}\" (chuyển về nháp).";
        return RedirectToAction(nameof(Index));
    }

    // ================= Xóa =================
    [HttpPost("Delete/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var rows = await _db.Articles.Where(x => x.Id == id).ExecuteDeleteAsync();
        TempData["AdminMsg"] = rows == 1 ? "Đã xóa bài viết." : "Không tìm thấy bài viết.";
        if (rows == 1) _log.LogInformation("Admin {Admin} xóa bài {Id}", User.Identity?.Name, id);
        return RedirectToAction(nameof(Index));
    }
}