using CinemaApp.Models;

namespace CinemaApp.Data;

public static class DbSeeder
{
    public static void Seed(AppDbContext db)
    {
        if (db.Cities.Any()) return;   // đã có dữ liệu thì không nạp lại

        // 1. Thành phố
        var hcm = new City { Name = "Hồ Chí Minh" };
        var hn = new City { Name = "Hà Nội" };
        var dn = new City { Name = "Đà Nẵng" };
        db.Cities.AddRange(hcm, hn, dn);
        db.SaveChanges();

        // 2. Rạp (tên tự đặt, không dùng tên thương hiệu thật)
        var cinemas = new List<Cinema>
        {
            new() { CityId = hcm.Id, Name = "Star Cinema Quận 1", Address = "12 Lê Lợi, Quận 1", Hotline = "1900 0001" },
            new() { CityId = hcm.Id, Name = "Star Cinema Quận 7", Address = "88 Nguyễn Văn Linh, Quận 7", Hotline = "1900 0002" },
            new() { CityId = hn.Id,  Name = "Star Cinema Hoàn Kiếm", Address = "5 Tràng Tiền, Hoàn Kiếm", Hotline = "1900 0003" },
            new() { CityId = dn.Id,  Name = "Star Cinema Hải Châu", Address = "20 Bạch Đằng, Hải Châu", Hotline = "1900 0004" },
        };
        db.Cinemas.AddRange(cinemas);
        db.SaveChanges();

        // 3. Mỗi rạp 3 phòng, mỗi phòng tự sinh ghế
        foreach (var c in cinemas)
        {
            var rooms = new List<Room>
            {
                new() { CinemaId = c.Id, Name = "Phòng 1", Type = "2D" },
                new() { CinemaId = c.Id, Name = "Phòng 2", Type = "4DX" },
                new() { CinemaId = c.Id, Name = "Phòng 3", Type = "GOLDCLASS" },
            };
            db.Rooms.AddRange(rooms);
            db.SaveChanges();

            foreach (var r in rooms)
                db.Seats.AddRange(SeatSeeder.BuildSeats(r.Id));
            db.SaveChanges();
        }
        // Phim không seed ở đây: TmdbImporter nạp phim thật từ TMDB
    }

    public static void SeedMoreCities(AppDbContext db)
    {
        var names = new[] {
            "Cần Thơ", "Hải Phòng", "Đồng Nai", "Quảng Ninh", "Bà Rịa - Vũng Tàu",
            "Khánh Hòa", "Nghệ An", "Bình Dương", "Thừa Thiên Huế", "Lâm Đồng",
            "Bình Định", "Thanh Hóa", "Quảng Nam", "Gia Lai", "Đắk Lắk",
            "An Giang", "Kiên Giang", "Tiền Giang", "Long An", "Bắc Ninh",
            "Thái Nguyên", "Hải Dương", "Nam Định", "Vĩnh Phúc", "Hà Tĩnh"
        };

        foreach (var name in names)
        {
            if (db.Cities.Any(c => c.Name == name)) continue;   // có rồi thì bỏ qua

            var city = new City { Name = name };
            db.Cities.Add(city);
            db.SaveChanges();

            var cinema = new Cinema
            {
                CityId = city.Id,
                Name = $"Star Cinema {name}",
                Address = $"1 Đường Trung Tâm, {name}",
                Hotline = "1900 0000"
            };
            db.Cinemas.Add(cinema);
            db.SaveChanges();

            var rooms = new List<Room>
            {
                new() { CinemaId = cinema.Id, Name = "Phòng 1", Type = "2D" },
                new() { CinemaId = cinema.Id, Name = "Phòng 2", Type = "4DX" },
            };
            db.Rooms.AddRange(rooms);
            db.SaveChanges();

            foreach (var r in rooms)
                db.Seats.AddRange(SeatSeeder.BuildSeats(r.Id));
            db.SaveChanges();
        }
    }
}