using NiftyPremiumTrader.Api.Models;
using Npgsql;

namespace NiftyPremiumTrader.Api.Data;

public sealed class TradeRepository
{
    private readonly string _connectionString;

    public TradeRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Trading")
            ?? throw new InvalidOperationException("ConnectionStrings:Trading is required.");
    }

    public async Task SaveLiveTradeAsync(
        StrategyTrade trade,
        CancellationToken cancellationToken)
    {
        const string sql = """
        INSERT INTO trading.strategy_trades
        (
            trade_id,
            mode,
            session_date,
            direction,
            signal_time,
            sold_option_type,
            sold_strike,
            sold_entry_time,
            sold_entry_price,
            hedge_distance,
            hedge_strike,
            hedge_entry_time,
            hedge_entry_price,
            hedge_trigger_pnl,
            exit_time,
            sold_exit_price,
            hedge_exit_price,
            quantity,
            lots,
            lot_size,
            gross_pnl,
            costs,
            net_pnl,
            best_mark_pnl,
            worst_mark_pnl,
            exit_reason,
            sold_entry_order_id,
            hedge_entry_order_id,
            sold_exit_order_id,
            hedge_exit_order_id,
            broker_execution,
            risk_setup,
            status,
            updated_at
        )
        VALUES
        (
            @trade_id,
            'LIVE',
            @session_date,
            @direction,
            @signal_time,
            @sold_option_type,
            @sold_strike,
            @sold_entry_time,
            @sold_entry_price,
            @hedge_distance,
            @hedge_strike,
            @hedge_entry_time,
            @hedge_entry_price,
            @hedge_trigger_pnl,
            @exit_time,
            @sold_exit_price,
            @hedge_exit_price,
            @quantity,
            @lots,
            @lot_size,
            @gross_pnl,
            @costs,
            @net_pnl,
            @best_mark_pnl,
            @worst_mark_pnl,
            @exit_reason,
            @sold_entry_order_id,
            @hedge_entry_order_id,
            @sold_exit_order_id,
            @hedge_exit_order_id,
            @broker_execution,
            @risk_setup,
            @status,
            NOW()
        )
        ON CONFLICT(trade_id)
        DO UPDATE SET
            hedge_strike = EXCLUDED.hedge_strike,
            hedge_entry_time = EXCLUDED.hedge_entry_time,
            hedge_entry_price = EXCLUDED.hedge_entry_price,
            hedge_trigger_pnl = EXCLUDED.hedge_trigger_pnl,
            exit_time = EXCLUDED.exit_time,
            sold_exit_price = EXCLUDED.sold_exit_price,
            hedge_exit_price = EXCLUDED.hedge_exit_price,
            quantity = EXCLUDED.quantity,
            lots = EXCLUDED.lots,
            lot_size = EXCLUDED.lot_size,
            gross_pnl = EXCLUDED.gross_pnl,
            costs = EXCLUDED.costs,
            net_pnl = EXCLUDED.net_pnl,
            best_mark_pnl = EXCLUDED.best_mark_pnl,
            worst_mark_pnl = EXCLUDED.worst_mark_pnl,
            exit_reason = EXCLUDED.exit_reason,
            sold_entry_order_id = EXCLUDED.sold_entry_order_id,
            hedge_entry_order_id = EXCLUDED.hedge_entry_order_id,
            sold_exit_order_id = EXCLUDED.sold_exit_order_id,
            hedge_exit_order_id = EXCLUDED.hedge_exit_order_id,
            broker_execution = EXCLUDED.broker_execution,
            risk_setup = COALESCE(trading.strategy_trades.risk_setup, EXCLUDED.risk_setup),
            status = EXCLUDED.status,
            updated_at = NOW();
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("trade_id", trade.TradeId);
        command.Parameters.AddWithValue("session_date", trade.SessionDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("direction", trade.Direction.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("signal_time", trade.SignalTime);
        command.Parameters.AddWithValue("sold_option_type", trade.SoldOptionType);
        command.Parameters.AddWithValue("sold_strike", trade.SoldStrike);
        command.Parameters.AddWithValue("sold_entry_time", trade.EntryTime);
        command.Parameters.AddWithValue("sold_entry_price", trade.EntryPrice);
        command.Parameters.AddWithValue("hedge_distance", trade.HedgeDistance);
        command.Parameters.AddWithValue("hedge_strike", ToDbValue(trade.HedgeStrike));
        command.Parameters.AddWithValue("hedge_entry_time", ToDbValue(trade.HedgeEntryTime));
        command.Parameters.AddWithValue("hedge_entry_price", ToDbValue(trade.HedgeEntryPrice));
        command.Parameters.AddWithValue("hedge_trigger_pnl", ToDbValue(trade.HedgeTriggerPnl));
        command.Parameters.AddWithValue("exit_time", ToDbValue(trade.ExitTime));
        command.Parameters.AddWithValue("sold_exit_price", ToDbValue(trade.SoldExitPrice));
        command.Parameters.AddWithValue("hedge_exit_price", ToDbValue(trade.HedgeExitPrice));
        command.Parameters.AddWithValue("quantity", trade.Quantity);
        command.Parameters.AddWithValue("lots", trade.Lots);
        command.Parameters.AddWithValue("lot_size", trade.LotSize);
        command.Parameters.AddWithValue("gross_pnl", trade.GrossPnl);
        command.Parameters.AddWithValue("costs", trade.Costs);
        command.Parameters.AddWithValue("net_pnl", trade.NetPnl);
        command.Parameters.AddWithValue("best_mark_pnl", trade.BestMarkPnl);
        command.Parameters.AddWithValue("worst_mark_pnl", trade.WorstMarkPnl);
        command.Parameters.AddWithValue("exit_reason", trade.ExitReason);
        command.Parameters.AddWithValue("sold_entry_order_id", trade.SoldEntryOrderId);
        command.Parameters.AddWithValue("hedge_entry_order_id", trade.HedgeEntryOrderId);
        command.Parameters.AddWithValue("sold_exit_order_id", trade.SoldExitOrderId);
        command.Parameters.AddWithValue("hedge_exit_order_id", trade.HedgeExitOrderId);
        command.Parameters.AddWithValue("broker_execution", trade.BrokerExecution);
        command.Parameters.AddWithValue("risk_setup", NpgsqlTypes.NpgsqlDbType.Jsonb, trade.RiskSetup == null ? (object)DBNull.Value : System.Text.Json.JsonSerializer.Serialize(trade.RiskSetup));
        command.Parameters.AddWithValue("status", trade.IsOpen ? "OPEN" : "CLOSED");

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<StrategyTrade>> GetTradesAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT
            trade_id,
            session_date,
            direction,
            signal_time,
            sold_option_type,
            sold_strike,
            sold_entry_time,
            sold_entry_price,
            hedge_distance,
            hedge_strike,
            hedge_entry_time,
            hedge_entry_price,
            hedge_trigger_pnl,
            exit_time,
            sold_exit_price,
            hedge_exit_price,
            quantity,
            lots,
            lot_size,
            gross_pnl,
            costs,
            net_pnl,
            best_mark_pnl,
            worst_mark_pnl,
            exit_reason,
            sold_entry_order_id,
            hedge_entry_order_id,
            sold_exit_order_id,
            hedge_exit_order_id,
            broker_execution,
            risk_setup
        FROM trading.strategy_trades
        WHERE session_date = @date
        ORDER BY sold_entry_time;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var trades = new List<StrategyTrade>();

        while (await reader.ReadAsync(cancellationToken))
        {
            string direction = reader.GetString(2);

            var trade = new StrategyTrade
            {
                RiskSetup = reader.IsDBNull(reader.GetOrdinal("risk_setup")) ? null : System.Text.Json.JsonSerializer.Deserialize<NiftyPremiumTrader.Api.Services.RiskSetup>(reader.GetString(reader.GetOrdinal("risk_setup"))),
                TradeId = reader.GetGuid(0),
                SessionDate = DateOnly.FromDateTime(reader.GetDateTime(1)),
                Direction = Enum.Parse<SignalDirection>(direction, true),
                SignalTime = ReadUtc(reader, 3),
                SoldOptionType = reader.GetString(4),
                SoldStrike = reader.GetDecimal(5),
                EntryTime = ReadUtc(reader, 6),
                EntryPrice = reader.GetDecimal(7),
                HedgeDistance = reader.GetInt32(8),
                HedgeStrike = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                HedgeEntryTime = reader.IsDBNull(10) ? null : ReadUtc(reader, 10),
                HedgeEntryPrice = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                HedgeTriggerPnl = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                ExitTime = reader.IsDBNull(13) ? null : ReadUtc(reader, 13),
                SoldExitPrice = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                HedgeExitPrice = reader.IsDBNull(15) ? null : reader.GetDecimal(15),
                Quantity = reader.GetInt32(16),
                Lots = reader.GetInt32(17),
                LotSize = reader.GetInt32(18),
                GrossPnl = reader.GetDecimal(19),
                Costs = reader.GetDecimal(20),
                NetPnl = reader.GetDecimal(21),
                BestMarkPnl = reader.GetDecimal(22),
                WorstMarkPnl = reader.GetDecimal(23),
                ExitReason = reader.IsDBNull(24) ? string.Empty : reader.GetString(24),
                SoldEntryOrderId = reader.IsDBNull(25) ? string.Empty : reader.GetString(25),
                HedgeEntryOrderId = reader.IsDBNull(26) ? string.Empty : reader.GetString(26),
                SoldExitOrderId = reader.IsDBNull(27) ? string.Empty : reader.GetString(27),
                HedgeExitOrderId = reader.IsDBNull(28) ? string.Empty : reader.GetString(28),
                BrokerExecution = !reader.IsDBNull(29) && reader.GetBoolean(29)
            };

            trades.Add(trade);
        }

        return trades;
    }

    public async Task<List<StrategyTrade>> GetOpenTradesAsync(
        DateOnly date,
        bool brokerExecution,
        CancellationToken cancellationToken)
    {
        List<StrategyTrade> trades = await GetTradesAsync(
            date,
            cancellationToken);

        return trades
            .Where(x =>
            {
                if (!x.IsOpen)
                {
                    return false;
                }

                bool looksLikeBrokerTrade =
                    x.BrokerExecution ||
                    !string.IsNullOrWhiteSpace(x.SoldEntryOrderId);

                return brokerExecution
                    ? looksLikeBrokerTrade
                    : !looksLikeBrokerTrade;
            })
            .OrderBy(x => x.EntryTime)
            .ToList();
    }

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal)
    {
        DateTime value = reader.GetDateTime(ordinal);
        DateTime utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    private static object ToDbValue<T>(T? value)
        where T : struct
    {
        if (value.HasValue)
        {
            return value.Value;
        }

        return DBNull.Value;
    }
}
