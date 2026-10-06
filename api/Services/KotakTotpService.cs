using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Options;
using OtpNet;

namespace NiftyPremiumTrader.Api.Services;

public sealed class KotakTotpService
{
    private readonly KotakNeoOptions _options;

    public KotakTotpService(
        IOptions<KotakNeoOptions> options)
    {
        _options = options.Value;
    }

    public string GenerateOTP()
    {
        if (string.IsNullOrWhiteSpace(_options.TotpSecret))
        {
            throw new InvalidOperationException(
                "KotakNeo:TotpSecret is not configured.");
        }

        byte[] secretBytes = Base32Encoding.ToBytes(
            _options.TotpSecret.Trim());

        var totp = new Totp(secretBytes);

        string totpCode = totp.ComputeTotp();

        return totpCode;
    }

    public string GenerateCurrentCode()
    {
        return GenerateOTP();
    }
}
