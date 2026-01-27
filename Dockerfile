# --- GIAI ĐOẠN 1: BUILD (Dùng bộ SDK nặng để biên dịch) ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy file project và cài thư viện
COPY ["PriceAlertWorker.csproj", "./"]
RUN dotnet restore "PriceAlertWorker.csproj"

# Copy toàn bộ code và build ra file chạy
COPY . .
RUN dotnet publish "PriceAlertWorker.csproj" -c Release -o /app/publish

# --- GIAI ĐOẠN 2: RUN (Dùng bộ Runtime nhẹ để chạy) ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Lấy kết quả đã build từ giai đoạn 1
COPY --from=build /app/publish .

# Cấu hình Port cho Render (Render Free Tier yêu cầu Port 8080)
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Lệnh chạy ứng dụng
ENTRYPOINT ["dotnet", "PriceAlertWorker.dll"]