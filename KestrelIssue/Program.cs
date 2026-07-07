var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// BUG REPRO — see README.md for the full write-up and exact reproduction matrix.
//
// On macOS 26.5.2 (Darwin 25.5.0) + .NET 10.0.9, launched via the apphost (dotnet run / Rider Run),
// a connection to a secondary loopback alias (e.g. `sudo ifconfig lo0 alias 10.100.100.101`) is
// broken — genuine 127.0.0.1 always works fine. The exact symptom depends on how Kestrel is bound:
//
//   - options.ListenAnyIP(5000)                 -> process CRASHES on accept (unhandled ArgumentException)
//   - options.Listen(IPAddress.Any, 5000)       -> connection is silently dropped (no crash, no response)
//
// Toggle between the two lines below to see each symptom. Neither reproduces via a raw
// `dotnet <path-to-dll>` exec (through the shared framework host), and neither reproduces on .NET 8,
// on the same machine and launch method.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000); // crashes the process on accept from the alias address
    // options.Listen(System.Net.IPAddress.Any, 5000); // silently drops the connection instead, no crash
});

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.WriteLine();
    Console.WriteLine("=== UNHANDLED EXCEPTION (this is the bug) ===");
    Console.WriteLine(e.ExceptionObject);
};

Console.WriteLine($"OS: {Environment.OSVersion}");
Console.WriteLine($".NET: {Environment.Version}");
Console.WriteLine($"Arch: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
    {
        var forecast = Enumerable.Range(1, 5).Select(index =>
                new WeatherForecast
                (
                    DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    Random.Shared.Next(-20, 55),
                    summaries[Random.Shared.Next(summaries.Length)]
                ))
            .ToArray();
        return forecast;
    })
    .WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}