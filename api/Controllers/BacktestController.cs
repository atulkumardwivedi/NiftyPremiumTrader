using Microsoft.AspNetCore.Mvc;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Services;

namespace NiftyPremiumTrader.Api.Controllers;

[ApiController]
[Route("api/backtest")]
public sealed class BacktestController : ControllerBase
{
    private readonly BacktestService _backtestService;

    public BacktestController(BacktestService backtestService)
    {
        _backtestService = backtestService;
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start(
        [FromBody] StartBacktestRequest request,
        CancellationToken cancellationToken)
    {
        StepBacktestResponse result = await _backtestService.StartAsync(
            request,
            cancellationToken);

        return Ok(result);
    }

    [HttpPost("step/{sessionId:guid}")]
    public async Task<IActionResult> Step(
        Guid sessionId,
        [FromQuery] int direction,
        CancellationToken cancellationToken)
    {
        if (direction != -1 && direction != 1)
        {
            return BadRequest(new
            {
                message = "direction must be -1 or 1."
            });
        }

        StepBacktestResponse result = await _backtestService.MoveAsync(
            sessionId,
            direction,
            cancellationToken);

        return Ok(result);
    }

    [HttpPost("range")]
    public async Task<IActionResult> Range(
        [FromBody] RangeBacktestRequest request,
        CancellationToken cancellationToken)
    {
        RangeBacktestResult result = await _backtestService.RunRangeAsync(
            request,
            cancellationToken);

        return Ok(result);
    }
}
