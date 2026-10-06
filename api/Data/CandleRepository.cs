using NiftyPremiumTrader.Api.Models;
using Npgsql;

namespace NiftyPremiumTrader.Api.Data;

public sealed partial class CandleRepository : ICandleStore
{
    private readonly string _connectionString;

    public CandleRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Trading")
            ?? throw new InvalidOperationException("ConnectionStrings:Trading is required.");
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        string schemaPath = Path.Combine(AppContext.BaseDirectory, "Sql", "schema.sql");

        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException("Sql/schema.sql was not copied to the API output folder.", schemaPath);
        }

        string sql = await File.ReadAllTextAsync(schemaPath, cancellationToken);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetIciciSessionAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT icicisession FROM sessionmaster LIMIT 1";

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(sql, connection);
            object? result = await command.ExecuteScalarAsync(cancellationToken);

            return result?.ToString();
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> IsHistoricalDayLoadedAsync(
        DateOnly sessionDate,
        string loadKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT EXISTS
        (
            SELECT 1
            FROM trading.candle_day_loads
            WHERE session_date = @session_date
              AND load_key = @load_key
        );
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(
            "session_date",
            sessionDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("load_key", loadKey);

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool value && value;
    }

    public async Task MarkHistoricalDayLoadedAsync(
        DateOnly sessionDate,
        string loadKey,
        int candleCount,
        CancellationToken cancellationToken)
    {
        const string sql = """
        INSERT INTO trading.candle_day_loads
        (
            session_date,
            load_key,
            candle_count,
            loaded_at
        )
        VALUES
        (
            @session_date,
            @load_key,
            @candle_count,
            NOW()
        )
        ON CONFLICT(session_date, load_key)
        DO UPDATE SET
            candle_count = EXCLUDED.candle_count,
            loaded_at = NOW();
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(
            "session_date",
            sessionDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("load_key", loadKey);
        command.Parameters.AddWithValue("candle_count", candleCount);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddExpiryAsync(DateOnly expiryDate, CancellationToken cancellationToken)
    {
        const string sql = """
        INSERT INTO trading.option_expiries(expiry_date)
        VALUES(@expiry_date)
        ON CONFLICT(expiry_date) DO NOTHING;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("expiry_date", expiryDate.ToDateTime(TimeOnly.MinValue));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DateOnly?> ResolveExpiryAsync(
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        DateOnly? fromCandles = await ResolveExpiryFromCandlesAsync(
            sessionDate,
            cancellationToken);

        if (fromCandles.HasValue)
        {
            return fromCandles.Value;
        }

        // Your existing ShareMarket database already keeps weekly expiries in
        // niftyexpiry. Prefer that historical source before the small local
        // fallback table so an old backtest date cannot accidentally use a
        // single recently-entered expiry.
        DateOnly? fromLegacy = await ResolveLegacyExpiryAsync(
            sessionDate,
            cancellationToken);

        if (fromLegacy.HasValue)
        {
            await AddExpiryAsync(
                fromLegacy.Value,
                cancellationToken);

            return fromLegacy.Value;
        }

        return await ResolveExpiryFromLocalTableAsync(
            sessionDate,
            cancellationToken);
    }

    public async Task UpsertCandlesAsync(
        IReadOnlyCollection<OptionCandle> candles,
        CancellationToken cancellationToken)
    {
        if (candles.Count == 0)
        {
            return;
        }

        const string sql = """
        INSERT INTO trading.premium_candles_5m
        (
            session_date,
            candle_time,
            contract_key,
            stock_code,
            exchange_code,
            product_type,
            expiry_date,
            strike,
            option_type,
            open_price,
            high_price,
            low_price,
            close_price,
            volume,
            open_interest,
            data_source,
            is_complete,
            delta,
            updated_at
        )
        VALUES
        (
            @session_date,
            @candle_time,
            @contract_key,
            @stock_code,
            @exchange_code,
            @product_type,
            @expiry_date,
            @strike,
            @option_type,
            @open_price,
            @high_price,
            @low_price,
            @close_price,
            @volume,
            @open_interest,
            @data_source,
            @is_complete,
            @delta,
            NOW()
        )
        ON CONFLICT(session_date, contract_key, candle_time)
        DO UPDATE SET
            open_price = EXCLUDED.open_price,
            high_price = EXCLUDED.high_price,
            low_price = EXCLUDED.low_price,
            close_price = EXCLUDED.close_price,
            volume = EXCLUDED.volume,
            open_interest = EXCLUDED.open_interest,
            data_source = EXCLUDED.data_source,
            is_complete = EXCLUDED.is_complete,
            delta = EXCLUDED.delta,
            updated_at = NOW()
        WHERE EXCLUDED.is_complete OR NOT trading.premium_candles_5m.is_complete;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (OptionCandle candle in candles)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);

            command.Parameters.AddWithValue(
                "session_date",
                candle.SessionDate.ToDateTime(TimeOnly.MinValue));

            command.Parameters.AddWithValue("candle_time", candle.CandleTime);
            command.Parameters.AddWithValue("contract_key", BuildContractKey(candle));
            command.Parameters.AddWithValue("stock_code", candle.StockCode);
            command.Parameters.AddWithValue("exchange_code", candle.ExchangeCode);
            command.Parameters.AddWithValue("product_type", candle.ProductType);
            command.Parameters.AddWithValue("expiry_date", ToDbValue(candle.ExpiryDate));
            command.Parameters.AddWithValue("strike", ToDbValue(candle.Strike));
            command.Parameters.AddWithValue("option_type", ToDbValue(candle.OptionType));
            command.Parameters.AddWithValue("open_price", candle.Open);
            command.Parameters.AddWithValue("high_price", candle.High);
            command.Parameters.AddWithValue("low_price", candle.Low);
            command.Parameters.AddWithValue("close_price", candle.Close);
            command.Parameters.AddWithValue("volume", candle.Volume);
            command.Parameters.AddWithValue("open_interest", candle.OpenInterest);
            command.Parameters.AddWithValue("data_source", candle.DataSource);
            command.Parameters.AddWithValue("is_complete", candle.IsComplete);
            command.Parameters.Add("delta", NpgsqlTypes.NpgsqlDbType.Numeric).Value = (object?)candle.Delta ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<List<OptionCandle>> GetOptionCandlesAsync(
        DateOnly sessionDate,
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT
            session_date,
            candle_time,
            stock_code,
            exchange_code,
            product_type,
            expiry_date,
            strike,
            option_type,
            open_price,
            high_price,
            low_price,
            close_price,
            volume,
            open_interest,
            data_source,
            is_complete,
            delta
        FROM trading.premium_candles_5m
        WHERE session_date = @session_date
          AND expiry_date = @expiry_date
          AND strike = @strike
          AND option_type = @option_type
        ORDER BY candle_time;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("session_date", sessionDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("expiry_date", expiryDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("strike", strike);
        command.Parameters.AddWithValue("option_type", optionType.ToUpperInvariant());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var result = new List<OptionCandle>();

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadCandle(reader));
        }

        return result;
    }

    public async Task<List<OptionCandle>> GetIndexCandlesAsync(
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT
            session_date,
            candle_time,
            stock_code,
            exchange_code,
            product_type,
            expiry_date,
            strike,
            option_type,
            open_price,
            high_price,
            low_price,
            close_price,
            volume,
            open_interest,
            data_source,
            is_complete,
            delta
        FROM trading.premium_candles_5m
        WHERE session_date = @session_date
          AND product_type = 'cash'
        ORDER BY candle_time;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("session_date", sessionDate.ToDateTime(TimeOnly.MinValue));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var result = new List<OptionCandle>();

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadCandle(reader));
        }

        return result;
    }

    public async Task<OptionCandle?> GetOptionCandleAsync(
        DateOnly sessionDate,
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        DateTimeOffset candleTime,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT
            session_date,
            candle_time,
            stock_code,
            exchange_code,
            product_type,
            expiry_date,
            strike,
            option_type,
            open_price,
            high_price,
            low_price,
            close_price,
            volume,
            open_interest,
            data_source,
            is_complete,
            delta
        FROM trading.premium_candles_5m
        WHERE session_date = @session_date
          AND expiry_date = @expiry_date
          AND strike = @strike
          AND option_type = @option_type
          AND candle_time = @candle_time
        LIMIT 1;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("session_date", sessionDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("expiry_date", expiryDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("strike", strike);
        command.Parameters.AddWithValue("option_type", optionType.ToUpperInvariant());
        command.Parameters.AddWithValue("candle_time", candleTime);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadCandle(reader);
    }

    public async Task<OptionCandle?> GetCandleAtOrBeforeAsync(
        DateOnly sessionDate,
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        DateTimeOffset time,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT
            session_date,
            candle_time,
            stock_code,
            exchange_code,
            product_type,
            expiry_date,
            strike,
            option_type,
            open_price,
            high_price,
            low_price,
            close_price,
            volume,
            open_interest,
            data_source,
            is_complete,
            delta
        FROM trading.premium_candles_5m
        WHERE session_date = @session_date
          AND expiry_date = @expiry_date
          AND strike = @strike
          AND option_type = @option_type
          AND candle_time <= @time
        ORDER BY candle_time DESC
        LIMIT 1;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("session_date", sessionDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("expiry_date", expiryDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("strike", strike);
        command.Parameters.AddWithValue("option_type", optionType.ToUpperInvariant());
        command.Parameters.AddWithValue("time", time);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadCandle(reader);
    }

    public async Task<DateTimeOffset?> GetLatestOptionCandleTimeAsync(
        DateOnly sessionDate,
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT MAX(candle_time)
        FROM trading.premium_candles_5m
        WHERE session_date = @session_date
          AND expiry_date = @expiry_date
          AND strike = @strike
          AND option_type = @option_type;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("session_date", sessionDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("expiry_date", expiryDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("strike", strike);
        command.Parameters.AddWithValue("option_type", optionType.ToUpperInvariant());

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return ToDateTimeOffset(result);
    }

    public async Task<DateTimeOffset?> GetLatestIndexCandleTimeAsync(
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT MAX(candle_time)
        FROM trading.premium_candles_5m
        WHERE session_date = @session_date
          AND product_type = 'cash';
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("session_date", sessionDate.ToDateTime(TimeOnly.MinValue));

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return ToDateTimeOffset(result);
    }

    public async Task<int> GetUsedApiCallsAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT used_count
        FROM trading.api_daily_budget
        WHERE usage_date = @date;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));

        object? result = await command.ExecuteScalarAsync(cancellationToken);

        if (result == null || result == DBNull.Value)
        {
            return 0;
        }

        return Convert.ToInt32(result);
    }

    public async Task<bool> TryConsumeApiCallsAsync(
        DateOnly date,
        int count,
        int ceiling,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
        INSERT INTO trading.api_daily_budget(usage_date, used_count)
        VALUES(@date, 0)
        ON CONFLICT(usage_date) DO NOTHING;
        """;

        const string updateSql = """
        UPDATE trading.api_daily_budget
        SET used_count = used_count + @count,
            updated_at = NOW()
        WHERE usage_date = @date
          AND used_count + @count <= @ceiling
        RETURNING used_count;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var insertCommand = new NpgsqlCommand(insertSql, connection, transaction))
        {
            insertCommand.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var updateCommand = new NpgsqlCommand(updateSql, connection, transaction);
        updateCommand.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));
        updateCommand.Parameters.AddWithValue("count", count);
        updateCommand.Parameters.AddWithValue("ceiling", ceiling);

        object? result = await updateCommand.ExecuteScalarAsync(cancellationToken);

        if (result == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<DateOnly?> ResolveExpiryFromLocalTableAsync(
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT expiry_date
        FROM trading.option_expiries
        WHERE expiry_date >= @session_date
        ORDER BY expiry_date
        LIMIT 1;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("session_date", sessionDate.ToDateTime(TimeOnly.MinValue));

        object? result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is DateTime dateTime)
        {
            return DateOnly.FromDateTime(dateTime);
        }

        return null;
    }

    private async Task<DateOnly?> ResolveExpiryFromCandlesAsync(
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT MIN(expiry_date)
        FROM trading.premium_candles_5m
        WHERE session_date = @session_date
          AND expiry_date >= @session_date;
        """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("session_date", sessionDate.ToDateTime(TimeOnly.MinValue));

        object? result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is DateTime dateTime)
        {
            return DateOnly.FromDateTime(dateTime);
        }

        return null;
    }

    private async Task<DateOnly?> ResolveLegacyExpiryAsync(
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        const string sql = """
        SELECT expirydate
        FROM niftyexpiry
        WHERE expirydate >= @session_date
        ORDER BY expirydate
        LIMIT 1;
        """;

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("session_date", sessionDate.ToDateTime(TimeOnly.MinValue));

            object? result = await command.ExecuteScalarAsync(cancellationToken);

            if (result is DateTime dateTime)
            {
                return DateOnly.FromDateTime(dateTime);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string BuildContractKey(OptionCandle candle)
    {
        if (candle.ProductType.Equals("cash", StringComparison.OrdinalIgnoreCase))
        {
            return $"{candle.ExchangeCode}|CASH|{candle.StockCode}";
        }

        string expiry = candle.ExpiryDate?.ToString("yyyy-MM-dd") ?? "NOEXP";
        string strike = candle.Strike?.ToString("0.##") ?? "NOSTRIKE";
        string optionType = candle.OptionType ?? "NOOPTION";

        return $"{candle.ExchangeCode}|OPTIONS|{candle.StockCode}|{expiry}|{strike}|{optionType}";
    }

    private static object ToDbValue(DateOnly? value)
    {
        if (!value.HasValue)
        {
            return DBNull.Value;
        }

        return value.Value.ToDateTime(TimeOnly.MinValue);
    }

    private static object ToDbValue(decimal? value)
    {
        if (!value.HasValue)
        {
            return DBNull.Value;
        }

        return value.Value;
    }

    private static object ToDbValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DBNull.Value;
        }

        return value;
    }

    private static DateTimeOffset? ToDateTimeOffset(object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            return null;
        }

        if (value is DateTimeOffset offset)
        {
            return offset.ToUniversalTime();
        }

        if (value is DateTime dateTime)
        {
            DateTime utc = DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
            return new DateTimeOffset(utc);
        }

        return null;
    }

    private static OptionCandle ReadCandle(NpgsqlDataReader reader)
    {
        DateTime sessionDate = reader.GetDateTime(0);
        DateTime candleTimeValue = reader.GetDateTime(1);
        DateTime candleTimeUtc = DateTime.SpecifyKind(candleTimeValue, DateTimeKind.Utc);

        return new OptionCandle
        {
            SessionDate = DateOnly.FromDateTime(sessionDate),
            CandleTime = new DateTimeOffset(candleTimeUtc),
            StockCode = reader.GetString(2),
            ExchangeCode = reader.GetString(3),
            ProductType = reader.GetString(4),
            ExpiryDate = reader.IsDBNull(5)
                ? null
                : DateOnly.FromDateTime(reader.GetDateTime(5)),
            Strike = reader.IsDBNull(6)
                ? null
                : reader.GetDecimal(6),
            OptionType = reader.IsDBNull(7)
                ? null
                : reader.GetString(7),
            Open = reader.GetDecimal(8),
            High = reader.GetDecimal(9),
            Low = reader.GetDecimal(10),
            Close = reader.GetDecimal(11),
            Volume = reader.GetInt64(12),
            OpenInterest = reader.GetInt64(13),
            DataSource = reader.GetString(14),
            IsComplete = reader.GetBoolean(15),
            Delta = reader.IsDBNull(16) ? null : reader.GetDecimal(16)
        };
    }
}
