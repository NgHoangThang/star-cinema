namespace CinemaApp.Models;

public class HomeViewModel
{
    public List<Banner> Banners { get; set; } = new();
    public List<Movie> NowShowing { get; set; } = new();
    public List<Movie> ComingSoon { get; set; } = new();
        public List<Movie> Hero { get; set; } = new();
}