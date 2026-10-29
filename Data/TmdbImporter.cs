using System.Text.Json;
using CinemaApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Data;

public static class TmdbImporter
{
    const string Base = "https://api.themoviedb.org/3";
    const string ImgPoster = "https://image.tmdb.org/t/p/w500";
    const string ImgBackdrop = "https://image.tmdb.org/t/p/w1280";

    // Chỉ nhập phim mẫu khi hệ thống còn trống (chưa có phim, chưa có suất chiếu).
    // Mọi lỗi mạng/TMDB chỉ ghi nhật ký, KHÔNG làm web sập lúc khởi động.
    public static async Task ImportAsync(AppDbContext db, string apiKey)
    {
        if (await db.Showtimes.AnyAsync()) return;
        if (await db.Movies.AnyAsync()) return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var seen = new HashSet<string>();
            await ImportList(db, http, apiKey, "now_playing", "NowShowing", seen);
            await ImportList(db, http, apiKey, "upcoming", "ComingSoon", seen);
        }
        catch (Exception ex)
        {
            // Chỉ in loại lỗi, không in nội dung có thể chứa khóa API
            Console.WriteLine("[TMDB] Không nhập được phim mẫu (" + ex.GetType().Name + "). Web vẫn chạy bình thường, admin có thể thêm phim thủ công.");
            db.ChangeTracker.Clear();
        }
    }

    static async Task ImportList(AppDbContext db, HttpClient http, string key,
                                 string kind, string status, HashSet<string> seen)
    {
        var listJson = await http.GetStringAsync(
            $"{Base}/movie/{kind}?api_key={key}&language=vi-VN&region=VN&page=1");
        using var list = JsonDocument.Parse(listJson);

        int taken = 0;
        foreach (var item in list.RootElement.GetProperty("results").EnumerateArray())
        {
            if (taken >= 12) break;
            int id = item.GetProperty("id").GetInt32();

            // Một phim lỗi thì bỏ qua phim đó, không hủy cả danh sách
            try
            {
                var detailJson = await http.GetStringAsync(
                    $"{Base}/movie/{id}?api_key={key}&language=vi-VN&append_to_response=videos,credits,release_dates&include_video_language=vi,en");
                using var d = JsonDocument.Parse(detailJson);
                var r = d.RootElement;

                string title = r.GetProperty("title").GetString() ?? "";
                if (title == "" || !seen.Add(title)) continue;

                string? poster = r.GetProperty("poster_path").GetString();
                if (poster == null) continue;

                string? backdrop = r.TryGetProperty("backdrop_path", out var bp) && bp.ValueKind == JsonValueKind.String
                                   ? bp.GetString() : null;

                string trailer = "";
                foreach (var v in r.GetProperty("videos").GetProperty("results").EnumerateArray())
                {
                    if (v.GetProperty("site").GetString() == "YouTube" &&
                        v.GetProperty("type").GetString() == "Trailer")
                    {
                        trailer = "https://www.youtube.com/embed/" + v.GetProperty("key").GetString();
                        break;
                    }
                }

                // Đạo diễn và diễn viên
                var directors = new List<string>();
                var cast = new List<string>();
                if (r.TryGetProperty("credits", out var credits))
                {
                    foreach (var c in credits.GetProperty("crew").EnumerateArray())
                        if (c.GetProperty("job").GetString() == "Director")
                            directors.Add(c.GetProperty("name").GetString() ?? "");
                    foreach (var c in credits.GetProperty("cast").EnumerateArray().Take(6))
                        cast.Add(c.GetProperty("name").GetString() ?? "");
                }

                // Quốc gia
                string country = "Đang cập nhật";
                if (r.TryGetProperty("production_countries", out var pcs) && pcs.GetArrayLength() > 0)
                    country = pcs[0].GetProperty("name").GetString() ?? country;

                // Thể loại
                var genres = new List<Genre>();
                foreach (var g in r.GetProperty("genres").EnumerateArray())
                {
                    var name = g.GetProperty("name").GetString() ?? "";
                    if (name == "") continue;
                    var genre = db.Genres.Local.FirstOrDefault(x => x.Name == name)
                                ?? await db.Genres.FirstOrDefaultAsync(x => x.Name == name);
                    if (genre == null) { genre = new Genre { Name = name }; db.Genres.Add(genre); }
                    genres.Add(genre);
                }
                if (genres.Count == 0)
                {
                    var other = db.Genres.Local.FirstOrDefault(x => x.Name == "Khác")
                                ?? await db.Genres.FirstOrDefaultAsync(x => x.Name == "Khác");
                    if (other == null) { other = new Genre { Name = "Khác" }; db.Genres.Add(other); }
                    genres.Add(other);
                }

                DateTime rd = default;
                if (r.TryGetProperty("release_date", out var rdp) && rdp.ValueKind == JsonValueKind.String)
                    DateTime.TryParse(rdp.GetString(), out rd);
                if (rd == default) rd = DateTime.UtcNow.Date;

                db.Movies.Add(new Movie
                {
                    Title = title,
                    Synopsis = r.GetProperty("overview").GetString() ?? "",
                    DurationMin = r.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Number
                                  ? rt.GetInt32() : 0,
                    AgeRating = RatingOf(r),
                    ReleaseDate = DateTime.SpecifyKind(rd, DateTimeKind.Utc),
                    PosterUrl = ImgPoster + poster,
                    BackdropUrl = backdrop != null ? ImgBackdrop + backdrop : "-",
                    TrailerUrl = trailer,
                    Status = status,
                    Director = directors.Count > 0 ? string.Join(", ", directors) : "Đang cập nhật",
                    Cast = cast.Count > 0 ? string.Join(", ", cast) : "Đang cập nhật",
                    Country = country,
                    Genres = genres
                });
                taken++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine("[TMDB] Bỏ qua 1 phim lỗi dữ liệu (" + ex.GetType().Name + ").");
            }
        }
        await db.SaveChangesAsync();
    }

    // Lấy nhãn tuổi từ xếp hạng của TMDB (ưu tiên Việt Nam, sau đó Mỹ) rồi đổi sang nhãn của rạp.
    // Không có thông tin thì dùng T13 (mức thận trọng); admin nên kiểm tra lại ở trang Quản lý phim.
    static string RatingOf(JsonElement r)
    {
        try
        {
            if (!r.TryGetProperty("release_dates", out var rds)) return "T13";
            foreach (var country in new[] { "VN", "US" })
            {
                foreach (var c in rds.GetProperty("results").EnumerateArray())
                {
                    if (c.GetProperty("iso_3166_1").GetString() != country) continue;
                    foreach (var x in c.GetProperty("release_dates").EnumerateArray())
                    {
                        var cert = (x.GetProperty("certification").GetString() ?? "").Trim().ToUpperInvariant();
                        switch (cert)
                        {
                            case "P": case "G": return "P";
                            case "K": case "PG": return "K";
                            case "T13": case "PG-13": return "T13";
                            case "T16": case "R": return "T16";
                            case "T18": case "C18": case "NC-17": return "T18";
                        }
                    }
                }
            }
        }
        catch { }
        return "T13";
    }
}