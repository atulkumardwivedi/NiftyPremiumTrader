using Microsoft.AspNetCore.Mvc;
using NiftyPremiumTrader.Api.Services;
namespace NiftyPremiumTrader.Api.Controllers;
[ApiController,Route("api/sizing")]
public sealed class SizingController(PositionSizingService sizing):ControllerBase
{
 [HttpGet("setup")] public IActionResult Setup(int lots=7,int lotSize=65,string profile="ScaledWinner")=>Ok(sizing.GetSetup(lots,lotSize,profile));
 [HttpGet("funds")] public async Task<IActionResult> Funds(CancellationToken ct)=>Ok(new {availableMargin=await sizing.AvailableAsync(ct),checkedAt=DateTimeOffset.UtcNow});
 [HttpPost("preview")] public async Task<IActionResult> Preview([FromQuery]DateOnly? expiryDate,[FromQuery]int lots=7,[FromQuery]bool automatic=false,[FromQuery]string profile="ScaledWinner",CancellationToken ct=default)=>Ok(await sizing.PreviewAsync(expiryDate,lots,automatic,profile,ct));
}
