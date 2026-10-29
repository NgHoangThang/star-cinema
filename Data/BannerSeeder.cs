using CinemaApp.Models;

namespace CinemaApp.Data;

public static class BannerSeeder
{
    static readonly string[] Exts = { ".jpg", ".jpeg", ".png", ".webp" };

    public static void Seed(AppDbContext db, string webRoot)
    {
        var dir = Path.Combine(webRoot, "images", "banners");
        if (!Directory.Exists(dir)) return;

        var files = Directory.GetFiles(dir)
            .Where(f => Exts.Contains(Path.GetExtension(f).ToLower()))
            .OrderBy(f => f).ToList();
        if (files.Count == 0) return;

        db.Banners.RemoveRange(db.Banners.Where(b => b.ImageUrl.StartsWith("https://placehold.co")));
        db.SaveChanges();

        int order = db.Banners.Count();
        foreach (var f in files)
        {
            var url = "/images/banners/" + Path.GetFileName(f);
            if (db.Banners.Any(b => b.ImageUrl == url)) continue;
            db.Banners.Add(new Banner
            {
                Title = Path.GetFileNameWithoutExtension(f),
                ImageUrl = url,
                SortOrder = ++order
            });
        }
        db.SaveChanges();
    }
}