namespace CinemaApp.Models;

public class CinemaDetailsViewModel
{
    public Cinema Cinema { get; set; } = null!;
    public DateTime SelectedDate { get; set; }
    public List<DateTime> Days { get; set; } = new();
    public List<MovieShows> Movies { get; set; } = new();
}

public class MovieShows
{
    public Movie Movie { get; set; } = null!;
    public List<FormatShows> Formats { get; set; } = new();
}

public class FormatShows
{
    public string Label { get; set; } = "";
    public List<ShowtimeSlot> Slots { get; set; } = new();
}