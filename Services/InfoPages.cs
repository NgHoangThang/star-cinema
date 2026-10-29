namespace CinemaApp.Services;

// Thông tin liên hệ của rạp. Bạn điền vào đây, chỗ nào để trống thì trang Liên hệ hiện "Đang cập nhật".
public static class SiteInfo
{
    public static string Hotline = "1900 0000";
    public static string SupportEmail = "";
    public static string Address = "Quận Gò Vấp, TP. Hồ Chí Minh";
    public static string OpenHours = "08:00 – 22:00, mỗi ngày";
}

public record InfoSection(string Heading, string[] Paragraphs, string[]? Bullets = null);
public record InfoPage(string Slug, string Eyebrow, string Title, string Intro, InfoSection[] Sections);

// Nội dung các trang thông tin và chính sách. Muốn sửa chữ trên trang nào thì sửa ở đây.
public static class InfoPages
{
    const string Note = "";

    static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "Đang cập nhật" : s;

    public static InfoPage? Find(string? slug)
    {
        switch ((slug ?? "").Trim().ToLowerInvariant())
        {
            case "faq": return Faq();
            case "refund": return Refund();
            case "terms": return Terms();
            case "privacy": return Privacy();
            case "ratings": return Ratings();
            case "about": return About();
            case "contact": return Contact();
            default: return null;
        }
    }

    static InfoPage About() => new("about", "GIỚI THIỆU", "Về Star Cinema",
        "Star Cinema là hệ thống đặt vé xem phim trực tuyến: chọn phim, chọn suất, chọn ghế và thanh toán ngay trên web.",
        new[]
        {
            new InfoSection("Chúng tôi làm gì", new[]
            {
                "Bạn xem lịch chiếu theo thành phố, chọn ghế trên sơ đồ phòng chiếu và nhận vé có mã QR để vào rạp.",
                Note
            }),
            new InfoSection("Điểm nổi bật", Array.Empty<string>(), new[]
            {
                "Giữ ghế 10 phút khi bạn đang chọn, không sợ bị người khác lấy mất.",
                "Thanh toán bằng chuyển khoản QR hoặc tiền mặt tại quầy.",
                "Vé gửi về email ngay khi thanh toán xong.",
                "Chương trình thành viên: tích điểm theo mỗi đơn, lên hạng để nhận ưu đãi."
            })
        });

    static InfoPage Contact() => new("contact", "LIÊN HỆ", "Liên hệ Star Cinema",
        "Cần hỗ trợ về đơn đặt vé, thanh toán hoặc hoàn tiền? Hãy liên hệ kèm mã đặt vé (dạng SC và 8 ký tự) để được xử lý nhanh.",
        new[]
        {
            new InfoSection("Thông tin liên hệ", Array.Empty<string>(), new[]
            {
                "Hotline: " + Or(SiteInfo.Hotline),
                "Email hỗ trợ: " + Or(SiteInfo.SupportEmail),
                "Địa chỉ: " + Or(SiteInfo.Address),
                "Giờ làm việc: " + Or(SiteInfo.OpenHours)
            }),
            new InfoSection("Khi liên hệ, bạn nên cung cấp", Array.Empty<string>(), new[]
            {
                "Mã đặt vé (có trong email vé và trang Vé của tôi).",
                "Email tài khoản đã dùng để đặt vé.",
                "Nếu đã chuyển khoản: ảnh chụp giao dịch có số tiền và nội dung chuyển khoản."
            })
        });

    static InfoPage Refund() => new("refund", "CHÍNH SÁCH", "Chính sách đặt vé và hoàn tiền",
        "Đọc kỹ trước khi thanh toán: các quy định về giữ ghế, thời hạn thanh toán, đổi và hoàn vé.",
        new[]
        {
            new InfoSection("Giữ ghế", new[]
            {
                "Khi bạn chọn ghế, hệ thống giữ ghế cho bạn trong 10 phút. Hết thời gian mà chưa chọn cách thanh toán, ghế tự được nhả cho người khác.",
                "Mỗi tài khoản chỉ có một đơn đang chọn ghế và một đơn đang chờ thanh toán tại một thời điểm."
            }),
            new InfoSection("Thanh toán", Array.Empty<string>(), new[]
            {
                "Chuyển khoản QR: ghế được giữ 15 phút sau khi bạn chọn cách này. Bạn chuyển đúng số tiền và đúng nội dung (là mã đặt vé). Rạp xác nhận bằng tay sau khi nhận được tiền, trang sẽ tự cập nhật khi đơn được xác nhận.",
                "Tiền mặt tại quầy: vé được giữ tối đa 60 phút, tối đa 6 ghế. Bạn đến quầy đọc mã đặt vé và thanh toán trước hạn ghi trên đơn.",
                "Quá hạn mà chưa thanh toán, đơn bị hủy và ghế được nhả."
            }),
            new InfoSection("Đổi và hoàn vé", new[]
            {
                "Vé đã thanh toán không đổi, không hoàn. Vé trẻ em chỉ dành cho trẻ dưới 13 tuổi đi cùng người lớn, nhân viên có thể kiểm tra khi vào rạp."
            }),
            new InfoSection("Các trường hợp rạp xem xét hoàn tiền", new[]
            {
                "Rạp chỉ hoàn tiền khi suất chiếu chưa bắt đầu và vé chưa được quét ở cổng, trong các trường hợp sau:"
            }, new[]
            {
                "Suất chiếu bị hủy hoặc thay đổi do rạp.",
                "Lỗi hệ thống: bạn đã chuyển khoản đúng nhưng ghế không còn để giữ cho bạn.",
                "Chuyển khoản nhầm hoặc chuyển trùng.",
                "Các trường hợp bất khả kháng khác do rạp quyết định."
            }),
            new InfoSection("Đã chuyển khoản nhưng đơn báo hết hạn", new[]
            {
                "Hãy liên hệ rạp ngay kèm mã đặt vé và ảnh giao dịch. Nếu đơn hết hạn chưa quá 2 giờ, suất chưa chiếu và các ghế vẫn còn trống, rạp sẽ khôi phục vé cho bạn. Nếu ghế đã có người khác đặt, rạp sẽ hoàn tiền cho bạn.",
                "Khi hoàn tiền, ghế được trả lại hệ thống, vé không còn hiệu lực và rạp chuyển trả tiền cho bạn. Lượt dùng mã giảm giá của đơn đó cũng được trả lại."
            }),
            new InfoSection("Ghi chú", new[] { Note })
        });

    static InfoPage Terms() => new("terms", "ĐIỀU KHOẢN", "Điều khoản sử dụng",
        "Khi tạo tài khoản và đặt vé trên Star Cinema, bạn đồng ý với các điều khoản dưới đây.",
        new[]
        {
            new InfoSection("Tài khoản", Array.Empty<string>(), new[]
            {
                "Bạn cung cấp thông tin đúng sự thật và tự bảo mật mật khẩu của mình.",
                "Mỗi người dùng một tài khoản. Việc đặt vé từ tài khoản của bạn được xem là do bạn thực hiện.",
                "Chúng tôi có thể khóa tài khoản có dấu hiệu gian lận, lạm dụng mã giảm giá hoặc đặt giữ ghế hàng loạt."
            }),
            new InfoSection("Giá vé và ưu đãi", Array.Empty<string>(), new[]
            {
                "Giá vé được tính theo suất chiếu và loại vé, hiển thị rõ trước khi thanh toán.",
                "Suất chiếu trước 12 giờ trưa giảm 20%. Thứ Tư giảm 25%. Hai ưu đãi này không cộng dồn, hệ thống áp dụng mức cao hơn.",
                "Ưu đãi hạng thành viên giảm tiền bắp nước. Mã giảm giá được tính sau ưu đãi hạng, trên phần còn lại của đơn.",
                "Mỗi mã giảm giá có điều kiện riêng (đơn tối thiểu, hạn dùng, số lượt dùng). Mã riêng của một tài khoản không dùng được cho tài khoản khác."
            }),
            new InfoSection("Đặt vé và thanh toán", new[]
            {
                "Đơn chỉ có hiệu lực khi đã được xác nhận thanh toán. Việc thanh toán, đổi và hoàn vé thực hiện theo Chính sách đặt vé và hoàn tiền."
            }),
            new InfoSection("Vào rạp", Array.Empty<string>(), new[]
            {
                "Đưa mã QR hoặc mã đặt vé cho nhân viên. Mỗi vé chỉ được quét vào rạp một lần.",
                "Cổng chỉ mở trước giờ chiếu một khoảng thời gian nhất định. Suất đã kết thúc thì vé không còn dùng được.",
                "Phim được phân loại theo độ tuổi, khách phải tuân thủ quy định về nhãn phim."
            }),
            new InfoSection("Trách nhiệm", new[]
            {
                "Chúng tôi cố gắng để hệ thống hoạt động liên tục nhưng không bảo đảm không bao giờ gián đoạn. Khi xảy ra sự cố ảnh hưởng đến đơn của bạn, vui lòng liên hệ để được hỗ trợ.",
                Note
            })
        });

    static InfoPage Privacy() => new("privacy", "BẢO MẬT", "Chính sách bảo mật",
        "Chúng tôi thu thập và sử dụng thông tin của bạn như thế nào.",
        new[]
        {
            new InfoSection("Thông tin chúng tôi lưu", Array.Empty<string>(), new[]
            {
                "Thông tin tài khoản: họ tên, email, số điện thoại (khi bạn xác minh), ngày sinh (khi bạn cập nhật).",
                "Lịch sử đặt vé, thanh toán, mã giảm giá đã dùng và điểm thành viên.",
                "Thành phố bạn chọn, lưu trong trình duyệt để hiển thị đúng lịch chiếu."
            }),
            new InfoSection("Mục đích sử dụng", Array.Empty<string>(), new[]
            {
                "Xử lý đơn đặt vé, thanh toán và gửi vé qua email.",
                "Xác minh tài khoản, khôi phục mật khẩu.",
                "Tính điểm, xếp hạng thành viên và tặng quà sinh nhật.",
                "Phòng chống gian lận và hỗ trợ khi bạn gặp sự cố."
            }),
            new InfoSection("Chia sẻ thông tin", new[]
            {
                "Chúng tôi không bán thông tin của bạn. Thông tin chỉ được dùng nội bộ để vận hành dịch vụ, hoặc cung cấp cho cơ quan có thẩm quyền khi có yêu cầu hợp pháp."
            }),
            new InfoSection("Bảo mật", new[]
            {
                "Mật khẩu được mã hóa, không ai đọc được mật khẩu gốc của bạn. Các trang quản trị chỉ dành cho nhân viên được cấp quyền."
            }),
            new InfoSection("Quyền của bạn", new[]
            {
                "Bạn có thể xem và cập nhật thông tin trong Hồ sơ cá nhân, hoặc liên hệ để yêu cầu chỉnh sửa hoặc xóa tài khoản.",
                Note
            })
        });

    static InfoPage Ratings() => new("ratings", "QUY ĐỊNH", "Phân loại phim theo độ tuổi",
        "Mỗi phim có một nhãn độ tuổi theo quy định của Việt Nam, hiển thị trên thẻ phim, trang phim, vé và trang kiểm tra vé.",
        new[]
        {
            new InfoSection("Các nhãn phim", Array.Empty<string>(), new[]
            {
                "P: phim cho mọi đối tượng.",
                "K: phim dành cho khán giả dưới 13 tuổi và có người giám hộ đi cùng.",
                "T13: phim cấm khán giả dưới 13 tuổi.",
                "T16: phim cấm khán giả dưới 16 tuổi.",
                "T18: phim cấm khán giả dưới 18 tuổi.",
                "C: phim không được phép phổ biến."
            }),
            new InfoSection("Lưu ý khi đặt vé", new[]
            {
                "Nhân viên có thể kiểm tra giấy tờ khi vào rạp. Khách không đủ độ tuổi theo nhãn phim sẽ không được vào xem.",
                "Vé trẻ em chỉ dành cho trẻ dưới 13 tuổi đi cùng người lớn."
            })
        });

    static InfoPage Faq() => new("faq", "HỖ TRỢ", "Câu hỏi thường gặp",
        "Những câu hỏi khách hàng hay hỏi nhất khi đặt vé.",
        new[]
        {
            new InfoSection("Tôi giữ ghế được bao lâu?", new[]
            {
                "Khi chọn ghế, bạn có 10 phút để chọn cách thanh toán. Sau khi chọn Chuyển khoản, ghế được giữ thêm 15 phút. Chọn Tiền mặt, vé được giữ tối đa 60 phút."
            }),
            new InfoSection("Tôi đã chuyển khoản, bao lâu thì có vé?", new[]
            {
                "Rạp xác nhận bằng tay sau khi nhận được tiền, trang chờ sẽ tự chuyển sang vé khi xong, và vé cũng được gửi về email của bạn. Hãy chuyển đúng số tiền và đúng nội dung (mã đặt vé)."
            }),
            new InfoSection("Tôi chuyển khoản nhưng đơn báo hết hạn thì sao?", new[]
            {
                "Liên hệ rạp ngay kèm mã đặt vé và ảnh giao dịch. Xem chi tiết ở Chính sách đặt vé và hoàn tiền."
            }),
            new InfoSection("Tôi có đổi hoặc hoàn vé được không?", new[]
            {
                "Vé đã thanh toán không đổi, không hoàn, trừ một số trường hợp đặc biệt do rạp xem xét (suất bị hủy, lỗi hệ thống, chuyển nhầm)."
            }),
            new InfoSection("Giá vé có ưu đãi gì?", new[]
            {
                "Suất trước 12 giờ trưa giảm 20%, thứ Tư giảm 25% (không cộng dồn, lấy mức cao hơn). Thành viên từ hạng Bạc còn được giảm tiền bắp nước, và có thể dùng mã giảm giá."
            }),
            new InfoSection("Điểm thành viên được cộng khi nào?", new[]
            {
                "Cứ 1,000 đ bạn thực trả cho một đơn được 1 điểm. Điểm được cộng khi vé của bạn được quét ở cổng rạp. Hạng được tính theo điểm nhận trong 12 tháng gần nhất: Bạc từ 1,000 điểm, Vàng từ 2,500 điểm, Kim cương từ 5,000 điểm."
            }),
            new InfoSection("Quà sinh nhật nhận như thế nào?", new[]
            {
                "Cập nhật ngày sinh trong Hồ sơ cá nhân. Trong tháng sinh nhật, thành viên từ hạng Bạc vào trang Thành viên và bấm Nhận quà sinh nhật để có mã giảm giá riêng: Bạc 50,000 đ, Vàng 100,000 đ, Kim cương 200,000 đ. Mỗi năm nhận một lần, hạn dùng đến hết tháng sinh nhật."
            }),
            new InfoSection("Vé trẻ em dành cho ai?", new[]
            {
                "Trẻ em dưới 13 tuổi đi cùng người lớn. Nếu không đúng đối tượng, nhân viên có thể thu chênh lệch giá vé hoặc từ chối vào rạp."
            })
        });
}