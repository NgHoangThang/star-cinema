using CinemaApp.Models;

namespace CinemaApp.Data;

public static class SeatSeeder
{
    // Tạo 120 ghế: hàng A, B có 12 ghế; hàng C đến H có 16 ghế (24 + 96 = 120)
    public static List<Seat> BuildSeats(int roomId)
    {
        var seats = new List<Seat>();
        var rows = "ABCDEFGH";
        for (int r = 0; r < rows.Length; r++)
        {
            int count = r < 2 ? 12 : 16;
            int offset = r < 2 ? 2 : 0;   // hàng A, B lùi vào giữa
            for (int n = 1; n <= count; n++)
            {
                seats.Add(new Seat
                {
                    RoomId = roomId,
                    RowLabel = rows[r].ToString(),
                    SeatNumber = n,
                    SeatType = r >= 5 ? "VIP" : "Standard",   // hàng F, G, H là VIP
                    PosX = offset + n + (n - 1) / 4,          // chừa lối đi sau mỗi 4 ghế
                    PosY = r
                });
            }
        }
        return seats;
    }

    // Sơ đồ ghế tùy chỉnh khi admin thêm phòng: rows hàng (A, B, C...), mỗi hàng perRow ghế.
    // vipFromRow = số thứ tự hàng đầu tiên là VIP (1 = hàng A), 0 = không có ghế VIP.
    public static List<Seat> Build(int roomId, int rows, int perRow, int vipFromRow)
    {
        const string letters = "ABCDEFGHIJKL";
        var seats = new List<Seat>();
        for (int r = 0; r < rows; r++)
        {
            for (int n = 1; n <= perRow; n++)
            {
                seats.Add(new Seat
                {
                    RoomId = roomId,
                    RowLabel = letters[r].ToString(),
                    SeatNumber = n,
                    SeatType = vipFromRow > 0 && r + 1 >= vipFromRow ? "VIP" : "Standard",
                    PosX = n + (n - 1) / 4,   // chừa lối đi sau mỗi 4 ghế
                    PosY = r
                });
            }
        }
        return seats;
    }
}