using NiftyPremiumTrader.Api.Models;
using Npgsql;

namespace NiftyPremiumTrader.Api.Data;

public sealed class BrokerActivityRepository
{
    private readonly string _connectionString;

    public BrokerActivityRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Trading")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Trading is not configured.");
    }

    public async Task SaveManualOrderAsync(
        ManualBrokerOrderRecord order,
        CancellationToken cancellationToken)
    {
        const string sql = """
        INSERT INTO trading.manual_broker_orders
        (
            local_order_id,
            broker_order_id,
            session_date,
            trading_symbol,
            instrument_token,
            strike,
            option_type,
            side,
            quantity,
            lots,
            lot_size,
            order_type,
            limit_price,
            purpose,
            status,
            average_price,
            updated_at
        )
        VALUES
        (
            @local_order_id,
            @broker_order_id,
            @session_date,
            @trading_symbol,
            @instrument_token,
            @strike,
            @option_type,
            @side,
            @quantity,
            @lots,
            @lot_size,
            @order_type,
            @limit_price,
            @purpose,
            @status,
            @average_price,
            NOW()
        )
        ON CONFLICT (broker_order_id) DO UPDATE SET
            status = EXCLUDED.status,
            average_price = EXCLUDED.average_price,
            updated_at = NOW();
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("local_order_id", order.LocalOrderId);
        command.Parameters.AddWithValue("broker_order_id", order.BrokerOrderId);
        command.Parameters.AddWithValue("session_date", order.SessionDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("trading_symbol", order.TradingSymbol);
        command.Parameters.AddWithValue("instrument_token", order.InstrumentToken);
        command.Parameters.AddWithValue("strike", (object?)order.Strike ?? DBNull.Value);
        command.Parameters.AddWithValue("option_type", order.OptionType);
        command.Parameters.AddWithValue("side", order.Side);
        command.Parameters.AddWithValue("quantity", order.Quantity);
        command.Parameters.AddWithValue("lots", order.Lots);
        command.Parameters.AddWithValue("lot_size", order.LotSize);
        command.Parameters.AddWithValue("order_type", order.OrderType);
        command.Parameters.AddWithValue("limit_price", (object?)order.LimitPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("purpose", order.Purpose);
        command.Parameters.AddWithValue("status", order.Status);
        command.Parameters.AddWithValue("average_price", (object?)order.AveragePrice ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<HashSet<string>> GetManualUiBrokerOrderIdsAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT broker_order_id
        FROM trading.manual_broker_orders
        WHERE session_date = @date;
        """;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                result.Add(reader.GetString(0));
            }
        }

        return result;
    }

    public async Task<HashSet<string>> GetSystemBrokerOrderIdsAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT order_id
        FROM
        (
            SELECT sold_entry_order_id AS order_id
            FROM trading.strategy_trades
            WHERE session_date = @date

            UNION ALL

            SELECT hedge_entry_order_id
            FROM trading.strategy_trades
            WHERE session_date = @date

            UNION ALL

            SELECT sold_exit_order_id
            FROM trading.strategy_trades
            WHERE session_date = @date

            UNION ALL

            SELECT hedge_exit_order_id
            FROM trading.strategy_trades
            WHERE session_date = @date
        ) q
        WHERE order_id IS NOT NULL
          AND BTRIM(order_id) <> '';
        """;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}
