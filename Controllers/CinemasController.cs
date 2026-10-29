using System.Globalization;
using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class CinemasController : Controller
{
    private readonly AppDbContext _db;

    public CinemasController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        var city = Request.Cookies["city"] ?? "Hồ Chí Minh";
        var cinemas = await _db.Cinemas
            .Include(c => c.City).Include(c => c.Rooms)
            .Where(c => c.City.Name == city)
            .OrderBy(c => c.Name).ToListAsync();

        ViewBag.City = city;
        ViewBag.Cities = await _db.Cities.OrderBy(c => c.Name).ToListAsync();
        return View(cinemas);
    }

    public async Task<IActionResult> Details(int id, string? date)
    {
        var cinema = await _db.Cinemas.Include(c => c.City).Include(c => c.Rooms)
                              .FirstOrDefaultAsync(c => c.Id == id);
        if (cinema == null) return NotFound();

        var today = DateTime.UtcNow.AddHours(7).Date;
        var selected = today;
        if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out var d))
            selected = d.Date;
        if (selected < today || selected > today.AddDays(13)) selected = today;

        var startUtc = DateTime.SpecifyKind(selected.AddHours(-7), DateTimeKind.Utc);
        var endUtc = startUtc.AddDays(1);
        var nowUtc = DateTime.UtcNow;

        var shows = await _db.Showtimes
            .Where(s => s.Room.CinemaId == id && s.StartTime >= startUtc && s.StartTime < endUtc)
            .Include(s => s.Movie).Include(s => s.Room)
            .OrderBy(s => s.StartTime).ToListAsync();

        var vm = new CinemaDetailsViewModel
        {
            Cinema = cinema,
            SelectedDate = selected,
            Days = Enumerable.Range(0, 14).Select(i => today.AddDays(i)).ToList(),
            Movies = shows.GroupBy(s => s.MovieId)
                .Select(g => new MovieShows
                {
                    Movie = g.First().Movie,
                    Formats = g.GroupBy(s => $"Rạp {s.Room.Type} · {s.Format} {s.LanguageMode}")
                        .OrderBy(f => f.Key)
                        .Select(f => new FormatShows
                        {
                            Label = f.Key,
                            Slots = f.Select(s => new ShowtimeSlot
                            {
                                Id = s.Id,
                                Time = s.StartTime.AddHours(7).ToString("HH:mm"),
                                Past = s.StartTime <= nowUtc,
                                Price = s.EffectivePrice, Promo = s.PromoLabel
                            }).ToList()
                        }).ToList()
                })
                .OrderByDescending(m => m.Movie.ReleaseDate).ToList()
        };
        return View(vm);
    }
}