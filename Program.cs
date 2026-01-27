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
while (true)
{
    try
    {
        Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] ⏳ Đang quét dữ liệu...");

        // A. Lấy giá BTC từ Binance (API công khai)
        decimal currentPrice = await GetBinancePrice();
        Console.WriteLine($"💰 Giá BTC hiện tại: {currentPrice} USD");

        // B. Lấy danh sách lệnh từ Supabase (Active & Chưa hết hạn)
        // Lưu ý: Thư viện Supabase C# dùng Model để map dữ liệu
        var response = await supabase.From<PriceAlert>()
                                     .Select("*")
                                     // SỬA LỖI Ở ĐÂY: Đổi true thành "true" (dạng chuỗi)
                                     .Filter("is_active", Postgrest.Constants.Operator.Equals, "true")
                                     .Filter("status", Postgrest.Constants.Operator.Equals, "PENDING")
                                     .Get();

        var alerts = response.Models;
        Console.WriteLine($"📋 Tìm thấy {alerts.Count} lệnh đang chờ.");

        // C. Duyệt và so sánh
        foreach (var alert in alerts)
        {
            // Bỏ qua nếu đã hết hạn (Check ngày tháng)
            if (alert.ExpiryDate < DateTime.UtcNow) continue;

            bool isTriggered = false;
            string type = "";

            if (alert.MinPrice > 0 && currentPrice <= alert.MinPrice)
            {
                isTriggered = true; type = "GIẢM SÂU (Min)";
            }
            else if (alert.MaxPrice > 0 && currentPrice >= alert.MaxPrice)
            {
                isTriggered = true; type = "TĂNG MẠNH (Max)";
            }

            if (isTriggered)
            {
                Console.WriteLine($"🔥 Trigger lệnh của: {alert.Email}");

                // Gửi Email
                SendEmail(alert.Email, type, currentPrice, resendKey);

                // Cập nhật trạng thái trong Database thành 'SENT'
                await supabase.From<PriceAlert>()
                              .Where(x => x.Id == alert.Id)
                              .Set(x => x.Status, "SENT")
                              .Set(x => x.IsActive, false)
                              .Update();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Lỗi vòng lặp: {ex.Message}");
    }

    // Nghỉ 10 giây
    await Task.Delay(10000);
}

// --- CÁC HÀM HỖ TRỢ ---

// 1. Hàm lấy giá Binance
async Task<decimal> GetBinancePrice()
{
    using var client = new HttpClient();
    var json = await client.GetStringAsync("https://api.binance.com/api/v3/ticker/price?symbol=BTCUSDT");
    dynamic? data = JsonConvert.DeserializeObject(json);
    return data?.price != null ? (decimal)data.price : 0;
}

// 2. Hàm gửi Email qua Resend SMTP
void SendEmail(string toEmail, string type, decimal price, string apiKey)
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
            Body = $"<h1>Giá BTC đã chạm ngưỡng!</h1><p>Giá hiện tại: <b>{price} USD</b></p>",
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

// --- MODEL DATABASE ---
[Table("price_alerts")]
public class PriceAlert : BaseModel
{
    [Column("id")]
    public string? Id { get; set; } // Thêm dấu ?

    [Column("email")]
    public string? Email { get; set; }

    [Column("min_price")]
    public decimal MinPrice { get; set; }

    [Column("max_price")]
    public decimal MaxPrice { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    [Column("status")]
    public string? Status { get; set; }

    [Column("expiry_date")]
    public DateTime ExpiryDate { get; set; }
}