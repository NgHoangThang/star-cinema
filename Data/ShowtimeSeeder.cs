using CinemaApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Data;

public static class ShowtimeSeeder
{
    // Việt Nam UTC+7. Database lưu UTC, hiển thị cộng 7 giờ.
    static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local.AddHours(-7), DateTimeKind.Utc);

    static readonly string[] BigCities = { "Hồ Chí Minh", "Hà Nội", "Đà Nẵng" };
    static readonly string[] BigLayout = { "2D", "2D", "2D", "2D", "4DX", "GOLDCLASS", "SCREENX", "2D" };
    static readonly string[] SmallLayout = { "2D", "2D", "2D", "4DX", "GOLDCLASS" };

    static readonly (string City, string Name, string Address)[] ExtraCinemas =
    {
        ("Hồ Chí Minh", "Star Cinema Quận 3", "45 Võ Văn Tần, Quận 3"),
        ("Hồ Chí Minh", "Star Cinema Thủ Đức", "120 Võ Văn Ngân, TP. Thủ Đức"),
        ("Hồ Chí Minh", "Star Cinema Gò Vấp", "33 Quang Trung, Gò Vấp"),
        ("Hồ Chí Minh", "Star Cinema Tân Bình", "77 Cộng Hòa, Tân Bình"),
        ("Hà Nội", "Star Cinema Cầu Giấy", "9 Trần Thái Tông, Cầu Giấy"),
        ("Hà Nội", "Star Cinema Long Biên", "18 Nguyễn Văn Cừ, Long Biên"),
        ("Hà Nội", "Star Cinema Hà Đông", "52 Quang Trung, Hà Đông"),
        ("Hà Nội", "Star Cinema Đống Đa", "101 Tây Sơn, Đống Đa"),
        ("Đà Nẵng", "Star Cinema Sơn Trà", "66 Võ Nguyên Giáp, Sơn Trà"),
    };

   public static void Seed(AppDbContext db, bool ensureStructure = true)
    {
        bool changed = ensureStructure && EnsureCinemas(db);
        changed |= ensureStructure && EnsureRooms(db);
        db.ChangeTracker.Clear();

        // Có thêm rạp hoặc phòng thì xếp lại từ đầu (chưa có đơn đặt vé nên xóa an toàn)
        if (changed && !db.Bookings.Any()) db.Showtimes.ExecuteDelete();

        var movies = db.Movies.Where(m => m.Status == "NowShowing").OrderBy(m => m.Id).ToList();
        if (movies.Count == 0) return;

        var byCinema = db.Rooms.AsNoTracking().OrderBy(r => r.CinemaId).ThenBy(r => r.Id).ToList()
                         .GroupBy(r => r.CinemaId).ToList();

        var today = DateTime.UtcNow.AddHours(7).Date;
        var epoch = new DateTime(2026, 1, 1);

        // Bù thêm mọi ngày còn thiếu trong 14 ngày tới
        for (int day = 0; day < 14; day++)
        {
            var date = today.AddDays(day);
            var from = ToUtc(date);
            var to = from.AddDays(1);
            if (db.Showtimes.Any(s => s.StartTime >= from && s.StartTime < to)) continue;

            int dayIndex = (date - epoch).Days;
            bool weekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
            var batch = new List<Showtime>();

            foreach (var g in byCinema)
            {
                int k = dayIndex * 3 + g.Key;   // xoay vòng phim, mỗi rạp mỗi ngày bắt đầu khác nhau
                int roomPos = 0;

                foreach (var room in g)
                {
                    var t = date.AddHours(9).AddMinutes(roomPos * 10);
                    var last = date.AddHours(22).AddMinutes(30);

                    while (t <= last)
                    {
                        var m = movies[k % movies.Count];
                        int round = k / movies.Count;   // đổi Phụ đề / Lồng tiếng theo vòng
                        k++;

                        int dur = m.DurationMin > 0 ? m.DurationMin : 110;
                        var end = t.AddMinutes(dur + 15);   // cộng 15 phút quảng cáo

                        decimal price = room.Type switch
                        {
                            "4DX" => 153000,
                            "GOLDCLASS" => 180000,
                            "SCREENX" => 130000,
                            _ => 90000
                        };
                        if (weekend) price += 20000;

                                               if (ToUtc(t) > DateTime.UtcNow) batch.Add(new Showtime
                        {
                            MovieId = m.Id,
                            RoomId = room.Id,
                            StartTime = ToUtc(t),
                            EndTime = ToUtc(end),
                            Format = room.Type switch { "4DX" => "4DX2D", "SCREENX" => "SCREENX", _ => "2D" },
                            LanguageMode = round % 2 == 0 ? "Phụ đề Anh & Việt" : "Lồng tiếng Việt",
                            BasePrice = price
                        });

                        var next = end.AddMinutes(10);   // nghỉ 10 phút dọn phòng
                        int mod = next.Minute % 5;
                        if (mod != 0) next = next.AddMinutes(5 - mod);
                        t = next;
                    }
                    roomPos++;
                }
            }

            db.Showtimes.AddRange(batch);
            db.SaveChanges();
            db.ChangeTracker.Clear();
        }
    }

    static bool EnsureCinemas(AppDbContext db)
    {
        bool added = false;
        foreach (var x in ExtraCinemas)
        {
            var city = db.Cities.FirstOrDefault(c => c.Name == x.City);
            if (city == null) continue;
            if (db.Cinemas.Any(c => c.Name == x.Name)) continue;
            db.Cinemas.Add(new Cinema { CityId = city.Id, Name = x.Name, Address = x.Address, Hotline = "1900 1000" });
            added = true;
        }
        if (added) db.SaveChanges();
        return added;
    }

    static bool EnsureRooms(AppDbContext db)
    {
        bool added = false;
        var cinemas = db.Cinemas.Include(c => c.City).Include(c => c.Rooms).ToList();

        foreach (var c in cinemas)
        {
            var layout = BigCities.Contains(c.City.Name) ? BigLayout : SmallLayout;
            int count = c.Rooms.Count;

            foreach (var g in layout.GroupBy(x => x))
            {
                int have = c.Rooms.Count(r => r.Type == g.Key);
                for (int i = have; i < g.Count(); i++)
                {
                    count++;
                    var room = new Room { CinemaId = c.Id, Name = $"Phòng {count}", Type = g.Key };
                    db.Rooms.Add(room);
                    db.SaveChanges();
                    db.Seats.AddRange(SeatSeeder.BuildSeats(room.Id));
                    db.SaveChanges();
                    added = true;
                }
            }
        }
        return added;
    }
}