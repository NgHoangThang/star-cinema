using System.Diagnostics;
using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CinemaApp.Controllers;

public class HomeController : Controller
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public HomeController(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<IActionResult> Index()
    {
        var vm = await _cache.GetOrCreateAsync("home-vm", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);

            var now = await _db.Movies.AsNoTracking()
                .Where(m => m.Status == "NowShowing")
                .OrderByDescending(m => m.ReleaseDate).ToListAsync();

            return new HomeViewModel
            {
                Banners = await _db.Banners.AsNoTracking()
                    .Where(b => b.IsActive).OrderBy(b => b.SortOrder).ToListAsync(),
                NowShowing = now,
                ComingSoon = await _db.Movies.AsNoTracking()
                    .Where(m => m.Status == "ComingSoon").OrderBy(m => m.ReleaseDate).ToListAsync(),
                Hero = now.Where(m => m.BackdropUrl.StartsWith("http")).Take(5).ToList()
            };
        });

        return View(vm);
    }

    public IActionResult Privacy() => View();
    
    public IActionResult StatusPage(int code)
    {
        ViewBag.Code = code;
        return View();
    }

    public IActionResult SetCity(string name, string? returnUrl)
    {
        Response.Cookies.Append("city", name, new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddDays(30)
        });
        var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        return LocalRedirect(target);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}