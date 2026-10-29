using CinemaApp.Data;
using CinemaApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class NewsListView
{
    public List<Article> Items { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public string? Category { get; set; }
    public int Page { get; set; }
    public int Pages { get; set; }
}

public class NewsDetailView
{
    public Article Article { get; set; } = null!;
    public List<Article> Related { get; set; } = new();
}

public class NewsController : Controller
{
    private const int PageSize = 9;

    private readonly AppDbContext _db;

    public NewsController(AppDbContext db) => _db = db;

    // Khách chỉ thấy bài đã đăng và đã đến giờ đăng
    private IQueryable<Article> Visible()
    {
        var now = DateTime.UtcNow;
        return _db.Articles.AsNoTracking()
            .Where(a => a.IsPublished && a.PublishedAt != null && a.PublishedAt <= now);
    }

    [HttpGet("News")]
    public async Task<IActionResult> Index(string? category, int page = 1)
    {
        var q = Visible();

        var cats = await q.Select(a => a.Category).Distinct().OrderBy(c => c).ToListAsync();
        if (!string.IsNullOrWhiteSpace(category) && cats.Contains(category))
            q = q.Where(a => a.Category == category);
        else
            category = null;

        int total = await q.CountAsync();
        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pages);

        var items = await q
            .OrderByDescending(a => a.PublishedAt).ThenByDescending(a => a.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .ToListAsync();

        return View(new NewsListView
        {
            Items = items,
            Categories = cats,
            Category = category,
            Page = page,
            Pages = pages
        });
    }

    [HttpGet("News/{slug}")]
    public async Task<IActionResult> Details(string slug)
    {
        var article = await Visible().FirstOrDefaultAsync(a => a.Slug == slug);
        if (article == null) return NotFound();

        var related = await Visible()
            .Where(a => a.Id != article.Id)
            .OrderByDescending(a => a.PublishedAt).ThenByDescending(a => a.Id)
            .Take(3).ToListAsync();

        return View(new NewsDetailView { Article = article, Related = related });
    }
}