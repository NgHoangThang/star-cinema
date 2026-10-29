namespace CinemaApp.Helpers;

public static class CountryNames
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["United States of America"] = "Mỹ", ["United Kingdom"] = "Anh", ["Japan"] = "Nhật Bản",
        ["South Korea"] = "Hàn Quốc", ["China"] = "Trung Quốc", ["Hong Kong"] = "Hồng Kông",
        ["Taiwan"] = "Đài Loan", ["Thailand"] = "Thái Lan", ["Vietnam"] = "Việt Nam",
        ["France"] = "Pháp", ["Germany"] = "Đức", ["Italy"] = "Ý", ["Spain"] = "Tây Ban Nha",
        ["Russia"] = "Nga", ["India"] = "Ấn Độ", ["Canada"] = "Canada", ["Australia"] = "Úc",
        ["New Zealand"] = "New Zealand", ["Ireland"] = "Ireland", ["Sweden"] = "Thụy Điển",
        ["Denmark"] = "Đan Mạch", ["Norway"] = "Na Uy", ["Belgium"] = "Bỉ", ["Netherlands"] = "Hà Lan",
        ["Poland"] = "Ba Lan", ["Philippines"] = "Philippines", ["Indonesia"] = "Indonesia",
        ["Malaysia"] = "Malaysia", ["Singapore"] = "Singapore", ["Turkey"] = "Thổ Nhĩ Kỳ",
        ["Mexico"] = "Mexico", ["Brazil"] = "Brasil", ["Argentina"] = "Argentina"
    };

    public static string Vi(string? name)
        => !string.IsNullOrEmpty(name) && Map.TryGetValue(name, out var vi) ? vi : (name ?? "");
}