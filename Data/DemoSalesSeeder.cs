using CinemaApp.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Data;

// Tạo dữ liệu bán vé MẪU để báo cáo doanh thu có số liệu đầy đủ (chỉ chạy ở máy phát triển, và chỉ khi bạn bật).
// Cách bật (một lần, trong PowerShell rồi dotnet run):
//   $env:Seed__DemoSales = "generate"      tạo dữ liệu
//   $env:Seed__DemoSales = "clear"         xóa toàn bộ đơn và khách mẫu
//   $env:Seed__DemoSales = "regenerate"    xóa rồi tạo lại
// Dữ liệu mẫu nhận biết bằng: khách có email @demo.starcinema.vn, mã vé bắt đầu bằng DM.
public static class DemoSalesSeeder
{
    const string UserDomain = "demo.starcinema.vn";
    const int RngSeed = 20261026;   // cố định để chạy lại ra cùng một bộ số liệu

    // Rạp được mô phỏng bán vé; mỗi rạp chỉ dùng 2 phòng đầu (để tỷ lệ lấp đầy thực tế, không bị loãng)
    static readonly string[] FocusNames =
    {
        "Star Cinema Quận 1", "Star Cinema Quận 7", "Star Cinema Gò Vấp",
        "Star Cinema Hoàn Kiếm", "Star Cinema Cầu Giấy", "Star Cinema Hải Châu"
    };
    const int RoomsPerCinema = 2;
    static readonly (int h, int m)[] Slots = { (10, 0), (13, 30), (17, 0), (20, 30) };

    static readonly string[] Last = { "Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Huỳnh", "Phan", "Vũ", "Võ", "Đặng", "Bùi", "Đỗ", "Hồ", "Ngô", "Dương", "Lý" };
    static readonly string[] Mid = { "Văn", "Thị", "Minh", "Quốc", "Ngọc", "Thanh", "Hoài", "Gia", "Anh", "Bảo" };
    static readonly string[] First = { "An", "Bình", "Chi", "Dũng", "Giang", "Hà", "Hùng", "Khánh", "Lan", "Linh", "Mai", "Nam", "Phúc", "Quân", "Sơn", "Thảo", "Trang", "Tuấn", "Vy", "Yến" };

    static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local.AddHours(-7), DateTimeKind.Utc);

    record SeatInfo(int Id, int RoomId, string Row, int Number, string Type);
    record DemoUser(string Id, string Tier);

    class TargetShow
    {
        public int Id { get; set; }
        public int MovieId { get; set; }
        public int RoomId { get; set; }
        public string RoomType { get; set; } = "";
        public DateTime StartTime { get; set; }
        public decimal BasePrice { get; set; }
    }

    public static async Task RunAsync(AppDbContext db, IServiceProvider sp, string mode, IConfiguration cfg)
    {
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DemoSales");
        mode = (mode ?? "").Trim().ToLowerInvariant();
        if (mode != "generate" && mode != "clear" && mode != "regenerate")
        {
            log.LogWarning("Seed:DemoSales = '{Mode}' không hợp lệ (dùng generate, clear hoặc regenerate).", mode);
            return;
        }
        if (mode != "generate") await ClearAsync(db, log);
        if (mode != "clear") await GenerateAsync(db, sp, cfg, log);
    }

    // ---------------------------------------------------------------- xóa
    static async Task ClearAsync(AppDbContext db, ILogger log)
    {
        var ids = await db.Users.Where(u => u.Email != null && u.Email.EndsWith("@" + UserDomain)).Select(u => u.Id).ToListAsync();
        int orders = await db.Bookings.Where(b => ids.Contains(b.UserId)).ExecuteDeleteAsync();   // ghế và bắp nước xóa theo (cascade)
        int users = await db.Users.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        log.LogInformation("DemoSales: đã xóa {Orders} đơn và {Users} khách mẫu.", orders, users);
    }

    // ---------------------------------------------------------------- tạo
    static async Task GenerateAsync(AppDbContext db, IServiceProvider sp, IConfiguration cfg, ILogger log)
    {
        if (await db.Users.AnyAsync(u => u.Email != null && u.Email.EndsWith("@" + UserDomain)))
        {
            log.LogWarning("DemoSales: đã có dữ liệu mẫu. Dùng Seed__DemoSales=regenerate để làm lại.");
            return;
        }

        int days = Math.Clamp(cfg.GetValue("Seed:DemoSalesDays", 60), 7, 180);
        bool trim = cfg.GetValue("Seed:DemoSalesTrim", true);
        var rnd = new Random(RngSeed);
        var nowUtc = DateTime.UtcNow;
        var todayVn = nowUtc.AddHours(7).Date;
        var firstDay = todayVn.AddDays(-days);
        var fromUtc = ToUtc(firstDay);
        var epoch = new DateTime(2026, 1, 1);

        var focus = await db.Cinemas.AsNoTracking().Where(c => FocusNames.Contains(c.Name)).OrderBy(c => c.Id).ToListAsync();
        if (focus.Count == 0) { log.LogWarning("DemoSales: không thấy rạp nào trong danh sách mô phỏng."); return; }
        var focusIds = focus.Select(c => c.Id).ToList();

        var allRooms = await db.Rooms.AsNoTracking().Where(r => focusIds.Contains(r.CinemaId)).OrderBy(r => r.CinemaId).ThenBy(r => r.Id).ToListAsync();
        var roomsByCinema = allRooms.GroupBy(r => r.CinemaId).ToDictionary(g => g.Key, g => g.Take(RoomsPerCinema).ToList());
        var allowedRoomIds = roomsByCinema.Values.SelectMany(x => x).Select(r => r.Id).ToList();

        var movies = await db.Movies.AsNoTracking().ToListAsync();
        if (movies.Count == 0) { log.LogWarning("DemoSales: chưa có phim."); return; }
        var movieMap = movies.ToDictionary(m => m.Id);
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.SortOrder).ToListAsync();

        // ---- 1. Khách mẫu (không có mật khẩu nên không đăng nhập được)
        var um = sp.GetRequiredService<UserManager<AppUser>>();
        var admins = await um.GetUsersInRoleAsync(AdminSeeder.RoleName);
        string adminEmail = admins.FirstOrDefault()?.Email ?? "admin@starcinema.vn";

        var demoUsers = new List<DemoUser>();
        for (int i = 1; i <= 40; i++)
        {
            var tier = MembershipRules.Tiers[PickWeighted(rnd, new[] { 55, 25, 15, 5 })];
            var u = new AppUser
            {
                UserName = $"khach{i:00}@{UserDomain}",
                Email = $"khach{i:00}@{UserDomain}",
                EmailConfirmed = true,
                FullName = $"{Last[rnd.Next(Last.Length)]} {Mid[rnd.Next(Mid.Length)]} {First[rnd.Next(First.Length)]}",
                MemberTier = tier.Name,
                Points = tier.MinPoints + rnd.Next(0, 300),
                CreatedAt = fromUtc.AddDays(-rnd.Next(1, 200))
            };
            var r = await um.CreateAsync(u);
            if (r.Succeeded) demoUsers.Add(new DemoUser(u.Id, tier.Name));
        }
        db.ChangeTracker.Clear();
        if (demoUsers.Count == 0) { log.LogWarning("DemoSales: không tạo được khách mẫu."); return; }

        // ---- 2. Bổ sung suất chiếu trong quá khứ cho các rạp mô phỏng (chỉ những ngày rạp chưa có suất nào)
        var existing = await db.Showtimes.AsNoTracking()
            .Where(s => focusIds.Contains(s.Room.CinemaId) && s.StartTime >= fromUtc && s.StartTime < nowUtc)
            .Select(s => new { s.Room.CinemaId, s.StartTime }).ToListAsync();
        var have = existing.Select(x => (x.CinemaId, Day: x.StartTime.AddHours(7).Date)).ToHashSet();

        int createdShows = 0;
        for (var date = firstDay; date <= todayVn; date = date.AddDays(1))
        {
            var cand = movies.Where(m => m.ReleaseDate.Date <= date).OrderByDescending(m => m.ReleaseDate).Take(12).ToList();
            if (cand.Count < 4) cand = movies.Where(m => m.Status == "NowShowing").OrderBy(m => m.Id).Take(12).ToList();
            if (cand.Count == 0) cand = movies.Take(12).ToList();
            bool weekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
            var batch = new List<Showtime>();

            foreach (var c in focus)
            {
                if (have.Contains((c.Id, date))) continue;
                if (!roomsByCinema.TryGetValue(c.Id, out var rooms)) continue;
                int k = (int)(date - epoch).TotalDays * 3 + c.Id;

                foreach (var room in rooms)
                {
                    var prevEnd = date;
                    foreach (var (h, m) in Slots)
                    {
                        var start = date.AddHours(h).AddMinutes(m);
                        var minStart = prevEnd.AddMinutes(10);
                        if (start < minStart)
                        {
                            start = minStart;
                            int mod = start.Minute % 5;
                            if (mod != 0) start = start.AddMinutes(5 - mod);
                        }
                        if (start > date.AddHours(22).AddMinutes(30)) break;
                        if (ToUtc(start) >= nowUtc) break;   // chỉ tạo suất đã bắt đầu

                        var movie = cand[k % cand.Count];
                        int round = k / cand.Count;
                        k++;
                        int dur = movie.DurationMin > 0 ? movie.DurationMin : 110;
                        var end = start.AddMinutes(dur + 15);
                        prevEnd = end;

                        decimal price = room.Type switch { "4DX" => 153000, "GOLDCLASS" => 180000, "SCREENX" => 130000, _ => 90000 };
                        if (weekend) price += 20000;

                        batch.Add(new Showtime
                        {
                            MovieId = movie.Id,
                            RoomId = room.Id,
                            StartTime = ToUtc(start),
                            EndTime = ToUtc(end),
                            Format = room.Type switch { "4DX" => "4DX2D", "SCREENX" => "SCREENX", _ => "2D" },
                            LanguageMode = round % 2 == 0 ? "Phụ đề Anh & Việt" : "Lồng tiếng Việt",
                            BasePrice = price
                        });
                    }
                }
            }
            if (batch.Count > 0)
            {
                db.Showtimes.AddRange(batch);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                createdShows += batch.Count;
            }
        }

        // ---- 3. Dọn suất đã qua, chưa có đơn, ở phòng không mô phỏng (để tỷ lệ lấp đầy phản ánh đúng)
        int trimmed = 0;
        if (trim)
        {
            trimmed = await db.Showtimes
                .Where(s => s.StartTime < nowUtc && !allowedRoomIds.Contains(s.RoomId)
                            && !db.Bookings.Any(b => b.ShowtimeId == s.Id)
                            && !db.ShowtimeSeats.Any(x => x.ShowtimeId == s.Id))
                .ExecuteDeleteAsync();
            db.ChangeTracker.Clear();
        }

        // ---- 4. Ghế của các phòng mô phỏng
        var seatRows = await db.Seats.AsNoTracking().Where(s => allowedRoomIds.Contains(s.RoomId))
            .OrderBy(s => s.RoomId).ThenBy(s => s.PosY).ThenBy(s => s.SeatNumber)
            .Select(s => new SeatInfo(s.Id, s.RoomId, s.RowLabel, s.SeatNumber, s.SeatType)).ToListAsync();
        var seatsByRoom = seatRows.GroupBy(s => s.RoomId).ToDictionary(g => g.Key, g => g.ToList());

        // ---- 5. Suất chiếu đã diễn ra, chưa có ai đặt: bán vé mô phỏng
        var targets = await db.Showtimes.AsNoTracking()
            .Where(s => allowedRoomIds.Contains(s.RoomId) && s.StartTime >= fromUtc && s.StartTime < nowUtc
                        && !db.ShowtimeSeats.Any(x => x.ShowtimeId == s.Id)
                        && !db.Bookings.Any(b => b.ShowtimeId == s.Id))
            .OrderBy(s => s.StartTime)
            .Select(s => new TargetShow { Id = s.Id, MovieId = s.MovieId, RoomId = s.RoomId, RoomType = s.Room.Type, StartTime = s.StartTime, BasePrice = s.BasePrice })
            .ToListAsync();

        var usedCodes = new HashSet<string>();
        var usedRefs = new HashSet<string>();
        var pending = new List<Booking>();
        int paidCount = 0, refundCount = 0, expiredCount = 0, cancelledCount = 0, tickets = 0;
        decimal revenue = 0m;

        async Task Flush()
        {
            if (pending.Count == 0) return;
            db.Bookings.AddRange(pending);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            pending.Clear();
        }        foreach (var t in targets)
        {
            if (!movieMap.TryGetValue(t.MovieId, out var movie)) continue;
            if (!seatsByRoom.TryGetValue(t.RoomId, out var seats) || seats.Count == 0) continue;

            decimal effective = ShowtimePricing.Effective(t.BasePrice, t.StartTime);
            int target = (int)Math.Round(seats.Count * FillRatio(t, movie, rnd));
            var taken = new HashSet<int>();
            int sold = 0;
            bool childOk = movie.AgeRating == "P" || movie.AgeRating == "K";

            while (sold < target)
            {
                int n = Math.Min(PickSize(rnd), target - sold);
                var picked = PickSeats(seats, taken, n, rnd);
                if (picked.Count == 0) break;
                foreach (var p in picked) taken.Add(p.Id);
                sold += picked.Count;
                n = picked.Count;

                var user = demoUsers[rnd.Next(demoUsers.Count)];

                // Loại vé: vé trẻ em chỉ ở phim P/K, mỗi người lớn tối đa 2 trẻ em
                var types = new List<string>();
                int adults = 0, kids = 0;
                for (int i = 0; i < n; i++)
                {
                    bool kid = i > 0 && childOk && rnd.NextDouble() < 0.28 && kids < adults * 2;
                    types.Add(kid ? "Child" : "Adult");
                    if (kid) kids++; else adults++;
                }

                // Thời điểm: đa số mua trong vòng một ngày trước giờ chiếu
                double hoursBefore = Math.Min(72.0, -Math.Log(1 - rnd.NextDouble()) * 7.0) + 0.3;
                var paidAt = t.StartTime.AddHours(-hoursBefore);
                if (paidAt > nowUtc) paidAt = nowUtc.AddMinutes(-5);
                string method = rnd.NextDouble() < 0.62 ? "Transfer" : "Cash";
                var created = paidAt.AddMinutes(-rnd.Next(2, 9));
                var expires = created.AddMinutes(method == "Cash" ? 60 : 15);

                var b = new Booking
                {
                    UserId = user.Id,
                    ShowtimeId = t.Id,
                    CreatedAt = created,
                    ExpiresAt = expires,
                    PaymentMethod = method
                };

                decimal seatsTotal = 0m;
                bool refunded = rnd.NextDouble() < 0.025;
                for (int i = 0; i < n; i++)
                {
                    decimal price = TicketPrice(effective, picked[i].Type, types[i]);
                    seatsTotal += price;
                    if (!refunded)   // đơn đã hoàn tiền thì ghế đã được nhả (giống luồng hoàn tiền thật)
                        b.Seats.Add(new ShowtimeSeat
                        {
                            ShowtimeId = t.Id,
                            SeatId = picked[i].Id,
                            Status = "Sold",
                            TicketType = types[i],
                            UserId = user.Id,
                            HoldExpiresAt = expires,
                            Price = price
                        });
                }

                // Bắp nước
                decimal itemsTotal = 0m;
                if (products.Count > 0 && rnd.NextDouble() < 0.58)
                {
                    int idx = n == 1 ? 0 : (n <= 3 ? 1 : 2);
                    var main = products[Math.Min(idx, products.Count - 1)];
                    var lines = new Dictionary<int, (Product p, int q)> { [main.Id] = (main, 1) };
                    if (products.Count > 3 && rnd.NextDouble() < 0.28)
                    {
                        var extra = products[3 + rnd.Next(products.Count - 3)];
                        lines[extra.Id] = lines.TryGetValue(extra.Id, out var cur) ? (extra, cur.q + 1) : (extra, 1 + rnd.Next(2));
                    }
                    foreach (var (p, q) in lines.Values)
                    {
                        b.Items.Add(new BookingItem { ProductId = p.Id, Quantity = q, UnitPrice = p.Price });
                        itemsTotal += p.Price * q;
                    }
                }

                // Ưu đãi hạng thành viên: giảm theo % trên tiền bắp nước
                var tierInfo = MembershipRules.Tiers.First(x => x.Name == user.Tier);
                decimal memberDisc = tierInfo.ItemsPercent > 0 ? Math.Round(itemsTotal * tierInfo.ItemsPercent / 100m, 0) : 0m;
                b.MemberDiscount = memberDisc;
                b.MemberTierName = memberDisc > 0 ? tierInfo.Name : null;
                b.Total = Math.Max(0m, seatsTotal + itemsTotal - memberDisc);

                b.Code = NewCode(rnd, usedCodes);
                b.PaidAt = paidAt;
                b.ApprovedAt = paidAt;
                b.ApprovedByEmail = adminEmail;
                b.ReceivedAmount = b.Total;
                b.TicketEmailedAt = paidAt.AddMinutes(1);   // đã "gửi" rồi nên dịch vụ email không gửi tới địa chỉ mẫu
                if (method == "Transfer") b.BankRef = NewRef(rnd, usedRefs);

                if (refunded)
                {
                    var refundAt = paidAt.AddHours(1 + rnd.Next(20));
                    if (refundAt > t.StartTime.AddHours(-1)) refundAt = paidAt.AddMinutes(30);
                    b.Status = "Refunded";
                    b.RefundedAt = refundAt;
                    b.RefundedByEmail = adminEmail;
                    b.RefundReason = "Khách đổi lịch (dữ liệu mẫu)";
                    refundCount++;
                }
                else
                {
                    b.Status = "Paid";
                    if (rnd.NextDouble() < 0.92)
                    {
                        b.CheckedInAt = t.StartTime.AddMinutes(-rnd.Next(3, 26));
                        b.CheckedInByEmail = adminEmail;
                    }
                    paidCount++;
                    tickets += n;
                    revenue += b.Total;
                }
                pending.Add(b);

                // Đơn bị mất: hết hạn chưa thanh toán, hoặc khách tự hủy (không giữ ghế)
                double lost = rnd.NextDouble();
                if (lost < 0.19)
                {
                    bool expiredOne = lost < 0.12;
                    var c2 = t.StartTime.AddHours(-(0.5 + rnd.NextDouble() * 20));
                    if (c2 > nowUtc) c2 = nowUtc.AddMinutes(-30);
                    string m2 = rnd.NextDouble() < 0.5 ? "Transfer" : "Cash";
                    pending.Add(new Booking
                    {
                        UserId = demoUsers[rnd.Next(demoUsers.Count)].Id,
                        ShowtimeId = t.Id,
                        Status = expiredOne ? "Expired" : "Cancelled",
                        CreatedAt = c2,
                        ExpiresAt = c2.AddMinutes(m2 == "Cash" ? 60 : 15),
                        PaymentMethod = m2,
                        Total = Math.Round(effective * (1 + rnd.Next(1, 4)) / 1000m) * 1000m
                    });
                    if (expiredOne) expiredCount++; else cancelledCount++;
                }

                if (pending.Count >= 400) await Flush();
            }
        }
        await Flush();

        log.LogInformation("DemoSales XONG: {Days} ngày; thêm {Shows} suất chiếu quá khứ; dọn {Trim} suất trống; {Paid} đơn đã thanh toán ({Tickets} vé, {Rev:N0} đ), {Refund} hoàn tiền, {Exp} hết hạn, {Can} hủy.",
            days, createdShows, trimmed, paidCount, tickets, revenue, refundCount, expiredCount, cancelledCount);
    }

    // ---------------------------------------------------------------- hàm phụ
    static int PickWeighted(Random rnd, int[] weights)
    {
        int roll = rnd.Next(weights.Sum()), acc = 0;
        for (int i = 0; i < weights.Length; i++) { acc += weights[i]; if (roll < acc) return i; }
        return weights.Length - 1;
    }

    // Số ghế mỗi đơn: từ 1 đến 6, phổ biến nhất là 2 ghế (cặp đôi, bạn bè)
    static int PickSize(Random rnd) => 1 + PickWeighted(rnd, new[] { 22, 44, 12, 14, 3, 5 });

    // Tỷ lệ lấp đầy mô phỏng: cuối tuần, giờ vàng tối, thứ Tư giảm giá, phim mới và phim hot bán tốt hơn
    static double FillRatio(TargetShow t, Movie m, Random rnd)
    {
        var local = t.StartTime.AddHours(7);
        double r = 0.16;
        if (local.DayOfWeek == DayOfWeek.Saturday || local.DayOfWeek == DayOfWeek.Sunday) r += 0.14;
        if (local.DayOfWeek == DayOfWeek.Wednesday) r += 0.08;
        int h = local.Hour;
        if (h >= 18 && h <= 21) r += 0.18; else if (h >= 15) r += 0.08; else if (h < 12) r -= 0.05;
        int age = Math.Max(0, (int)(local.Date - m.ReleaseDate.Date).TotalDays);
        if (age < 7) r += 0.20; else if (age < 21) r += 0.10; else if (age > 45) r -= 0.06;
        r *= t.RoomType switch { "GOLDCLASS" => 0.80, "4DX" => 1.05, "SCREENX" => 0.95, _ => 1.0 };
        r *= 0.8 + ((m.Id * 37) % 7) * 0.07;   // mỗi phim có độ hot khác nhau
        r += (rnd.NextDouble() - 0.5) * 0.16;
        return Math.Clamp(r, 0.03, 0.92);
    }

    // Giá vé giống TicketPricing: giá suất đã ưu đãi, trẻ em giảm 30% (làm tròn nghìn), ghế VIP phụ thu 15.000
    static decimal TicketPrice(decimal effective, string seatType, string ticketType)
    {
        int disc = ticketType == "Child" ? 30 : 0;
        var ticket = Math.Round(effective * (100 - disc) / 100000m, 0, MidpointRounding.AwayFromZero) * 1000m;
        return ticket + (seatType == "VIP" ? 15000m : 0m);
    }

    // Ưu tiên chọn ghế liền nhau trong một hàng
    static List<SeatInfo> PickSeats(List<SeatInfo> seats, HashSet<int> taken, int n, Random rnd)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            int i = rnd.Next(seats.Count);
            var row = seats[i].Row;
            var list = new List<SeatInfo>();
            for (int j = i; j < seats.Count && list.Count < n; j++)
            {
                if (seats[j].Row != row || taken.Contains(seats[j].Id)) break;
                if (list.Count > 0 && seats[j].Number != list[^1].Number + 1) break;
                list.Add(seats[j]);
            }
            if (list.Count == n) return list;
        }
        var free = seats.Where(s => !taken.Contains(s.Id)).ToList();
        var res = new List<SeatInfo>();
        while (res.Count < n && free.Count > 0)
        {
            var s = free[rnd.Next(free.Count)];
            free.Remove(s);
            res.Add(s);
        }
        return res;
    }

    static string NewCode(Random rnd, HashSet<string> used)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        while (true)
        {
            var c = "DM" + new string(Enumerable.Range(0, 8).Select(_ => chars[rnd.Next(chars.Length)]).ToArray());
            if (used.Add(c)) return c;
        }
    }

    static string NewRef(Random rnd, HashSet<string> used)
    {
        while (true)
        {
            var r = "DMFT" + rnd.NextInt64(1_000_000_000L, 9_999_999_999L);
            if (used.Add(r)) return r;
        }
    }
}