using Npgsql;

var connString = "Host=localhost;Port=5432;Database=tolik;Username=tolik;Password=TolikSecureP@ss2026!";
try 
{
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    Console.WriteLine("Connection successful!");
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT 1";
    var result = await cmd.ExecuteScalarAsync();
    Console.WriteLine($"Result: {result}");
} 
catch (Exception ex) 
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine($"Stack: {ex.StackTrace}");
}