namespace CinemaApp.Models;

// Nhật ký thao tác của quản trị viên: ai làm gì, lúc nào, lên đối tượng nào
public class AdminAuditLog
{
    public int Id { get; set; }
    public DateTime At { get; set; }                 // UTC
    public string AdminEmail { get; set; } = "";
    public string Action { get; set; } = "";         // Approve, Revive, Refund, VoucherDelete...
    public string Target { get; set; } = "";         // ví dụ: Booking:103
    public string Detail { get; set; } = "";         // số tiền, mã giao dịch, lý do...
}