using System.ComponentModel.DataAnnotations.Schema;

namespace CinemaApp.Models;

public class City
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Cinema> Cinemas { get; set; } = new();
}

public class Cinema
{
    public int Id { get; set; }
    public int CityId { get; set; }
    public City City { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Hotline { get; set; } = "";
    public List<Room> Rooms { get; set; } = new();
}

public class Room
{
    public int Id { get; set; }
    public int CinemaId { get; set; }
    public Cinema Cinema { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Type { get; set; } = "2D"; // 2D, 4DX, GOLDCLASS, SCREENX
    public List<Seat> Seats { get; set; } = new();
}

public class Seat
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public string RowLabel { get; set; } = "";
    public int SeatNumber { get; set; }
    public string SeatType { get; set; } = "Standard"; // Standard, VIP, Couple
    public int PosX { get; set; }
    public int PosY { get; set; }
}

public class Genre
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Movie> Movies { get; set; } = new();
}

public class Movie
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public int DurationMin { get; set; }
    public string AgeRating { get; set; } = "P"; // P, K, T13, T16, T18
    public DateTime ReleaseDate { get; set; }
    public string PosterUrl { get; set; } = "";
    public string BackdropUrl { get; set; } = "";
    public string TrailerUrl { get; set; } = "";
    public string Status { get; set; } = "NowShowing"; // NowShowing, ComingSoon
    public string Director { get; set; } = "Đang cập nhật";
    public string Cast { get; set; } = "Đang cập nhật";
    public string Country { get; set; } = "Đang cập nhật";
    public List<Genre> Genres { get; set; } = new();
}

public class Showtime
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    public Movie Movie { get; set; } = null!;
    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Format { get; set; } = "2D";
    public string LanguageMode { get; set; } = "Phụ đề Anh & Việt";
    public decimal BasePrice { get; set; }

    // Tính sẵn từ giờ chiếu, không lưu trong database
    [NotMapped]
    public decimal EffectivePrice => ShowtimePricing.Effective(BasePrice, StartTime);

    [NotMapped]
    public string? PromoLabel => ShowtimePricing.Label(StartTime);
}

public class Banner
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string LinkUrl { get; set; } = "#";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}