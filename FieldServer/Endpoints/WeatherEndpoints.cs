namespace FieldServer.Endpoints;

/// <summary>
/// HTTP API 端点（原 TestServer 功能合并而来）。
/// 新增 HTTP 端点：仿照本文件新建 MapXxxEndpoints 扩展方法，
/// 并在 Program.cs 调用一行即可。
/// </summary>
public static class WeatherEndpoints
{
    private static readonly string[] Summaries =
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    public static void MapWeatherEndpoints(this WebApplication app)
    {
        app.MapGet("/weatherforecast", () =>
            Enumerable.Range(1, 5).Select(index =>
                new WeatherForecast(
                    DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    Random.Shared.Next(-20, 55),
                    Summaries[Random.Shared.Next(Summaries.Length)]))
            .ToArray())
        .WithName("GetWeatherForecast");
    }

    private sealed record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
    {
        public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
    }
}
