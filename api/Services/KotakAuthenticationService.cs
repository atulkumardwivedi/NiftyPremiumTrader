using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class KotakAuthenticationService
{
    private readonly KotakNeoClient _client;
    private readonly KotakTotpService _totpService;
    private readonly KotakNeoOptions _options;
    private readonly SemaphoreSlim _loginGate = new(1, 1);

    public KotakAuthenticationService(
        KotakNeoClient client,
        KotakTotpService totpService,
        IOptions<KotakNeoOptions> options)
    {
        _client = client;
        _totpService = totpService;
        _options = options.Value;
    }

    public bool IsAuthenticated => _client.IsAuthenticated;

    public async Task EnsureAuthenticatedAsync(
        CancellationToken cancellationToken)
    {
        if (_client.IsAuthenticated)
        {
            return;
        }

        if (!_options.AutoLogin)
        {
            throw new InvalidOperationException(
                "Kotak Neo is not authenticated and KotakNeo:AutoLogin is false.");
        }

        await _loginGate.WaitAsync(cancellationToken);

        try
        {
            if (_client.IsAuthenticated)
            {
                return;
            }

            string totp = _totpService.GenerateCurrentCode();

            await _client.LoginWithTotpAsync(
                totp,
                cancellationToken);

            await _client.ValidateMpinAsync(
                cancellationToken);
        }
        finally
        {
            _loginGate.Release();
        }
    }
}
