$env:TOLIK_POSTGRES_CONNECTION_STRING = "Host=localhost;Port=5432;Database=tolik;Username=tolik;Password=TolikSecureP@ss2026!"
cd C:\DEV\TOLIK_QWEN
dotnet ef migrations add AddMfaToDevice --project src\TOLIK.Infrastructure\TOLIK.Infrastructure.csproj --startup-project src\TOLIK.Core.Host\TOLIK.Core.Host.csproj