using System.Globalization;
using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class MoviesController : Controller
{
    private readonly AppDbContext _db;
    private const int PageSize = 12;

    public MoviesController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index(string? q, string? status, int? genre, int page = 1)
    {
        var query = _db.Movies.Where(m => m.Status != "Ended");   // phim ngừng chiếu không hiện ở danh sách bán vé

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(m => EF.Functions.ILike(m.Title, "%" + q.Trim() + "%"));

        if (status == "NowShowing" || status == "ComingSoon")
            query = query.Where(m => m.Status == status);

        if (genre.HasValue)
            query = query.Where(m => m.Genres.Any(g => g.Id == genre.Value));

        int total = await query.CountAsync();
        int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);

        var movies = await query
            .OrderByDescending(m => m.ReleaseDate)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        var vm = new MoviesViewModel
        {
            Movies = movies,
            Genres = await _db.Genres.OrderBy(g => g.Name).ToListAsync(),
            Q = q,
            Status = status,
            GenreId = genre,
            Page = page,
            TotalPages = totalPages,
            Total = total
        };
        return View(vm);
    }

    public async Task<IActionResult> Details(int id, string? date, string? fmt)
    {
        var movie = await _db.Movies.Include(m => m.Genres).FirstOrDefaultAsync(m => m.Id == id);
        if (movie == null) return NotFound();

        ViewBag.Related = await _db.Movies
            .Where(m => m.Id != id && m.Status == movie.Status)
            .OrderByDescending(m => m.ReleaseDate)
            .Take(6).ToListAsync();

        // ===== Lịch chiếu =====
        var today = DateTime.UtcNow.AddHours(7).Date;   // ngày hôm nay theo giờ Việt Nam
        var selected = today;
        if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out var d))
            selected = d.Date;
        if (selected < today || selected > today.AddDays(13)) selected = today;

        var city = Request.Cookies["city"] ?? "Hồ Chí Minh";
        var startUtc = DateTime.SpecifyKind(selected.AddHours(-7), DateTimeKind.Utc);
        var endUtc = startUtc.AddDays(1);

        var all = await _db.Showtimes
            .Where(s => s.MovieId == id && s.StartTime >= startUtc && s.StartTime < endUtc
                        && s.Room.Cinema.City.Name == city)
            .Include(s => s.Room).ThenInclude(r => r.Cinema)
            .OrderBy(s => s.StartTime)
            .ToListAsync();

        var formats = all.Select(s => $"{s.Format} {s.LanguageMode}").Distinct().OrderBy(x => x).ToList();
        var selFmt = fmt != null && formats.Contains(fmt) ? fmt : formats.FirstOrDefault();
        var list = selFmt == null
            ? new List<Showtime>()
            : all.Where(s => $"{s.Format} {s.LanguageMode}" == selFmt).ToList();

        var nowUtc = DateTime.UtcNow;
        ViewBag.Schedule = new ScheduleViewModel
        {
            City = city,
            SelectedDate = selected,
            Days = Enumerable.Range(0, 14).Select(i => today.AddDays(i)).ToList(),
            Formats = formats,
            SelectedFormat = selFmt,
            Cinemas = list.GroupBy(s => s.Room.Cinema).OrderBy(g => g.Key.Name)
                .Select(g => new CinemaSchedule
                {
                    Name = g.Key.Name,
                    Address = g.Key.Address,
                    Groups = g.GroupBy(s => s.Room.Type).OrderBy(x => x.Key)
                        .Select(rg => new RoomGroup
                        {
                            RoomType = rg.Key,
                            Slots = rg.Select(s => new ShowtimeSlot
                            {
                                Id = s.Id,
                                Time = s.StartTime.AddHours(7).ToString("HH:mm"),
                                Past = s.StartTime <= nowUtc,
                                Price = s.EffectivePrice, Promo = s.PromoLabel
                            }).ToList()
                        }).ToList()
                }).ToList()
        };

        return View(movie);
    }
}