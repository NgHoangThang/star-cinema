using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class AdminProductRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string ImageUrl { get; set; } = "";
    public string Badge { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public int SoldQty { get; set; }      // tổng số lượng trong các đơn đã thanh toán
    public int UsedInOrders { get; set; } // số dòng đơn từng chứa sản phẩm (kể cả đơn hủy)
}

public class ProductForm
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; } = 50000;
    public string Badge { get; set; } = "";
    public int SortOrder { get; set; } = 10;
    public bool IsActive { get; set; } = true;
    public string ImageUrl { get; set; } = "";
}

[Authorize]
[Route("Admin/Products")]
public class AdminProductsController : Controller
{
    private const long MaxImageBytes = 2 * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AdminProductsController> _log;

    public AdminProductsController(AppDbContext db, IWebHostEnvironment env, ILogger<AdminProductsController> log)
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

    private void Audit(string action, string target, string detail) =>
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = User.Identity?.Name ?? "",
            Action = action,
            Target = target,
            Detail = detail
        });

    // ================= Danh sách =================
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var list = await _db.Products.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();

        var sold = await _db.BookingItems.Where(i => i.Booking.Status == "Paid")
            .GroupBy(i => i.ProductId).Select(g => new { g.Key, Q = g.Sum(i => i.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Q);
        var used = await _db.BookingItems
            .GroupBy(i => i.ProductId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);

        var rows = list.Select(p => new AdminProductRow
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            Price = p.Price,
            ImageUrl = p.ImageUrl,
            Badge = p.Badge,
            SortOrder = p.SortOrder,
            IsActive = p.IsActive,
            SoldQty = sold.GetValueOrDefault(p.Id),
            UsedInOrders = used.GetValueOrDefault(p.Id)
        }).ToList();

        return View("~/Views/Admin/Products.cshtml", rows);
    }

    // ================= Thêm / sửa =================
    private IActionResult FormView(ProductForm f, string? error = null)
    {
        ViewBag.Error = error;
        return View("~/Views/Admin/ProductEdit.cshtml", f);
    }

    [HttpGet("New")]
    public async Task<IActionResult> New()
    {
        int next = (await _db.Products.MaxAsync(p => (int?)p.SortOrder) ?? 0) + 1;
        return FormView(new ProductForm { SortOrder = next });
    }

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        var p = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        return FormView(new ProductForm
        {
            Id = p.Id, Name = p.Name, Description = p.Description, Price = p.Price,
            Badge = p.Badge, SortOrder = p.SortOrder, IsActive = p.IsActive, ImageUrl = p.ImageUrl
        });
    }

    // Kiểm tra "chữ ký" đầu file để chắc chắn đúng là ảnh PNG, JPG hoặc WEBP
    private static async Task<string?> DetectImageExt(IFormFile file)
    {
        var head = new byte[12];
        await using var s = file.OpenReadStream();
        int n = await s.ReadAsync(head, 0, head.Length);
        if (n < 12) return null;
        if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return ".png";
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
        if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
            head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P') return ".webp";
        return null;
    }

    private void DeleteUploaded(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        var clean = url.Split('?')[0];
        if (!clean.StartsWith("/uploads/products/")) return;   // ảnh có sẵn trong images/combos thì không xóa
        var path = Path.Combine(_env.WebRootPath, clean.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (Exception ex) { _log.LogWarning(ex, "Không xóa được ảnh {Path}", path); }
    }

    [HttpPost("Save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ProductForm f, IFormFile? image, bool removeImage = false)
    {
        f.Name = (f.Name ?? "").Trim();
        f.Description = (f.Description ?? "").Trim();
        f.Badge = (f.Badge ?? "").Trim();

        var errs = new List<string>();
        if (f.Name.Length < 2 || f.Name.Length > 80) errs.Add("Tên sản phẩm từ 2 đến 80 ký tự.");
        if (f.Description.Length > 200) errs.Add("Mô tả tối đa 200 ký tự.");
        if (f.Badge.Length > 20) errs.Add("Nhãn tối đa 20 ký tự (ví dụ: Bán chạy, Mới).");
        if (f.Price < 1000 || f.Price > 2000000 || f.Price % 1000 != 0) errs.Add("Giá phải là bội số của 1.000 đ, từ 1.000 đến 2.000.000.");
        if (f.SortOrder < 0 || f.SortOrder > 9999) errs.Add("Thứ tự hiển thị từ 0 đến 9999.");

        Product? p = null;
        if (f.Id != 0)
        {
            p = await _db.Products.FirstOrDefaultAsync(x => x.Id == f.Id);
            if (p == null) return NotFound();
            f.ImageUrl = p.ImageUrl;
        }

        if (errs.Count == 0)
        {
            var low = f.Name.ToLower();
            if (await _db.Products.AnyAsync(x => x.Id != f.Id && x.Name.ToLower() == low))
                errs.Add("Đã có sản phẩm trùng tên.");
        }

        string? newExt = null;
        if (image != null && image.Length > 0)
        {
            if (image.Length > MaxImageBytes) errs.Add("Ảnh tối đa 2 MB.");
            else
            {
                newExt = await DetectImageExt(image);
                if (newExt == null) errs.Add("Ảnh phải là PNG, JPG hoặc WEBP hợp lệ.");
            }
        }

        if (errs.Count > 0) return FormView(f, string.Join(" ", errs));

        string oldUrl = p?.ImageUrl ?? "";
        string newUrl = oldUrl;

        if (newExt != null)
        {
            var dir = Path.Combine(_env.WebRootPath, "uploads", "products");
            Directory.CreateDirectory(dir);
            var fileName = Guid.NewGuid().ToString("N") + newExt;
            await using (var fs = new FileStream(Path.Combine(dir, fileName), FileMode.Create))
                await image!.CopyToAsync(fs);
            newUrl = "/uploads/products/" + fileName;
        }
        else if (removeImage)
        {
            newUrl = "";
        }

        bool isNew = p == null;
        p ??= new Product();
        p.Name = f.Name;
        p.Description = f.Description;
        p.Price = f.Price;
        p.Badge = f.Badge;
        p.SortOrder = f.SortOrder;
        p.IsActive = f.IsActive;
        p.ImageUrl = newUrl;
        if (isNew) _db.Products.Add(p);
        await _db.SaveChangesAsync();

        if (newUrl != oldUrl) DeleteUploaded(oldUrl);

        Audit(isNew ? "ProductCreate" : "ProductEdit", "Product:" + p.Id,
            $"{p.Name}; {p.Price:N0} đ; thứ tự {p.SortOrder}; {(p.IsActive ? "đang bán" : "đang ẩn")}");
        await _db.SaveChangesAsync();

        TempData["AdminMsg"] = (isNew ? "Đã thêm " : "Đã lưu ") + "\"" + p.Name + "\".";
        return RedirectToAction(nameof(Index));
    }

    // ================= Bật / tắt bán =================
    [HttpPost("Toggle/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        p.IsActive = !p.IsActive;
        Audit("ProductToggle", "Product:" + id, p.Name + " → " + (p.IsActive ? "bán" : "ẩn"));
        await _db.SaveChangesAsync();
        TempData["AdminMsg"] = "\"" + p.Name + "\" " + (p.IsActive ? "đã được bán lại." : "đã được ẩn khỏi trang chọn bắp nước.");
        return RedirectToAction(nameof(Index));
    }

    // ================= Xóa =================
    // Sản phẩm từng nằm trong đơn thì không xóa (giữ lịch sử đơn), chỉ được ẩn
    [HttpPost("Delete/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id);
        if (p == null)
        {
            TempData["AdminMsg"] = "Không tìm thấy sản phẩm.";
            return RedirectToAction(nameof(Index));
        }

        int used = await _db.BookingItems.CountAsync(i => i.ProductId == id);
        if (used > 0)
        {
            TempData["AdminMsg"] = $"Không xóa được \"{p.Name}\" vì đã nằm trong {used} dòng đơn hàng. Hãy bấm Ẩn để ngừng bán.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            string name = p.Name, img = p.ImageUrl;
            _db.Products.Remove(p);
            Audit("ProductDelete", "Product:" + id, name);
            await _db.SaveChangesAsync();
            DeleteUploaded(img);
            TempData["AdminMsg"] = "Đã xóa \"" + name + "\".";
        }
        catch (DbUpdateException ex)
        {
            _log.LogError(ex, "Xóa sản phẩm {Id} thất bại", id);
            TempData["AdminMsg"] = "Không xóa được sản phẩm này vì còn dữ liệu liên quan.";
        }
        return RedirectToAction(nameof(Index));
    }
}