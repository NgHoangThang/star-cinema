namespace CinemaApp.Models;

public class ScheduleViewModel
{
    public string City { get; set; } = "";
    public DateTime SelectedDate { get; set; }
    public List<DateTime> Days { get; set; } = new();
    public List<string> Formats { get; set; } = new();
    public string? SelectedFormat { get; set; }
    public List<CinemaSchedule> Cinemas { get; set; } = new();
}

public class CinemaSchedule
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public List<RoomGroup> Groups { get; set; } = new();
}

public class RoomGroup
{
    public string RoomType { get; set; } = "";
    public List<ShowtimeSlot> Slots { get; set; } = new();
}

public class ShowtimeSlot
{
    public int Id { get; set; }
    public string Time { get; set; } = "";
    public bool Past { get; set; }
    public decimal Price { get; set; }
    public string? Promo { get; set; }   // nhãn ưu đãi, null nếu không có
}