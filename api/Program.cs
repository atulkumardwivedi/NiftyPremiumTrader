using Microsoft.AspNetCore.Diagnostics;
using NiftyPremiumTrader.Api.Data;
using NiftyPremiumTrader.Api.Options;
using NiftyPremiumTrader.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BreezeOptions>(
    builder.Configuration.GetSection("Breeze"));

builder.Services.Configure<KotakNeoOptions>(
    builder.Configuration.GetSection("KotakNeo"));

builder.Services.Configure<ApiBudgetOptions>(
    builder.Configuration.GetSection("ApiBudget"));

builder.Services.Configure<StrategyOptions>(
    builder.Configuration.GetSection("Strategy"));

builder.Services.Configure<LiveTradingOptions>(
    builder.Configuration.GetSection("LiveTrading"));

builder.Services.AddSingleton<CandleRepository>();
builder.Services.AddSingleton<ICandleStore>(sp=>sp.GetRequiredService<CandleRepository>());
builder.Services.AddSingleton<TradeRepository>();
builder.Services.AddSingleton<BrokerActivityRepository>();
builder.Services.AddSingleton<ApiBudgetService>();
builder.Services.AddHttpClient("Breeze", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<BreezeRestClient>();
builder.Services.AddSingleton<BreezeMarketDataClient>();
builder.Services.AddSingleton<IBreezeDaySource>(sp=>sp.GetRequiredService<BreezeMarketDataClient>());

builder.Services.AddHttpClient("KotakNeo", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<KotakNeoClient>();
builder.Services.AddSingleton<KotakTotpService>();
builder.Services.AddSingleton<KotakAuthenticationService>();
builder.Services.AddSingleton<KotakInstrumentService>();
builder.Services.AddSingleton<KotakOrderService>();
builder.Services.Configure<PositionSizingOptions>(builder.Configuration.GetSection("PositionSizing"));
builder.Services.AddSingleton<PositionSizingService>();
builder.Services.AddSingleton<KotakBrokerActivityService>();
builder.Services.AddSingleton<KotakBrokerRealtimeService>();
builder.Services.AddHostedService<KotakBrokerRealtimeService>(sp =>
    sp.GetRequiredService<KotakBrokerRealtimeService>());
builder.Services.AddSingleton<CandleDataService>();
builder.Services.AddSingleton<PremiumSignalService>();
builder.Services.AddSingleton<StrategyEngine>();
builder.Services.AddSingleton<BacktestService>();
builder.Services.AddSingleton<LiveTradingService>();
builder.Services.AddHostedService<LiveTradingWorker>();

builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
            "http://localhost:4200",
            "https://localhost:4200");
        policy.AllowAnyHeader();
        policy.AllowAnyMethod();
        policy.AllowCredentials();
    });
});

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        IExceptionHandlerFeature? feature =
            context.Features.Get<IExceptionHandlerFeature>();

        Exception? exception = feature?.Error;

        context.Response.StatusCode = exception switch
        {
            ArgumentException => StatusCodes.Status400BadRequest,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            ApiBudgetExceededException => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status500InternalServerError
        };

        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            message = exception?.Message ?? "Unexpected API error."
        });
    });
});

app.UseCors();
app.MapControllers();
app.MapHub<BrokerRealtimeHub>("/hubs/broker");

using (IServiceScope scope = app.Services.CreateScope())
{
    CandleRepository repository =
        scope.ServiceProvider.GetRequiredService<CandleRepository>();

    await repository.EnsureSchemaAsync(CancellationToken.None);
}

app.Run();
