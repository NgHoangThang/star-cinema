using System.Text;
using QRCoder;

namespace CinemaApp.Services;

// Dựng mã VietQR (chuẩn EMVCo, NAPAS 247) cho từng đơn: có sẵn số tài khoản, số tiền, nội dung chuyển khoản
public static class VietQr
{
    private static string Tag(string id, string value) => id + value.Length.ToString("00") + value;

    public static string BuildPayload(string bankBin, string accountNo, long amount, string memo)
    {
        var bankInfo = Tag("00", bankBin) + Tag("01", accountNo);
        var merchant = Tag("00", "A000000727") + Tag("01", bankInfo) + Tag("02", "QRIBFTTA");

        var sb = new StringBuilder();
        sb.Append(Tag("00", "01"));          // phiên bản
        sb.Append(Tag("01", amount > 0 ? "12" : "11"));   // 12: dùng một lần, có số tiền; 11: không có số tiền
        sb.Append(Tag("38", merchant));      // thông tin tài khoản nhận
        sb.Append(Tag("53", "704"));         // tiền tệ VND
        if (amount > 0) sb.Append(Tag("54", amount.ToString()));   // số tiền 0 thì bỏ thẻ này
        sb.Append(Tag("58", "VN"));
        sb.Append(Tag("62", Tag("08", memo)));   // nội dung chuyển khoản
        sb.Append("6304");
        sb.Append(Crc16(sb.ToString()));
        return sb.ToString();
    }

    private static string Crc16(string s)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in Encoding.ASCII.GetBytes(s))
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc.ToString("X4");
    }

    public static string QrDataUrl(string payload)
    {
        var gen = new QRCodeGenerator();
        var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}