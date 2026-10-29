namespace CinemaApp.Models;

public class Booking
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int ShowtimeId { get; set; }
    public Showtime Showtime { get; set; } = null!;
    public string Status { get; set; } = "Pending";   // Pending, Paid, Expired, Cancelled
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public decimal Total { get; set; }

    // Thanh toán
    public string? Code { get; set; }                  // mã đặt vé, chỉ có khi đã thanh toán
    public DateTime? PaidAt { get; set; }
    public string? PaymentMethod { get; set; }         // Card, Wallet, Counter (giả lập)
    public decimal DiscountAmount { get; set; } public decimal MemberDiscount { get; set; } public string? MemberTierName { get; set; }        // số tiền giảm bằng mã giảm giá (làm ở ngày 25)
    public string? VoucherCode { get; set; } public string? ExpiredSeats { get; set; } public DateTime? TicketEmailedAt { get; set; } public DateTime? RefundedAt { get; set; } public string? RefundedByEmail { get; set; } public string? RefundReason { get; set; } public string? ApprovedByEmail { get; set; } public DateTime? ApprovedAt { get; set; } public DateTime? CheckedInAt { get; set; } public string? CheckedInByEmail { get; set; } public decimal? ReceivedAmount { get; set; } public string? BankRef { get; set; }
    public DateTime? CancelRequestedAt { get; set; }   // khách gửi yêu cầu hủy vé (chờ admin hoàn tiền)
    public string? CancelRequestReason { get; set; }

    public List<ShowtimeSeat> Seats { get; set; } = new();
    public List<BookingItem> Items { get; set; } = new();
}

public class ShowtimeSeat
{
    public int Id { get; set; }
    public int ShowtimeId { get; set; }
    public Showtime Showtime { get; set; } = null!;
    public int SeatId { get; set; }
    public Seat Seat { get; set; } = null!;
    public string Status { get; set; } = "Held";       // Held, Sold
    public string TicketType { get; set; } = "Adult";  // Adult, Child
    public string UserId { get; set; } = "";
    public int BookingId { get; set; }
    public Booking Booking { get; set; } = null!;
    public DateTime HoldExpiresAt { get; set; }
    public decimal Price { get; set; }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string ImageUrl { get; set; } = "";
    public string Badge { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class BookingItem
{
    public int Id { get; set; }
    public int BookingId { get; set; }
    public Booking Booking { get; set; } = null!;
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }   // giá chốt lúc mua, đổi giá sau này không ảnh hưởng đơn cũ
}