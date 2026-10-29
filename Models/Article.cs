namespace CinemaApp.Models;

public class Article
{
    public int Id { get; set; }

    // Đường dẫn thân thiện, không dấu, không trùng nhau: /News/giam-gia-thu-tu
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";

    // Mô tả ngắn hiện ở danh sách (tối đa 300 ký tự)
    public string Summary { get; set; } = "";

    // Nội dung dạng chữ thường: dòng trống = đoạn mới. Khi hiển thị luôn được mã hóa nên không chèn được HTML.
    public string Body { get; set; } = "";

    public string CoverUrl { get; set; } = "";

    // Nhóm bài: "Khuyến mãi", "Tin phim", "Sự kiện"
    public string Category { get; set; } = "Tin phim";

    // Chỉ bài đã đăng mới hiện cho khách
    public bool IsPublished { get; set; }
    public DateTime? PublishedAt { get; set; }

    public string AuthorEmail { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}