using System.Text.Json;
using Wms.Simulation;

const double MinimumOperationSeconds = 0.05;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddSingleton<SimulationEngine>();
builder.Services.AddHostedService<SimulationTicker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/simulation", (SimulationEngine engine) => engine.GetSnapshot());
app.MapPost("/api/control/play", (SimulationEngine engine) => engine.Play());
app.MapPost("/api/control/pause", (SimulationEngine engine) => engine.Pause());
app.MapPost("/api/control/previous", (SimulationEngine engine) => engine.StepPrevious());
app.MapPost("/api/control/next", (SimulationEngine engine) => engine.StepNext());
app.MapPost("/api/control/reset", (SimulationEngine engine) => engine.Reset());
app.MapPut("/api/settings", (SimulationSettingsRequest request, SimulationEngine engine) =>
{
    var unloadCycleSeconds = request.UnloadCycleSeconds ?? request.UnloadSeconds ?? 300;
    var unloadStackSeconds = request.UnloadStackSeconds ?? 5;

    if (!double.IsFinite(request.LoadSeconds)
        || !double.IsFinite(unloadCycleSeconds)
        || !double.IsFinite(unloadStackSeconds))
    {
        return Results.BadRequest(new { message = "Czasy musza byc prawidlowymi liczbami." });
    }

    if (request.LoadSeconds < MinimumOperationSeconds
        || unloadCycleSeconds < MinimumOperationSeconds
        || unloadStackSeconds < MinimumOperationSeconds)
    {
        return Results.BadRequest(new { message = "Minimalny czas operacji to 0.05 s." });
    }

    return Results.Ok(engine.UpdateSettings(
        TimeSpan.FromSeconds(request.LoadSeconds),
        TimeSpan.FromSeconds(unloadCycleSeconds),
        TimeSpan.FromSeconds(unloadStackSeconds)));
});

app.Run();

internal sealed record SimulationSettingsRequest
{
    public double LoadSeconds { get; init; }

    public double? UnloadSeconds { get; init; }

    public double? UnloadCycleSeconds { get; init; }

    public double? UnloadStackSeconds { get; init; }
}

internal sealed class SimulationTicker(SimulationEngine engine) : BackgroundService
{
    private const int TickerMilliseconds = 25;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickerMilliseconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            engine.Tick();
        }
    }
}
