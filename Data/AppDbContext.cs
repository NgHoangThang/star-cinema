using CinemaApp.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CinemaApp.Data;

public class AppDbContext : IdentityDbContext<AppUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<City> Cities => Set<City>();
    public DbSet<Cinema> Cinemas => Set<Cinema>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<Showtime> Showtimes => Set<Showtime>();
    public DbSet<Banner> Banners => Set<Banner>();
    public DbSet<Booking> Bookings => Set<Booking>(); public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();
    public DbSet<ShowtimeSeat> ShowtimeSeats => Set<ShowtimeSeat>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<BookingItem> BookingItems => Set<BookingItem>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<VoucherUse> VoucherUses => Set<VoucherUse>();
        public DbSet<PointEntry> PointEntries => Set<PointEntry>();
    public DbSet<Article> Articles => Set<Article>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);   // bắt buộc: tạo các bảng đăng nhập của Identity

        b.Entity<Showtime>().HasIndex(s => new { s.MovieId, s.StartTime });
        b.Entity<Showtime>().HasIndex(s => new { s.RoomId, s.StartTime });
        b.Entity<Seat>().HasIndex(s => new { s.RoomId, s.RowLabel, s.SeatNumber }).IsUnique();
        b.Entity<Genre>().HasIndex(g => g.Name).IsUnique();
        b.Entity<Movie>().HasIndex(m => new { m.Status, m.ReleaseDate });
        b.Entity<AppUser>().HasIndex(u => u.PhoneNumber);

        // Chốt chặn cuối chống đặt trùng: một ghế chỉ có một dòng trong một suất chiếu
        b.Entity<ShowtimeSeat>().HasIndex(x => new { x.ShowtimeId, x.SeatId }).IsUnique();
        b.Entity<ShowtimeSeat>().HasIndex(x => x.HoldExpiresAt);
        b.Entity<ShowtimeSeat>().HasOne(x => x.Seat).WithMany().HasForeignKey(x => x.SeatId)
                                .OnDelete(DeleteBehavior.Restrict);
        b.Entity<ShowtimeSeat>().HasOne(x => x.Booking).WithMany(x => x.Seats).HasForeignKey(x => x.BookingId)
                                .OnDelete(DeleteBehavior.Cascade);
        b.Entity<ShowtimeSeat>().HasOne(x => x.Showtime).WithMany().HasForeignKey(x => x.ShowtimeId)
                                .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Booking>().HasIndex(x => new { x.UserId, x.ShowtimeId, x.Status }); b.Entity<Booking>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Booking>().HasOne(x => x.Showtime).WithMany().HasForeignKey(x => x.ShowtimeId)
                           .OnDelete(DeleteBehavior.Cascade);

        // Bắp nước: mỗi sản phẩm chỉ một dòng trong một đơn
        b.Entity<BookingItem>().HasIndex(x => new { x.BookingId, x.ProductId }).IsUnique();
        b.Entity<BookingItem>().HasOne(x => x.Booking).WithMany(x => x.Items).HasForeignKey(x => x.BookingId)
                               .OnDelete(DeleteBehavior.Cascade);
        b.Entity<BookingItem>().HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId)
                               .OnDelete(DeleteBehavior.Restrict);

        // Tin tức: đường dẫn (slug) không trùng; tìm bài đã đăng mới nhất nhanh
        b.Entity<Article>().HasIndex(a => a.Slug).IsUnique();
        b.Entity<Article>().HasIndex(a => new { a.IsPublished, a.PublishedAt });

        // Voucher: mã không trùng; mỗi đơn chỉ dùng tối đa một mã
        b.Entity<Voucher>().HasIndex(v => v.Code).IsUnique();
        b.Entity<VoucherUse>().HasIndex(x => x.BookingId).IsUnique();
        b.Entity<VoucherUse>().HasIndex(x => new { x.VoucherId, x.UserId });
        b.Entity<VoucherUse>().HasOne(x => x.Voucher).WithMany(v => v.Uses).HasForeignKey(x => x.VoucherId)
                              .OnDelete(DeleteBehavior.Cascade);
        b.Entity<VoucherUse>().HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.BookingId)
                              .OnDelete(DeleteBehavior.Cascade);
    }
}