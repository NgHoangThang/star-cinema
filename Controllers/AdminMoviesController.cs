using System.Globalization;
using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class AdminMovieRow
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string AgeRating { get; set; } = "P";
    public string Status { get; set; } = "";
    public string PosterUrl { get; set; } = "";
    public string Release { get; set; } = "";
    public int Showtimes { get; set; }
}

// Dữ liệu form thêm / sửa phim
public class MovieForm
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public int DurationMin { get; set; } = 120;
    public string AgeRating { get; set; } = "P";
    public string ReleaseDate { get; set; } = "";          // yyyy-MM-dd
    public string Status { get; set; } = "NowShowing";
    public string Director { get; set; } = "";
    public string Cast { get; set; } = "";
    public string Country { get; set; } = "";
    public string PosterUrl { get; set; } = "";
    public string BackdropUrl { get; set; } = "";
    public string TrailerUrl { get; set; } = "";
    public List<int> GenreIds { get; set; } = new();
    public IFormFile? PosterFile { get; set; }
}

[Authorize]
[Route("Admin/Movies")]
public class AdminMoviesController : Controller
{
    private const int PageSize = 20;
    private const long MaxPosterBytes = 3 * 1024 * 1024;
    private const string UploadUrlPrefix = "/uploads/movies/";
    private static readonly string[] Ratings = { "P", "K", "T13", "T16", "T18" };
    private static readonly string[] Statuses = { "NowShowing", "ComingSoon", "Ended" };
    public static string OpenHours = "08:00 – 22:00, mỗi ngày";

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AdminMoviesController> _log;

    public AdminMoviesController(AppDbContext db, IWebHostEnvironment env, ILogger<AdminMoviesController> log)
    {
        _db = db;
        _env = env;
        _log = log;
    }

    // Người không phải admin nhận 404, giống các trang quản trị khác
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!User.IsInRole(AdminSeeder.RoleName)) context.Result = NotFound();
        base.OnActionExecuting(context);
    }

    private void Audit(string action, int movieId, string detail) =>
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = User.Identity?.Name ?? "",
            Action = action,
            Target = "Movie:" + movieId,
            Detail = detail
        });

    // ================= Danh sách =================
    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, string? status, int page = 1)
    {
        var query = _db.Movies.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var key = q.Trim().ToLower();
            query = query.Where(m => m.Title.ToLower().Contains(key));
        }
        if (status == "NowShowing" || status == "ComingSoon" || status == "Ended")
            query = query.Where(m => m.Status == status);
        else status = null;

        int total = await query.CountAsync();
        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pages);

        var raw = await query
            .OrderByDescending(m => m.ReleaseDate).ThenByDescending(m => m.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(m => new
            {
                m.Id, m.Title, m.AgeRating, m.Status, m.PosterUrl, m.ReleaseDate,
                Showtimes = _db.Showtimes.Count(s => s.MovieId == m.Id)
            })
            .ToListAsync();

        var rows = raw.Select(m => new AdminMovieRow
        {
            Id = m.Id,
            Title = m.Title,
            AgeRating = m.AgeRating,
            Status = m.Status,
            PosterUrl = m.PosterUrl,
            Release = m.ReleaseDate.ToString("dd/MM/yyyy"),
            Showtimes = m.Showtimes
        }).ToList();

        ViewBag.Q = q;
        ViewBag.Status = status;
        ViewBag.Page = page;
        ViewBag.Pages = pages;
        ViewBag.Total = total;
        return View("~/Views/Admin/Movies.cshtml", rows);
    }

    // ================= Đổi nhanh nhãn độ tuổi =================
    [HttpPost("Age/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAge(int id, string age, string? q)
    {
        if (!Ratings.Contains(age))
        {
            TempData["AdminMsg"] = "Nhãn độ tuổi không hợp lệ.";
            return RedirectToAction(nameof(Index), new { q });
        }

        var rows = await _db.Movies.Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.AgeRating, age));

        if (rows == 1)
        {
            Audit("MovieEdit", id, "Đổi nhãn độ tuổi thành " + age);
            await _db.SaveChangesAsync();
            TempData["AdminMsg"] = "Đã lưu nhãn " + age + ".";
        }
        else TempData["AdminMsg"] = "Không tìm thấy phim.";

        return RedirectToAction(nameof(Index), new { q });
    }

    // ================= Form thêm / sửa =================
    private async Task<IActionResult> FormView(MovieForm f, string? error = null)
    {
        ViewBag.Genres = await _db.Genres.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        ViewBag.Ratings = Ratings;
        ViewBag.Error = error;
        return View("~/Views/Admin/MovieEdit.cshtml", f);
    }

    [HttpGet("New")]
    public Task<IActionResult> New() =>
        FormView(new MovieForm { ReleaseDate = DateTime.UtcNow.AddHours(7).ToString("yyyy-MM-dd") });

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        var m = await _db.Movies.AsNoTracking().Include(x => x.Genres).FirstOrDefaultAsync(x => x.Id == id);
        if (m == null) return NotFound();
        return await FormView(new MovieForm
        {
            Id = m.Id,
            Title = m.Title,
            Synopsis = m.Synopsis,
            DurationMin = m.DurationMin,
            AgeRating = m.AgeRating,
            ReleaseDate = m.ReleaseDate.ToString("yyyy-MM-dd"),
            Status = m.Status,
            Director = m.Director,
            Cast = m.Cast,
            Country = m.Country,
            PosterUrl = m.PosterUrl,
            BackdropUrl = m.BackdropUrl,
            TrailerUrl = m.TrailerUrl,
            GenreIds = m.Genres.Select(g => g.Id).ToList()
        });
    }

    // Đường dẫn ảnh/video: để trống, hoặc đường dẫn nội bộ "/...", hoặc http(s)
    private static bool UrlOk(string url)
    {
        if (url.Length == 0) return true;
        if (url.Length > 500) return false;
        if (url.StartsWith("/")) return !url.StartsWith("//") && !url.Contains("..");
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");
    }

    // Lưu file poster tải lên: chỉ nhận JPG, PNG, WEBP, tối đa 3 MB, kiểm tra theo nội dung file chứ không tin đuôi file
    private async Task<string?> SavePosterAsync(IFormFile file, List<string> errs)
    {
        if (file.Length == 0) return null;
        if (file.Length > MaxPosterBytes) { errs.Add("Ảnh poster tối đa 3 MB."); return null; }

        var head = new byte[12];
        int n;
        using (var s = file.OpenReadStream()) n = await s.ReadAsync(head, 0, 12);

        string? ext = null;
        if (n >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) ext = ".jpg";
        else if (n >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) ext = ".png";
        else if (n >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                 && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P') ext = ".webp";
        if (ext == null) { errs.Add("Ảnh poster phải là file JPG, PNG hoặc WEBP."); return null; }

        var dir = Path.Combine(_env.WebRootPath, "uploads", "movies");
        Directory.CreateDirectory(dir);
        var name = Guid.NewGuid().ToString("N") + ext;
        using (var fs = new FileStream(Path.Combine(dir, name), FileMode.Create))
            await file.CopyToAsync(fs);
        return UploadUrlPrefix + name;
    }

    private void DeleteLocalPoster(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith(UploadUrlPrefix)) return;
        var name = Path.GetFileName(url);
        var path = Path.Combine(_env.WebRootPath, "uploads", "movies", name);
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
    }

    [HttpPost("Save"), ValidateAntiForgeryToken, RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> Save(MovieForm f)
    {
        f.Title = (f.Title ?? "").Trim();
        f.Synopsis = (f.Synopsis ?? "").Trim();
        f.Director = (f.Director ?? "").Trim();
        f.Cast = (f.Cast ?? "").Trim();
        f.Country = (f.Country ?? "").Trim();
        f.PosterUrl = (f.PosterUrl ?? "").Trim();
        f.BackdropUrl = (f.BackdropUrl ?? "").Trim();
        f.TrailerUrl = TrailerLinks.Normalize(f.TrailerUrl);
        f.GenreIds ??= new();

        var errs = new List<string>();
        if (f.Title.Length < 1 || f.Title.Length > 200) errs.Add("Tên phim từ 1 đến 200 ký tự.");
        if (f.Synopsis.Length > 4000) errs.Add("Nội dung phim tối đa 4000 ký tự.");
        if (f.DurationMin < 1 || f.DurationMin > 600) errs.Add("Thời lượng từ 1 đến 600 phút.");
        if (!Ratings.Contains(f.AgeRating)) errs.Add("Nhãn độ tuổi không hợp lệ.");
        if (!Statuses.Contains(f.Status)) errs.Add("Trạng thái không hợp lệ.");
        if (f.Director.Length > 200 || f.Cast.Length > 500 || f.Country.Length > 100)
            errs.Add("Đạo diễn tối đa 200, diễn viên tối đa 500, quốc gia tối đa 100 ký tự.");
        if (!UrlOk(f.PosterUrl) || !UrlOk(f.BackdropUrl) || !UrlOk(f.TrailerUrl))
            errs.Add("Đường dẫn ảnh/trailer phải bắt đầu bằng http://, https:// hoặc /, tối đa 500 ký tự.");

        DateTime release = default;
        if (!DateTime.TryParseExact(f.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out release)
            || release.Year < 1900 || release.Year > 2100)
            errs.Add("Ngày khởi chiếu không hợp lệ.");
        else release = DateTime.SpecifyKind(release.Date, DateTimeKind.Utc);

        Movie? m = null;
        if (f.Id != 0)
        {
            m = await _db.Movies.Include(x => x.Genres).FirstOrDefaultAsync(x => x.Id == f.Id);
            if (m == null) return NotFound();
        }

        // Không cho trùng phim (cùng tên và cùng ngày khởi chiếu)
        if (errs.Count == 0)
        {
            var lower = f.Title.ToLower();
            bool dup = await _db.Movies.AnyAsync(x => x.Id != f.Id && x.Title.ToLower() == lower && x.ReleaseDate == release);
            if (dup) errs.Add("Đã có phim cùng tên và cùng ngày khởi chiếu.");
        }

        // Phim ngừng chiếu không được còn suất chưa chiếu (khách vẫn mua được vé)
        if (errs.Count == 0 && f.Status == "Ended" && m != null)
        {
            int future = await _db.Showtimes.CountAsync(s => s.MovieId == m.Id && s.StartTime > DateTime.UtcNow);
            if (future > 0)
                errs.Add($"Phim còn {future} suất chưa chiếu. Hãy xóa các suất này ở trang Suất chiếu (Dọn lịch) rồi mới chuyển sang Ngừng chiếu.");
        }

        string? newPoster = null;
        if (f.PosterFile != null && f.PosterFile.Length > 0)
            newPoster = await SavePosterAsync(f.PosterFile, errs);
        var finalPoster = newPoster ?? f.PosterUrl;
        if (finalPoster.Length == 0) errs.Add("Cần ảnh poster: tải file lên hoặc dán đường dẫn.");

        if (errs.Count > 0)
        {
            DeleteLocalPoster(newPoster);   // lưu lỗi thì bỏ file vừa tải lên, khỏi để rác
            return await FormView(f, string.Join(" ", errs));
        }

        bool isNew = m == null;
        string? oldPoster = m?.PosterUrl;
        m ??= new Movie();
        m.Title = f.Title;
        m.Synopsis = f.Synopsis;
        m.DurationMin = f.DurationMin;
        m.AgeRating = f.AgeRating;
        m.ReleaseDate = release;
        m.Status = f.Status;
        m.Director = f.Director.Length > 0 ? f.Director : "Đang cập nhật";
        m.Cast = f.Cast.Length > 0 ? f.Cast : "Đang cập nhật";
        m.Country = f.Country.Length > 0 ? f.Country : "Đang cập nhật";
        m.PosterUrl = finalPoster;
        m.BackdropUrl = f.BackdropUrl;
        m.TrailerUrl = f.TrailerUrl;

        var genres = await _db.Genres.Where(g => f.GenreIds.Contains(g.Id)).ToListAsync();
        m.Genres.Clear();
        foreach (var g in genres) m.Genres.Add(g);

        if (isNew) _db.Movies.Add(m);
        await _db.SaveChangesAsync();

        Audit(isNew ? "MovieCreate" : "MovieEdit", m.Id,
            $"{m.Title}; {m.DurationMin} phút; nhãn {m.AgeRating}; {(m.Status == "NowShowing" ? "đang chiếu" : m.Status == "Ended" ? "ngừng chiếu" : "sắp chiếu")}");
        await _db.SaveChangesAsync();

        if (!isNew && oldPoster != m.PosterUrl) DeleteLocalPoster(oldPoster);

        TempData["AdminMsg"] = isNew ? "Đã thêm phim \"" + m.Title + "\"." : "Đã lưu phim \"" + m.Title + "\".";
        return RedirectToAction(nameof(Index));
    }

    // ================= Xóa =================
    // Phim đã có suất chiếu thì không xóa được (đơn đặt vé cũ còn tham chiếu), để giữ lịch sử bán vé
    [HttpPost("Delete/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var m = await _db.Movies.Include(x => x.Genres).FirstOrDefaultAsync(x => x.Id == id);
        if (m == null)
        {
            TempData["AdminMsg"] = "Không tìm thấy phim.";
            return RedirectToAction(nameof(Index));
        }

        int showtimes = await _db.Showtimes.CountAsync(s => s.MovieId == id);
        if (showtimes > 0)
        {
            TempData["AdminMsg"] = $"Không xóa được \"{m.Title}\" vì phim đã có {showtimes} suất chiếu. Hãy chuyển trạng thái sang Sắp chiếu hoặc giữ phim lại để bảo toàn lịch sử bán vé.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            string title = m.Title, poster = m.PosterUrl;
            _db.Movies.Remove(m);
            Audit("MovieDelete", id, title);
            await _db.SaveChangesAsync();
            DeleteLocalPoster(poster);
            TempData["AdminMsg"] = "Đã xóa phim \"" + title + "\".";
        }
        catch (DbUpdateException ex)
        {
            _log.LogError(ex, "Xóa phim {Id} thất bại", id);
            TempData["AdminMsg"] = "Không xóa được phim này vì còn dữ liệu liên quan.";
        }
        return RedirectToAction(nameof(Index));
    }
}