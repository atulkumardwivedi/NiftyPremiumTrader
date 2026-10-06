using Microsoft.AspNetCore.Mvc;
using NiftyPremiumTrader.Api.Data;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Services;

namespace NiftyPremiumTrader.Api.Controllers;

[ApiController]
[Route("api/data")]
public sealed class DataController : ControllerBase
{
    private readonly CandleRepository _repository;
    private readonly CandleDataService _dataService;
    private readonly ApiBudgetService _budgetService;

    public DataController(
        CandleRepository repository,
        CandleDataService dataService,
        ApiBudgetService budgetService)
    {
        _repository = repository;
        _dataService = dataService;
        _budgetService = budgetService;
    }

    [HttpGet("morning-selection")]
    public async Task<IActionResult> MorningSelection([FromQuery] DateOnly date,CancellationToken cancellationToken)
    {
        return Ok(await _repository.GetMorningSelectionAsync(date,cancellationToken));
    }

    [HttpPost("expiry")]
    public async Task<IActionResult> AddExpiry(
        [FromQuery] DateOnly expiryDate,
        CancellationToken cancellationToken)
    {
        await _repository.AddExpiryAsync(
            expiryDate,
            cancellationToken);

        return Ok(new
        {
            expiryDate
        });
    }

    [HttpPost("ensure-contract")]
    public async Task<IActionResult> EnsureContract(
        [FromQuery] DateOnly date,
        [FromQuery] DateOnly expiryDate,
        [FromQuery] decimal strike,
        [FromQuery] string optionType,
        [FromQuery] bool liveMode,
        CancellationToken cancellationToken)
    {
        List<OptionCandle> candles = await _dataService.EnsureContractAsync(
            date,
            expiryDate,
            strike,
            optionType,
            liveMode,
            cancellationToken);

        return Ok(new
        {
            date,
            expiryDate,
            strike,
            optionType,
            candleCount = candles.Count
        });
    }

    [HttpGet("candles")]
    public async Task<IActionResult> GetCandles(
        [FromQuery] DateOnly date,
        [FromQuery] DateOnly expiryDate,
        [FromQuery] decimal strike,
        [FromQuery] string optionType,
        [FromQuery] bool liveMode,
        CancellationToken cancellationToken)
    {
        List<OptionCandle> candles;

        if (liveMode)
        {
            // The live worker is the only component allowed to refresh ICICI
            // signal data every five minutes. The chart reads PostgreSQL only
            // so browser polling can never consume extra ICICI API calls.
            candles = await _dataService.GetStoredContractCandlesAsync(
                date,
                expiryDate,
                strike,
                optionType,
                true,
                cancellationToken);
        }
        else
        {
            candles = await _dataService.EnsureContractAsync(
                date,
                expiryDate,
                strike,
                optionType,
                false,
                cancellationToken);
        }

        return Ok(candles);
    }

    [HttpGet("hedge-preview")]
    public async Task<IActionResult> HedgePreview(
        [FromQuery] DateOnly date,
        [FromQuery] DateOnly expiryDate,
        [FromQuery] decimal soldStrike,
        [FromQuery] string optionType,
        [FromQuery] int hedgeDistance,
        [FromQuery] DateTimeOffset? replayTime,
        [FromQuery] bool liveMode,
        CancellationToken cancellationToken)
    {
        HedgePreview preview = await _dataService.GetHedgePreviewAsync(
            date,
            expiryDate,
            soldStrike,
            optionType,
            hedgeDistance,
            replayTime,
            liveMode,
            cancellationToken);

        return Ok(preview);
    }

    [HttpGet("budget")]
    public async Task<IActionResult> Budget(CancellationToken cancellationToken)
    {
        ApiBudgetStatus status = await _budgetService.GetStatusAsync(
            cancellationToken);

        return Ok(status);
    }
}
