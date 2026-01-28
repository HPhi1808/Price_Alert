using System.Net;
using System.Net.Mail;
using DotNetEnv;
using Supabase;
using Postgrest.Attributes;
using Postgrest.Models;
using Newtonsoft.Json;

// --- 1. KHỞI TẠO MÔI TRƯỜNG ---
Env.Load(); // Đọc file .env

var sbUrl = Environment.GetEnvironmentVariable("SUPABASE_URL");
var sbKey = Environment.GetEnvironmentVariable("SUPABASE_KEY");
var resendKey = Environment.GetEnvironmentVariable("RESEND_API_KEY");

if (string.IsNullOrEmpty(sbUrl) || string.IsNullOrEmpty(resendKey))
{
    Console.WriteLine("❌ LỖI: Chưa điền đủ thông tin trong file .env");
    return;
}

// Khởi tạo Supabase Client
var options = new Supabase.SupabaseOptions { AutoRefreshToken = true, AutoConnectRealtime = true };
var supabase = new Supabase.Client(sbUrl, sbKey, options);
await supabase.InitializeAsync();

Console.WriteLine("✅ Đã kết nối Supabase & Sẵn sàng quét giá!");

// --- 2. TẠO WEB SERVER GIẢ (Để Render không tắt App) ---
var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
var app = builder.Build();
app.MapGet("/", () => "Worker C# is running...");
_ = app.RunAsync($"http://0.0.0.0:{port}");

// --- 3. VÒNG LẶP WORKER ---
// --- 3. VÒNG LẶP WORKER (LOGIC MỚI) ---
while (true)
{
    try
    {
        Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] ⏳ Quét lệnh...");

        // B1. Lấy TẤT CẢ lệnh đang chờ
        var response = await supabase.From<PriceAlert>()
                                     .Select("*")
                                     .Filter("is_active", Postgrest.Constants.Operator.Equals, "true")
                                     .Filter("status", Postgrest.Constants.Operator.Equals, "PENDING")
                                     .Get();
        
        var alerts = response.Models;

        // B2. Lấy danh sách các cặp tiền cần check (Distinct)
        var uniqueSymbols = alerts.Select(a => a.Symbol).Distinct().ToList();

        if (uniqueSymbols.Count == 0) {
            Console.WriteLine("💤 Không có lệnh nào cần xử lý.");
        }

        // B3. Duyệt từng Symbol để lấy giá và so sánh
        foreach (var symbol in uniqueSymbols)
        {
            // Lấy giá của Symbol này (Dynamic URL)
            decimal currentPrice = await GetCryptoPrice(symbol);
            if (currentPrice == 0) continue;

            Console.WriteLine($"💰 {symbol}: {currentPrice} USD");

            // Lọc ra các lệnh thuộc Symbol này để check
            var alertsForSymbol = alerts.Where(a => a.Symbol == symbol).ToList();

            foreach (var alert in alertsForSymbol)
            {
                if (alert.ExpiryDate < DateTime.UtcNow) continue;

                bool isTriggered = false;
                string type = "";

                if (alert.MinPrice > 0 && currentPrice <= alert.MinPrice)
                {
                    isTriggered = true; type = $"GIẢM SÂU ({symbol})";
                }
                else if (alert.MaxPrice > 0 && currentPrice >= alert.MaxPrice)
                {
                    isTriggered = true; type = $"TĂNG MẠNH ({symbol})";
                }

                if (isTriggered)
                {
                    Console.WriteLine($"🔥 Trigger {symbol} cho: {alert.Email}");
                    
                    SendEmail(alert.Email!, type, currentPrice, symbol, resendKey!);

                    // Update DB
                    await supabase.From<PriceAlert>()
                                  .Where(x => x.Id == alert.Id)
                                  .Set(x => x.Status, "SENT")
                                  .Set(x => x.IsActive, false)
                                  .Update();
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Lỗi: {ex.Message}");
    }

    await Task.Delay(10000);
}

// --- CÁC HÀM HỖ TRỢ ---

// 1. Hàm lấy giá Binance
// Nhận tham số symbol động
// Đổi tên hàm cho đúng ý nghĩa
async Task<decimal> GetCryptoPrice(string symbol)
{
    try 
    {
        using var client = new HttpClient();
        
        // 1. Chuyển đổi Symbol sang ID của CoinCap
        // CoinCap dùng id là 'bitcoin', 'ethereum' chứ không dùng 'BTCUSDT'
        string coinId = "bitcoin"; 
        if (symbol.StartsWith("ETH")) coinId = "ethereum";
        if (symbol.StartsWith("BNB")) coinId = "binance-coin";
        if (symbol.StartsWith("SOL")) coinId = "solana";
        if (symbol == "GOLD") return 2035; // Vàng vẫn phải xử lý riêng nếu chưa có API

        // 2. Gọi API CoinCap (Thêm timestamp để chống Cache)
        var url = $"https://api.coincap.io/v2/assets/{coinId}?t={DateTime.Now.Ticks}";
        
        var json = await client.GetStringAsync(url);
        
        // 3. Phân tích JSON
        // Cấu trúc CoinCap: { "data": { "priceUsd": "89000.123" } }
        dynamic? response = JsonConvert.DeserializeObject(json);
        string priceString = response?.data?.priceUsd;
        
        if (decimal.TryParse(priceString, out decimal price))
        {
            return price;
        }
        
        return 0;
    } 
    catch (Exception ex)
    { 
        Console.WriteLine($"⚠️ Lỗi lấy giá {symbol}: {ex.Message}");
        return 0; 
    }
}
// 2. Hàm gửi Email qua Resend SMTP
void SendEmail(string toEmail, string type, decimal price, string symbol, string apiKey)
{
    try
    {
        var smtpClient = new SmtpClient("smtp.resend.com")
        {
            Port = 587,
            Credentials = new NetworkCredential("resend", apiKey),
            EnableSsl = true,
        };

        var mailMessage = new MailMessage
        {
            From = new MailAddress("noreply@uth.asia", "Price Alert Bot"),
            Subject = $"🚨 CẢNH BÁO: {type}",
            Body = $"<h1>Giá {symbol} đã chạm ngưỡng!</h1><p>Giá hiện tại: <b>{price} USD</b></p>",
            IsBodyHtml = true,
        };

        mailMessage.To.Add(toEmail);
        smtpClient.Send(mailMessage);
        Console.WriteLine($"📧 Đã gửi email tới {toEmail}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Lỗi gửi mail: {ex.Message}");
    }
}

// --- MODEL CẬP NHẬT (Thêm Symbol) ---
[Table("price_alerts")]
public class PriceAlert : BaseModel
{
    [Column("id")] public string? Id { get; set; }
    [Column("email")] public string? Email { get; set; }
    [Column("symbol")] public string Symbol { get; set; } = "BTCUSDT";
    [Column("min_price")] public decimal MinPrice { get; set; }
    [Column("max_price")] public decimal MaxPrice { get; set; }
    [Column("is_active")] public bool IsActive { get; set; }
    [Column("status")] public string? Status { get; set; }
    [Column("expiry_date")] public DateTime ExpiryDate { get; set; }
}