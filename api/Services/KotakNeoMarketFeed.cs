using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using NiftyPremiumTrader.Api.Models;

namespace NiftyPremiumTrader.Api.Services;

public sealed class MarketFeedOptions
{
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxReconnectAttempts { get; init; } = 0;
    public int MaxSubscriptions { get; init; } = 3000;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Native C# SFeed WebSocket client for the current Kotak Neo market feed.
/// It keeps the subscription set and automatically reconnects/re-subscribes.
/// </summary>
public sealed class KotakNeoMarketFeed : IAsyncDisposable
{
    private readonly string _url;
    private readonly string _ucc;
    private readonly string _sid;
    private readonly MarketFeedOptions _options;
    private readonly HashSet<WsToken> _subscriptions = [];
    private readonly Dictionary<int, int> _dividers = [];
    private readonly Dictionary<string, string> _symbols =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _socketGate = new(1, 1);

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private int _reconnectAttempts;
    private bool _disposed;

    public event Func<MarketTick, Task>? TickReceived;
    public event Func<IndexTick, Task>? IndexReceived;
    public event Func<MarketStatusTick, Task>? MarketStatusReceived;
    public event Func<Exception, Task>? Error;
    public event Func<Task>? ConnectionRestored;

    public bool IsConnected =>
        _webSocket?.State == WebSocketState.Open;

    public KotakNeoMarketFeed(
        string url,
        string ucc,
        string sid,
        MarketFeedOptions options)
    {
        _url = url;
        _ucc = ucc;
        _sid = sid;
        _options = options;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(KotakNeoMarketFeed));
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);

        await ConnectOnceAsync(_cts.Token);

        _reconnectAttempts = 0;
        _receiveTask = Task.Run(
            () => ReceiveLoopAsync(_cts.Token),
            _cts.Token);
    }

    public Task SubscribeScripsAsync(
        IEnumerable<WsToken> tokens,
        CancellationToken cancellationToken = default)
    {
        return SubscribeAsync(
            "subscribeScrips",
            tokens,
            cancellationToken);
    }

    public Task UnsubscribeScripsAsync(
        IEnumerable<WsToken> tokens,
        CancellationToken cancellationToken = default)
    {
        return UnsubscribeAsync(
            "unsubscribeScrips",
            tokens,
            cancellationToken);
    }

    public Task SubscribeDepthAsync(
        IEnumerable<WsToken> tokens,
        CancellationToken cancellationToken = default)
    {
        return SubscribeAsync(
            "subscribeDepth",
            tokens,
            cancellationToken);
    }

    public Task SubscribeLiteAsync(
        IEnumerable<WsToken> tokens,
        CancellationToken cancellationToken = default)
    {
        return SubscribeAsync(
            "subscribeScripsLite",
            tokens,
            cancellationToken);
    }

    public Task SubscribeIndexAsync(
        IEnumerable<WsToken> tokens,
        CancellationToken cancellationToken = default)
    {
        return SubscribeAsync(
            "subscribeIndices",
            tokens,
            cancellationToken);
    }

    public async Task SubscribeExchangeAsync(
        CancellationToken cancellationToken = default)
    {
        await SendAsync(
            "{\"event\":\"subscribeExchange\"}",
            cancellationToken);
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        await _socketGate.WaitAsync(cancellationToken);

        try
        {
            _webSocket?.Dispose();

            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = _options.PingInterval;
            _webSocket = socket;

            await socket.ConnectAsync(
                new Uri(_url),
                cancellationToken);

            var authentication = new
            {
                user = _ucc,
                auth = _sid,
                format = "native_batch",
                source = "NEOTRADEAPI",
                platform = "Web",
                version = "1.2.3",
                sdk_version = 2,
                sdk_date = "2026-08-07T09:41:17.667Z",
                conn_req_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sessionValidation = false
            };

            await SendAsync(
                JsonSerializer.Serialize(authentication),
                cancellationToken);

            await ReadAuthenticationAcknowledgementAsync(
                cancellationToken);

            if (_subscriptions.Count > 0)
            {
                await SendSubscriptionFrameAsync(
                    "subscribeScrips",
                    _subscriptions,
                    cancellationToken);
            }

            if (ConnectionRestored is not null)
            {
                await ConnectionRestored();
            }
        }
        finally
        {
            _socketGate.Release();
        }
    }

    private async Task ReadAuthenticationAcknowledgementAsync(
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var message = await ReceiveAsync(cancellationToken);

            if (message.Type != WebSocketMessageType.Text)
            {
                continue;
            }

            using var document = JsonDocument.Parse(message.Data);
            var root = document.RootElement;

            if (!root.TryGetProperty("message_code", out var code))
            {
                continue;
            }

            var messageCode = code.GetInt32();

            if (messageCode is not 1117 and not 1119)
            {
                continue;
            }

            if (root.TryGetProperty("exchanges", out var exchanges))
            {
                foreach (var exchange in exchanges.EnumerateObject())
                {
                    if (!exchange.Value.TryGetProperty(
                            "value",
                            out var value))
                    {
                        continue;
                    }

                    var exchangeId = value.GetInt32();
                    var divider = exchange.Value.TryGetProperty(
                        "divider",
                        out var dividerProperty)
                        ? dividerProperty.GetInt32()
                        : 100;

                    _dividers[exchangeId] =
                        divider > 0 ? divider : 100;
                }
            }

            return;
        }

        throw new WebSocketException(
            "Kotak SFeed authentication acknowledgement was not received.");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested &&
                           IsConnected)
                    {
                        var message = await ReceiveAsync(
                            cancellationToken);

                        if (message.Type == WebSocketMessageType.Text)
                        {
                            HandleText(message.Data);
                        }
                        else if (message.Type == WebSocketMessageType.Binary)
                        {
                            await HandleBinaryAsync(
                                message.Data);
                        }
                    }

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        throw new WebSocketException(
                            "Kotak SFeed connection closed.");
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (Error is not null)
                    {
                        await Error(ex);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await ReconnectAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_options.MaxReconnectAttempts > 0 &&
                _reconnectAttempts >= _options.MaxReconnectAttempts)
            {
                throw new WebSocketException(
                    "Kotak SFeed maximum reconnect attempts reached.");
            }

            _reconnectAttempts++;

            await Task.Delay(
                _options.ReconnectDelay,
                cancellationToken);

            try
            {
                await ConnectOnceAsync(cancellationToken);
                _reconnectAttempts = 0;
                return;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (Error is not null)
                {
                    await Error(ex);
                }
            }
        }
    }

    private async Task SubscribeAsync(
        string eventName,
        IEnumerable<WsToken> tokens,
        CancellationToken cancellationToken)
    {
        var tokenList = tokens
            .Distinct()
            .ToList();

        if (tokenList.Count == 0)
        {
            return;
        }

        var newTokens = tokenList
            .Where(x => !_subscriptions.Contains(x))
            .ToList();

        if (_subscriptions.Count + newTokens.Count >
            _options.MaxSubscriptions)
        {
            throw new InvalidOperationException(
                $"Kotak Neo subscription limit exceeded. " +
                $"Requested {_subscriptions.Count + newTokens.Count}, " +
                $"limit {_options.MaxSubscriptions}.");
        }

        foreach (var token in newTokens)
        {
            _subscriptions.Add(token);
        }

        await SendSubscriptionFrameAsync(
            eventName,
            tokenList,
            cancellationToken);
    }

    private async Task UnsubscribeAsync(
        string eventName,
        IEnumerable<WsToken> tokens,
        CancellationToken cancellationToken)
    {
        var tokenList = tokens
            .Distinct()
            .ToList();

        foreach (var token in tokenList)
        {
            _subscriptions.Remove(token);
            _symbols.Remove(token.InputToken);
        }

        if (tokenList.Count == 0)
        {
            return;
        }

        var frame = JsonSerializer.Serialize(
            new
            {
                @event = eventName,
                inputtoken = string.Join(",", tokenList.Select(x => x.InputToken))
            });

        await SendAsync(frame, cancellationToken);
    }

    private async Task SendSubscriptionFrameAsync(
        string eventName,
        IEnumerable<WsToken> tokens,
        CancellationToken cancellationToken)
    {
        var inputTokens = tokens
            .Select(x => x.InputToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var frame = JsonSerializer.Serialize(
            new
            {
                @event = eventName,
                inputtoken = string.Join(",", inputTokens),
                json = true,
                ack_symbol = true
            });

        await SendAsync(frame, cancellationToken);
    }

    private async Task SendAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (_webSocket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException(
                "Kotak Neo market WebSocket is not connected.");
        }

        var bytes = Encoding.UTF8.GetBytes(text);

        await _webSocket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private void HandleText(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            if (!root.TryGetProperty(
                    "message_code",
                    out var code) ||
                code.GetInt32() != 1109 ||
                !root.TryGetProperty(
                    "trading_symbols",
                    out var symbols))
            {
                return;
            }

            foreach (var symbol in symbols.EnumerateObject())
            {
                _symbols[symbol.Name] =
                    symbol.Value.GetString() ?? "";
            }
        }
        catch
        {
            // Ignore non-JSON informational frames.
        }
    }

    private async Task HandleBinaryAsync(byte[] frame)
    {
        foreach (var packet in SplitPackets(frame))
        {
            if (packet.Length < 9)
            {
                continue;
            }

            var messageCode = BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2));
            var exchange = Exchange(packet[4]);
            var level = packet[5];
            var auction = packet[6] > 0;
            var divider = _dividers.TryGetValue(
                    packet[4],
                    out var configuredDivider) &&
                configuredDivider > 0
                ? configuredDivider
                : 100;

            if (messageCode is 6511 or 6521)
            {
                if (MarketStatusReceived is not null)
                {
                    await MarketStatusReceived(
                        new MarketStatusTick(
                            exchange,
                            messageCode == 6511 ? 1 : 2,
                            messageCode == 6511 ? "open" : "close"));
                }

                continue;
            }

            if (messageCode == 7207)
            {
                var index = DecodeIndex(
                    packet,
                    exchange,
                    divider);

                if (index is not null && IndexReceived is not null)
                {
                    await IndexReceived(index);
                }

                continue;
            }

            if (messageCode == 105)
            {
                if (packet.Length >= 16)
                {
                    var statusCode = BinaryPrimitives.ReadUInt16LittleEndian(
                        packet.AsSpan(9));
                    var status = Encoding.UTF8.GetString(
                        packet,
                        11,
                        5).TrimEnd('\0', ' ');

                    if (MarketStatusReceived is not null)
                    {
                        await MarketStatusReceived(
                            new MarketStatusTick(
                                exchange,
                                statusCode,
                                status));
                    }
                }

                continue;
            }

            if (level != 1 &&
                level is not (2 or 4 or 8 or 16))
            {
                continue;
            }

            var tick = DecodePicture(
                packet,
                exchange,
                divider,
                level,
                auction);

            if (tick is null)
            {
                continue;
            }

            var key =
                $"{tick.ExchangeSegment}|{tick.InstrumentToken}";

            if (_symbols.TryGetValue(key, out var symbol))
            {
                tick.TradingSymbol = symbol;
            }

            if (TickReceived is not null)
            {
                await TickReceived(tick);
            }
        }
    }

    private static MarketTick? DecodePicture(
        byte[] packet,
        string exchange,
        int divider,
        int level,
        bool auction)
    {
        if (packet.Length < 144)
        {
            return null;
        }

        var data = packet.AsSpan(9);

        var token = BitConverter.ToUInt32(data[0..4]);
        var totalBuyQuantity = BitConverter.ToInt64(data[4..12]);
        var totalSellQuantity = BitConverter.ToInt64(data[12..20]);
        var volume = BitConverter.ToInt64(data[20..28]);
        var lastTradeTime = BitConverter.ToInt64(data[28..36]);
        var lastUpdateTime = BitConverter.ToInt64(data[36..44]);

        var open = BitConverter.ToInt32(data[44..48]);
        var close = BitConverter.ToInt32(data[48..52]);
        var high = BitConverter.ToInt32(data[52..56]);
        var low = BitConverter.ToInt32(data[56..60]);
        var lastTradedPrice = BitConverter.ToInt64(data[60..68]);
        var lastTradeQuantity = BitConverter.ToInt64(data[68..76]);
        var averagePrice = BitConverter.ToInt32(data[76..80]);
        var buyCount = BitConverter.ToInt16(data[88..90]);
        var sellCount = BitConverter.ToInt32(data[90..94]);
        var netChangePercent = BitConverter.ToDouble(data[94..102]);
        var openInterest = BitConverter.ToInt32(data[102..106]);
        var tradedValue = BitConverter.ToInt32(data[106..110]);
        var netChange = BitConverter.ToInt32(data[110..114]);
        var upperCircuit = BitConverter.ToInt32(data[114..118]);
        var lowerCircuit = BitConverter.ToInt32(data[118..122]);

        var buyLevels = new List<DepthLevel>();
        var sellLevels = new List<DepthLevel>();
        var offset = 144;
        var buyLevelCount = level == 4
            ? 1
            : Math.Max(0, (int)buyCount);
        var sellLevelCount = Math.Max(0, sellCount);

        for (var index = 0;
             index < buyLevelCount + sellLevelCount;
             index++)
        {
            if (offset + 16 > packet.Length)
            {
                break;
            }

            var quantity = BitConverter.ToInt64(
                packet,
                offset);
            var price = BitConverter.ToInt32(
                packet,
                offset + 8);
            var orders = BitConverter.ToInt32(
                packet,
                offset + 12);

            var depth = new DepthLevel(
                quantity,
                price / (decimal)divider,
                orders);

            if (index < buyLevelCount)
            {
                buyLevels.Add(depth);
            }
            else
            {
                sellLevels.Add(depth);
            }

            offset += 16;
        }

        return new MarketTick
        {
            ExchangeSegment = exchange,
            InstrumentToken = token.ToString(),
            Level = level,
            LastTradedPrice = lastTradedPrice / (decimal)divider,
            Open = open / (decimal)divider,
            High = high / (decimal)divider,
            Low = low / (decimal)divider,
            Close = close / (decimal)divider,
            AverageTradePrice = averagePrice / (decimal)divider,
            LastTradeTime = lastTradeTime,
            LastUpdateTime = lastUpdateTime,
            LastTradeQuantity = lastTradeQuantity,
            TotalBuyQuantity = totalBuyQuantity,
            TotalSellQuantity = totalSellQuantity,
            VolumeTradedToday = volume,
            OpenInterest = openInterest,
            NetChange = netChange / (decimal)divider,
            NetChangePercent = (decimal)netChangePercent / 100m,
            UpperCircuitLimit = upperCircuit / (decimal)divider,
            LowerCircuitLimit = lowerCircuit / (decimal)divider,
            TotalTradedValue = tradedValue / (decimal)divider,
            Auction = auction,
            Buy = buyLevels,
            Sell = sellLevels
        };
    }

    private static IndexTick? DecodeIndex(
        byte[] packet,
        string exchange,
        int divider)
    {
        if (packet.Length < 70)
        {
            return null;
        }

        var data = packet.AsSpan(9);
        var token = BitConverter.ToUInt32(data[0..4]);
        var open = BitConverter.ToInt32(data[4..8]);
        var close = BitConverter.ToInt32(data[8..12]);
        var high = BitConverter.ToInt32(data[12..16]);
        var low = BitConverter.ToInt32(data[16..20]);
        var indexValue = BitConverter.ToInt32(data[20..24]);
        var tradeTime = BitConverter.ToInt64(data[24..32]);
        var netChangePercent = BitConverter.ToDouble(data[48..56]);
        var name = Encoding.UTF8.GetString(
                data[61..Math.Min(82, data.Length)])
            .TrimEnd('\0', ' ');

        return new IndexTick(
            exchange,
            token.ToString(),
            name,
            indexValue / (decimal)divider,
            open / (decimal)divider,
            high / (decimal)divider,
            low / (decimal)divider,
            close / (decimal)divider,
            (indexValue - close) / (decimal)divider,
            (decimal)netChangePercent / 100m,
            tradeTime);
    }

    private static IEnumerable<byte[]> SplitPackets(byte[] frame)
    {
        var offset = 0;

        while (offset + 2 <= frame.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                frame.AsSpan(offset, 2));

            if (length < 9 || offset + length > frame.Length)
            {
                yield break;
            }

            var packet = new byte[length];
            Buffer.BlockCopy(
                frame,
                offset,
                packet,
                0,
                length);

            yield return packet;
            offset += length;
        }
    }

    private static string Exchange(byte exchangeId)
    {
        return exchangeId switch
        {
            0 => "none",
            1 => "nse_cm",
            2 => "nse_fo",
            3 => "cde_fo",
            4 => "nse_com",
            5 => "bse_cm",
            6 => "bse_fo",
            7 => "bse_cd",
            8 => "bse_co",
            9 => "mcx_fo",
            10 => "ncd_co",
            _ => exchangeId.ToString()
        };
    }

    private async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;

        do
        {
            result = await _webSocket!.ReceiveAsync(
                buffer,
                cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException(
                    "Kotak Neo closed the market feed.");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (
            result.MessageType,
            stream.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _cts?.Cancel();

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask;
                }
                catch
                {
                    // Ignore errors during shutdown.
                }
            }

            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "closing",
                    CancellationToken.None);
            }
        }
        catch
        {
            // Ignore shutdown errors.
        }
        finally
        {
            _webSocket?.Dispose();
            _cts?.Dispose();
            _socketGate.Dispose();
        }
    }
}
