using CinemaApp.Models;

namespace CinemaApp.Data;

public static class ProductSeeder
{
    static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".webp" };

    public static void Seed(AppDbContext db)
    {
        if (db.Products.Any()) return;

        db.Products.AddRange(
            new Product { SortOrder = 1, Name = "Combo Solo", Price = 79000, Description = "1 bắp ngọt vừa, 1 nước ngọt lớn" },
            new Product { SortOrder = 2, Name = "Combo Đôi", Price = 105000, Badge = "Bán chạy", Description = "1 bắp ngọt lớn, 2 nước ngọt lớn" },
            new Product { SortOrder = 3, Name = "Combo Nhóm", Price = 199000, Description = "2 bắp ngọt lớn, 4 nước ngọt lớn" },
            new Product { SortOrder = 4, Name = "Bắp ngọt lớn", Price = 55000, Description = "Bắp rang vị ngọt, hộp lớn" },
            new Product { SortOrder = 5, Name = "Bắp phô mai", Price = 65000, Badge = "Mới", Description = "Bắp rang phủ bột phô mai, hộp lớn" },
            new Product { SortOrder = 6, Name = "Nước ngọt lớn", Price = 39000, Description = "Ly lớn, chọn vị tại quầy" },
            new Product { SortOrder = 7, Name = "Snack khoai tây", Price = 29000, Description = "Gói snack khoai tây vị truyền thống" }
        );
        db.SaveChanges();
    }

    // Gắn ảnh trong wwwroot/images/combos theo thứ tự sản phẩm: 1.png ứng với sản phẩm SortOrder = 1...
    // Đổi ảnh (cùng tên) thì tự cập nhật, trình duyệt không giữ ảnh cũ.
    public static void AttachImages(AppDbContext db, string webRoot)
    {
        var dir = Path.Combine(webRoot, "images", "combos");
        if (!Directory.Exists(dir)) return;

        bool changed = false;
        foreach (var p in db.Products.ToList())
        {
            foreach (var ext in Exts)
            {
                var file = Path.Combine(dir, p.SortOrder + ext);
                if (!File.Exists(file)) continue;

                var url = "/images/combos/" + p.SortOrder + ext + "?v=" + File.GetLastWriteTimeUtc(file).Ticks;
                if (p.ImageUrl != url && (string.IsNullOrEmpty(p.ImageUrl) || p.ImageUrl.StartsWith("/images/combos/"))) { p.ImageUrl = url; changed = true; }
                break;
            }
        }
        if (changed) db.SaveChanges();
    }
}