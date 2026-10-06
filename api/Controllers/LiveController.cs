using Microsoft.AspNetCore.Mvc;
using NiftyPremiumTrader.Api.Data;
using NiftyPremiumTrader.Api.Services;

namespace NiftyPremiumTrader.Api.Controllers;

[ApiController]
[Route("api/live")]
public sealed class LiveController : ControllerBase
{
    private readonly LiveTradingService _liveTradingService;
    private readonly TradeRepository _tradeRepository;

    public LiveController(
        LiveTradingService liveTradingService,
        TradeRepository tradeRepository)
    {
        _liveTradingService = liveTradingService;
        _tradeRepository = tradeRepository;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start(
        [FromQuery] DateOnly? expiryDate,
        [FromQuery] int hedgeDistance = 300,
        [FromQuery] int lots = 7,
        [FromQuery] int lotSize = 65,
        [FromQuery] bool executeBrokerOrders = false,
        [FromQuery] bool autoLots = false,
        [FromQuery] string setupProfile = "ScaledWinner",
        CancellationToken cancellationToken = default)
    {
        LiveRuntimeState state = await _liveTradingService.StartAsync(
            expiryDate,
            hedgeDistance,
            lots,
            lotSize,
            executeBrokerOrders,
            cancellationToken, autoLots, setupProfile);

        return Ok(state);
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        return Ok(_liveTradingService.Stop());
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        LiveRuntimeState state = _liveTradingService.GetState();

        List<NiftyPremiumTrader.Api.Models.StrategyTrade> allTrades =
            await _tradeRepository.GetTradesAsync(
                MarketClock.IndiaDateNow(),
                cancellationToken);

        List<NiftyPremiumTrader.Api.Models.StrategyTrade> trades = allTrades
            .Where(x =>
            {
                bool looksLikeBrokerTrade =
                    x.BrokerExecution ||
                    !string.IsNullOrWhiteSpace(x.SoldEntryOrderId);

                return state.ExecuteBrokerOrders
                    ? looksLikeBrokerTrade
                    : !looksLikeBrokerTrade;
            })
            .ToList();

        return Ok(new
        {
            state,
            trades
        });
    }
}
