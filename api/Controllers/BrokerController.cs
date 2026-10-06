using Microsoft.AspNetCore.Mvc;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Services;

namespace NiftyPremiumTrader.Api.Controllers;

[ApiController]
[Route("api/broker")]
public sealed class BrokerController : ControllerBase
{
    private readonly KotakBrokerActivityService _brokerService;
    private readonly KotakBrokerRealtimeService _realtimeService;

    public BrokerController(
        KotakBrokerActivityService brokerService,
        KotakBrokerRealtimeService realtimeService)
    {
        _brokerService = brokerService;
        _realtimeService = realtimeService;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(
        CancellationToken cancellationToken)
    {
        BrokerDashboardResponse dashboard =
            await _brokerService.GetDashboardAsync(cancellationToken);

        dashboard.RealtimeConnected = _realtimeService.IsConnected;
        dashboard.RealtimeSubscriptions = _realtimeService.SubscriptionCount;

        return Ok(dashboard);
    }

    [HttpPost("manual-order")]
    public async Task<IActionResult> ManualOrder(
        [FromBody] ManualBrokerOrderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            KotakOrderExecutionResult result =
                await _brokerService.PlaceManualOrderAsync(
                    request,
                    cancellationToken);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("exit-position")]
    public async Task<IActionResult> ExitPosition(
        [FromBody] ExitBrokerPositionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            KotakOrderExecutionResult result =
                await _brokerService.ExitManualPositionAsync(
                    request,
                    cancellationToken);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
