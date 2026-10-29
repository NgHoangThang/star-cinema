namespace CinemaApp.Models;

public class MoviesViewModel
{
    public List<Movie> Movies { get; set; } = new();
    public List<Genre> Genres { get; set; } = new();
    public string? Q { get; set; }
    public string? Status { get; set; }
    public int? GenreId { get; set; }
    public int Page { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public int Total { get; set; }
}