using System.Text.RegularExpressions;
using CinemaApp.Data;
using CinemaApp.Models;
using CinemaApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Controllers;

public class AdminCinemaRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public string Address { get; set; } = "";
    public string Hotline { get; set; } = "";
    public int Rooms { get; set; }
    public int Seats { get; set; }
    public int Showtimes { get; set; }       // mọi suất từng tạo
    public int Upcoming { get; set; }        // suất còn ở tương lai
}

public class CinemaForm
{
    public int Id { get; set; }
    public int CityId { get; set; }
    public string NewCity { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Hotline { get; set; } = "";
}

public class AdminRoomRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public int Seats { get; set; }
    public int Rows { get; set; }
    public int VipSeats { get; set; }
    public int Showtimes { get; set; }
    public int Upcoming { get; set; }
}

[Authorize]
[Route("Admin/Cinemas")]
public class AdminCinemasController : Controller
{
    private const int PageSize = 25;
    private static readonly string[] RoomTypes = { "2D", "4DX", "GOLDCLASS", "SCREENX" };
    private static readonly Regex HotlineRx = new(@"^[0-9 .\-+()]{6,20}$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly ILogger<AdminCinemasController> _log;

    public AdminCinemasController(AppDbContext db, ILogger<AdminCinemasController> log)
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

    private void Audit(string action, string target, string detail) =>
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            At = DateTime.UtcNow,
            AdminEmail = User.Identity?.Name ?? "",
            Action = action,
            Target = target,
            Detail = detail
        });

    // ================= Danh sách rạp =================
    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, int? cityId, int page = 1)
    {
        var query = _db.Cinemas.AsNoTracking().Include(c => c.City).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var key = q.Trim().ToLower();
            query = query.Where(c => c.Name.ToLower().Contains(key) || c.Address.ToLower().Contains(key));
        }
        if (cityId.HasValue && cityId > 0) query = query.Where(c => c.CityId == cityId.Value);

        int total = await query.CountAsync();
        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, pages);

        var list = await query.OrderBy(c => c.City.Name).ThenBy(c => c.Name)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();
        var ids = list.Select(c => c.Id).ToList();
        var now = DateTime.UtcNow;

        var roomCounts = await _db.Rooms.Where(r => ids.Contains(r.CinemaId))
            .GroupBy(r => r.CinemaId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var seatCounts = await _db.Seats.Where(s => ids.Contains(s.Room.CinemaId))
            .GroupBy(s => s.Room.CinemaId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var showCounts = await _db.Showtimes.Where(s => ids.Contains(s.Room.CinemaId))
            .GroupBy(s => s.Room.CinemaId)
            .Select(g => new { g.Key, All = g.Count(), Up = g.Count(s => s.StartTime > now) })
            .ToDictionaryAsync(x => x.Key);

        var rows = list.Select(c => new AdminCinemaRow
        {
            Id = c.Id,
            Name = c.Name,
            City = c.City.Name,
            Address = c.Address,
            Hotline = c.Hotline,
            Rooms = roomCounts.GetValueOrDefault(c.Id),
            Seats = seatCounts.GetValueOrDefault(c.Id),
            Showtimes = showCounts.TryGetValue(c.Id, out var sc) ? sc.All : 0,
            Upcoming = showCounts.TryGetValue(c.Id, out var sc2) ? sc2.Up : 0
        }).ToList();

        ViewBag.Q = q;
        ViewBag.CityId = cityId;
        ViewBag.Page = page;
        ViewBag.Pages = pages;
        ViewBag.Total = total;
        ViewBag.Cities = await _db.Cities.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        return View("~/Views/Admin/Cinemas.cshtml", rows);
    }

    // ================= Thêm / sửa rạp =================
    private async Task<IActionResult> CinemaFormView(CinemaForm f, string? error = null)
    {
        ViewBag.Cities = await _db.Cities.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        ViewBag.Error = error;
        return View("~/Views/Admin/CinemaEdit.cshtml", f);
    }

    [HttpGet("New")]
    public Task<IActionResult> New() => CinemaFormView(new CinemaForm());

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        var c = await _db.Cinemas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        return await CinemaFormView(new CinemaForm { Id = c.Id, CityId = c.CityId, Name = c.Name, Address = c.Address, Hotline = c.Hotline });
    }

    [HttpPost("SaveCinema"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCinema(CinemaForm f)
    {
        f.Name = (f.Name ?? "").Trim();
        f.Address = (f.Address ?? "").Trim();
        f.Hotline = (f.Hotline ?? "").Trim();
        f.NewCity = (f.NewCity ?? "").Trim();

        var errs = new List<string>();
        if (f.Name.Length < 3 || f.Name.Length > 100) errs.Add("Tên rạp từ 3 đến 100 ký tự.");
        if (f.Address.Length < 5 || f.Address.Length > 200) errs.Add("Địa chỉ từ 5 đến 200 ký tự.");
        if (!HotlineRx.IsMatch(f.Hotline)) errs.Add("Số hotline chỉ gồm chữ số, dấu cách và + - . ( ), từ 6 đến 20 ký tự.");

        City? city = null;
        if (f.NewCity.Length > 0)
        {
            if (f.NewCity.Length < 2 || f.NewCity.Length > 60) errs.Add("Tên thành phố mới từ 2 đến 60 ký tự.");
            else
            {
                var low = f.NewCity.ToLower();
                city = await _db.Cities.FirstOrDefaultAsync(c => c.Name.ToLower() == low);   // có rồi thì dùng lại
            }
        }
        else
        {
            city = await _db.Cities.FirstOrDefaultAsync(c => c.Id == f.CityId);
            if (city == null) errs.Add("Chưa chọn thành phố.");
        }

        Cinema? cinema = null;
        if (f.Id != 0)
        {
            cinema = await _db.Cinemas.FirstOrDefaultAsync(c => c.Id == f.Id);
            if (cinema == null) return NotFound();
        }

        if (errs.Count == 0)
        {
            var low = f.Name.ToLower();
            if (await _db.Cinemas.AnyAsync(c => c.Id != f.Id && c.Name.ToLower() == low))
                errs.Add("Đã có rạp trùng tên.");
        }

        if (errs.Count > 0) return await CinemaFormView(f, string.Join(" ", errs));

        if (city == null)
        {
            city = new City { Name = f.NewCity };
            _db.Cities.Add(city);
            await _db.SaveChangesAsync();
        }

        bool isNew = cinema == null;
        cinema ??= new Cinema();
        cinema.CityId = city.Id;
        cinema.Name = f.Name;
        cinema.Address = f.Address;
        cinema.Hotline = f.Hotline;
        if (isNew) _db.Cinemas.Add(cinema);
        await _db.SaveChangesAsync();

        Audit(isNew ? "CinemaCreate" : "CinemaEdit", "Cinema:" + cinema.Id, $"{cinema.Name}; {city.Name}; {cinema.Address}; {cinema.Hotline}");
        await _db.SaveChangesAsync();

        TempData["AdminMsg"] = isNew
            ? "Đã thêm rạp \"" + cinema.Name + "\". Hãy thêm phòng chiếu cho rạp này."
            : "Đã lưu rạp \"" + cinema.Name + "\".";
        return isNew ? RedirectToAction(nameof(Rooms), new { id = cinema.Id }) : RedirectToAction(nameof(Index));
    }

    // ================= Xóa rạp =================
    // Rạp đã từng có suất chiếu thì không xóa (giữ lịch sử đơn vé); rạp còn nhân viên được gán cũng không xóa
    [HttpPost("Delete/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.Cinemas.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null)
        {
            TempData["AdminMsg"] = "Không tìm thấy rạp.";
            return RedirectToAction(nameof(Index));
        }

        int shows = await _db.Showtimes.CountAsync(s => s.Room.CinemaId == id);
        if (shows > 0)
        {
            TempData["AdminMsg"] = $"Không xóa được \"{c.Name}\" vì rạp đã có {shows} suất chiếu (giữ lại để bảo toàn lịch sử đơn vé).";
            return RedirectToAction(nameof(Index));
        }
        if (await _db.Users.AnyAsync(u => u.StaffCinemaId == id))
        {
            TempData["AdminMsg"] = "Không xóa được \"" + c.Name + "\" vì còn nhân viên được gán cho rạp này. Hãy gỡ hoặc đổi rạp của nhân viên trước.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            string name = c.Name;
            await _db.Seats.Where(s => s.Room.CinemaId == id).ExecuteDeleteAsync();
            await _db.Rooms.Where(r => r.CinemaId == id).ExecuteDeleteAsync();
            _db.Cinemas.Remove(c);
            Audit("CinemaDelete", "Cinema:" + id, name);
            await _db.SaveChangesAsync();
            TempData["AdminMsg"] = "Đã xóa rạp \"" + name + "\".";
        }
        catch (DbUpdateException ex)
        {
            _log.LogError(ex, "Xóa rạp {Id} thất bại", id);
            TempData["AdminMsg"] = "Không xóa được rạp này vì còn dữ liệu liên quan.";
        }
        return RedirectToAction(nameof(Index));
    }

    // ================= Danh sách phòng của một rạp =================
    [HttpGet("Rooms/{id:int}")]
    public async Task<IActionResult> Rooms(int id)
    {
        var cinema = await _db.Cinemas.AsNoTracking().Include(c => c.City).FirstOrDefaultAsync(c => c.Id == id);
        if (cinema == null) return NotFound();

        var rooms = await _db.Rooms.AsNoTracking().Where(r => r.CinemaId == id).OrderBy(r => r.Name).ToListAsync();
        var rids = rooms.Select(r => r.Id).ToList();
        var now = DateTime.UtcNow;

        var seatInfo = await _db.Seats.Where(s => rids.Contains(s.RoomId))
            .GroupBy(s => s.RoomId)
            .Select(g => new
            {
                Id = g.Key,
                N = g.Count(),
                Rows = g.Select(s => s.RowLabel).Distinct().Count(),
                Vip = g.Count(s => s.SeatType == "VIP")
            }).ToDictionaryAsync(x => x.Id);
        var showInfo = await _db.Showtimes.Where(s => rids.Contains(s.RoomId))
            .GroupBy(s => s.RoomId)
            .Select(g => new { Id = g.Key, All = g.Count(), Up = g.Count(s => s.StartTime > now) })
            .ToDictionaryAsync(x => x.Id);

        var rows = rooms.Select(r => new AdminRoomRow
        {
            Id = r.Id,
            Name = r.Name,
            Type = r.Type,
            Seats = seatInfo.TryGetValue(r.Id, out var si) ? si.N : 0,
            Rows = seatInfo.TryGetValue(r.Id, out var si2) ? si2.Rows : 0,
            VipSeats = seatInfo.TryGetValue(r.Id, out var si3) ? si3.Vip : 0,
            Showtimes = showInfo.TryGetValue(r.Id, out var sh) ? sh.All : 0,
            Upcoming = showInfo.TryGetValue(r.Id, out var sh2) ? sh2.Up : 0
        }).ToList();

        ViewBag.Cinema = cinema;
        ViewBag.Types = RoomTypes;
        return View("~/Views/Admin/CinemaRooms.cshtml", rows);
    }

    private IActionResult ToRooms(int cinemaId, string msg)
    {
        TempData["AdminMsg"] = msg;
        return RedirectToAction(nameof(Rooms), new { id = cinemaId });
    }

    // Kiểm tra cấu hình sơ đồ ghế; trả về thông báo lỗi hoặc null
    private static string? LayoutError(int rows, int perRow, int vipFromRow)
    {
        if (rows < 3 || rows > 12) return "Số hàng ghế từ 3 đến 12.";
        if (perRow < 6 || perRow > 20) return "Số ghế mỗi hàng từ 6 đến 20.";
        if (vipFromRow < 0 || vipFromRow > rows) return "Hàng bắt đầu ghế VIP phải từ 1 đến số hàng (0 nếu không có VIP).";
        return null;
    }

    // ================= Thêm phòng =================
    [HttpPost("Rooms/{id:int}/Add"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRoom(int id, string? name, string? type, int rows, int perRow, int vipFromRow)
    {
        var cinema = await _db.Cinemas.FirstOrDefaultAsync(c => c.Id == id);
        if (cinema == null) return NotFound();

        name = (name ?? "").Trim();
        if (name.Length < 2 || name.Length > 40) return ToRooms(id, "Tên phòng từ 2 đến 40 ký tự.");
        if (type == null || !RoomTypes.Contains(type)) return ToRooms(id, "Loại phòng không hợp lệ.");
        var le = LayoutError(rows, perRow, vipFromRow);
        if (le != null) return ToRooms(id, le);

        var low = name.ToLower();
        if (await _db.Rooms.AnyAsync(r => r.CinemaId == id && r.Name.ToLower() == low))
            return ToRooms(id, "Rạp này đã có phòng tên \"" + name + "\".");

        await using var tx = await _db.Database.BeginTransactionAsync();
        var room = new Room { CinemaId = id, Name = name, Type = type };
        _db.Rooms.Add(room);
        await _db.SaveChangesAsync();
        _db.Seats.AddRange(SeatSeeder.Build(room.Id, rows, perRow, vipFromRow));
        Audit("RoomCreate", "Room:" + room.Id, $"{cinema.Name} · {name} · {type}; {rows}×{perRow} = {rows * perRow} ghế");
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return ToRooms(id, $"Đã thêm {name} ({type}, {rows * perRow} ghế).");
    }

    // ================= Đổi tên / loại phòng =================
    // Đổi loại phòng làm đổi định dạng và giá gợi ý của suất chiếu, nên chỉ được khi phòng chưa có suất nào
    [HttpPost("Room/{roomId:int}/Edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRoom(int roomId, string? name, string? type)
    {
        var room = await _db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId);
        if (room == null) return NotFound();
        int cid = room.CinemaId;

        name = (name ?? "").Trim();
        if (name.Length < 2 || name.Length > 40) return ToRooms(cid, "Tên phòng từ 2 đến 40 ký tự.");
        if (type == null || !RoomTypes.Contains(type)) return ToRooms(cid, "Loại phòng không hợp lệ.");

        var low = name.ToLower();
        if (await _db.Rooms.AnyAsync(r => r.CinemaId == cid && r.Id != roomId && r.Name.ToLower() == low))
            return ToRooms(cid, "Rạp này đã có phòng tên \"" + name + "\".");

        if (type != room.Type && await _db.Showtimes.AnyAsync(s => s.RoomId == roomId))
            return ToRooms(cid, "Phòng đã có suất chiếu nên không đổi được loại phòng. Bạn vẫn đổi được tên.");

        string old = room.Name + " · " + room.Type;
        room.Name = name;
        room.Type = type;
        Audit("RoomEdit", "Room:" + roomId, $"{old} → {name} · {type}");
        await _db.SaveChangesAsync();
        return ToRooms(cid, "Đã lưu phòng " + name + ".");
    }

    // ================= Tạo lại sơ đồ ghế =================
    // Ghế của suất chiếu tham chiếu đến ghế của phòng, nên chỉ làm lại sơ đồ khi phòng chưa có suất nào
    [HttpPost("Room/{roomId:int}/Layout"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Layout(int roomId, int rows, int perRow, int vipFromRow)
    {
        var room = await _db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId);
        if (room == null) return NotFound();
        int cid = room.CinemaId;

        var le = LayoutError(rows, perRow, vipFromRow);
        if (le != null) return ToRooms(cid, le);
        if (await _db.Showtimes.AnyAsync(s => s.RoomId == roomId))
            return ToRooms(cid, "Phòng đã có suất chiếu nên không đổi được sơ đồ ghế.");

        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.Seats.Where(s => s.RoomId == roomId).ExecuteDeleteAsync();
        _db.Seats.AddRange(SeatSeeder.Build(roomId, rows, perRow, vipFromRow));
        Audit("RoomLayout", "Room:" + roomId, $"{room.Name}: {rows}×{perRow} = {rows * perRow} ghế, VIP từ hàng {(vipFromRow == 0 ? "không có" : vipFromRow.ToString())}");
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return ToRooms(cid, $"Đã tạo lại sơ đồ ghế {room.Name}: {rows * perRow} ghế.");
    }

    // ================= Xóa phòng =================
    [HttpPost("Room/{roomId:int}/Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRoom(int roomId)
    {
        var room = await _db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId);
        if (room == null) return NotFound();
        int cid = room.CinemaId;

        int shows = await _db.Showtimes.CountAsync(s => s.RoomId == roomId);
        if (shows > 0)
            return ToRooms(cid, $"Không xóa được {room.Name} vì phòng đã có {shows} suất chiếu (giữ lại để bảo toàn lịch sử đơn vé).");

        try
        {
            string name = room.Name;
            await _db.Seats.Where(s => s.RoomId == roomId).ExecuteDeleteAsync();
            _db.Rooms.Remove(room);
            Audit("RoomDelete", "Room:" + roomId, name);
            await _db.SaveChangesAsync();
            return ToRooms(cid, "Đã xóa " + name + ".");
        }
        catch (DbUpdateException ex)
        {
            _log.LogError(ex, "Xóa phòng {Id} thất bại", roomId);
            return ToRooms(cid, "Không xóa được phòng này vì còn dữ liệu liên quan.");
        }
    }
}