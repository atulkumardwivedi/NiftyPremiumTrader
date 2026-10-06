using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class LiveTradingWorker : BackgroundService
{
    private readonly LiveTradingService _liveTradingService;
    private readonly LiveTradingOptions _options;
    private readonly ILogger<LiveTradingWorker> _logger;

    public LiveTradingWorker(
        LiveTradingService liveTradingService,
        IOptions<LiveTradingOptions> options,
        ILogger<LiveTradingWorker> logger)
    {
        _liveTradingService = liveTradingService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Live trading worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _liveTradingService.ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Live strategy worker failed.");

                _liveTradingService.HandleWorkerError(ex);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.WorkerPollSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Live trading worker stopped.");
    }
}
