namespace CinemaApp.Models;

// Nhãn phân loại phim theo quy định Việt Nam: P, K, T13, T16, T18, C
public static class AgeRatings
{
    public static string Code(string? rating)
    {
        var r = (rating ?? "").Trim().ToUpperInvariant();
        return r is "P" or "K" or "T13" or "T16" or "T18" or "C" ? r : "P";
    }

    public static string Describe(string? rating) => Code(rating) switch
    {
        "P" => "P – Phim cho mọi đối tượng",
        "K" => "K – Phim dưới 13 tuổi chỉ xem khi có người giám hộ",
        "T13" => "T13 – Phim cấm khán giả dưới 13 tuổi",
        "T16" => "T16 – Phim cấm khán giả dưới 16 tuổi",
        "T18" => "T18 – Phim cấm khán giả dưới 18 tuổi",
        "C" => "C – Phim không được phép phổ biến",
        _ => "P – Phim cho mọi đối tượng"
    };
}