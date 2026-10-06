using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NiftyPremiumTrader.Api.Models;
using Npgsql;
using NpgsqlTypes;

namespace NiftyPremiumTrader.Api.Data;

public sealed partial class CandleRepository
{
    private string _legacySchema = "trading";
    private bool _legacyTimesAreUtc = true;

    public void ConfigureLegacySource(string schema, bool timesAreUtc)
    {
        if (!string.IsNullOrWhiteSpace(schema) && !Regex.IsMatch(schema, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException("Strategy:LegacySchema must be a SQL identifier.");
        _legacySchema = schema; _legacyTimesAreUtc = timesAreUtc;
    }

    public async Task<MorningSelection?> GetMorningSelectionAsync(DateOnly date, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT expiry_date, morning_spot, rounded_strike, spot_source, pairs::text
            FROM trading.morning_signal_selections
            WHERE session_date=@date AND rule_version=@version
            """, connection);
        cmd.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("version", MorningSelection.RuleVersion);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new MorningSelection
        {
            SessionDate = date, ExpiryDate = DateOnly.FromDateTime(reader.GetDateTime(0)),
            MorningSpot = reader.GetDecimal(1), RoundedStrike = reader.GetDecimal(2),
            SpotSource = reader.GetString(3),
            Pairs = JsonSerializer.Deserialize<List<SignalPairDefinition>>(reader.GetString(4)) ?? []
        };
    }

    public async Task<MorningSelection> SaveMorningSelectionAsync(MorningSelection selection, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO trading.morning_signal_selections
                (session_date,rule_version,expiry_date,morning_spot,rounded_strike,spot_source,pairs)
            VALUES (@date,@version,@expiry,@spot,@rounded,@source,@pairs)
            ON CONFLICT(session_date,rule_version) DO NOTHING
            """, connection);
        cmd.Parameters.AddWithValue("date", selection.SessionDate.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("version", MorningSelection.RuleVersion);
        cmd.Parameters.AddWithValue("expiry", selection.ExpiryDate.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("spot", selection.MorningSpot);
        cmd.Parameters.AddWithValue("rounded", selection.RoundedStrike);
        cmd.Parameters.AddWithValue("source", selection.SpotSource);
        cmd.Parameters.Add("pairs", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(selection.Pairs);
        await cmd.ExecuteNonQueryAsync(ct);
        return await GetMorningSelectionAsync(selection.SessionDate, ct)
            ?? throw new InvalidOperationException("Could not persist morning selection.");
    }

    public async Task<DateOnly?> ResolveOpstraExpiryAsync(DateOnly date, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        string? table = await FindOpstraTableAsync(connection, date.Year, ct);
        if (table == null) return null;
        // Same earliest-time/nearest-expiry order and special exclusion as GetStrike1.
        await using var cmd = new NpgsqlCommand($"""
            SELECT expirydate::date FROM {table}
            WHERE tradedateonly::date=@date AND expirydate::date>=@date
              AND (@exclude=FALSE OR expirydate::date<>DATE '2025-09-25')
            ORDER BY tradetimeonly::time, expirydate::date LIMIT 1
            """, connection);
        cmd.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("exclude", date > new DateOnly(2025, 9, 2));
        object? value = await cmd.ExecuteScalarAsync(ct);
        return value is DateTime time ? DateOnly.FromDateTime(time) : null;
    }

    public async Task<decimal?> GetMorningSpotAsync(DateOnly date, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        string? table = await FindOpstraTableAsync(connection, date.Year, ct);
        if (table == null) return null;
        await using var cmd = new NpgsqlCommand($"""
            SELECT spotprise FROM {table}
            WHERE tradedateonly::date=@date
              AND (@exclude=FALSE OR expirydate::date<>DATE '2025-09-25')
            ORDER BY tradetimeonly::time, expirydate::date LIMIT 1
            """, connection);
        cmd.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("exclude", date > new DateOnly(2025,9,2));
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result == null || result == DBNull.Value ? null : Convert.ToDecimal(result, CultureInfo.InvariantCulture);
    }

    public async Task PopulateLegacyDeltasAsync(List<OptionCandle> candles, CancellationToken ct)
    {
        if (candles.Count == 0) return;
        DateOnly date = candles[0].SessionDate;
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        string? table = await FindOpstraTableAsync(connection, date.Year, ct);
        if (table == null) return;
        await using var cmd = new NpgsqlCommand($"""
            SELECT tradetimeonly::time, to_jsonb(t)->>'strikes',
                   to_jsonb(t)->>'calldelta', to_jsonb(t)->>'putdelta'
            FROM {table} t WHERE tradedateonly::date=@date AND expirydate::date=@expiry
            ORDER BY tradetimeonly::time
            """, connection);
        cmd.Parameters.AddWithValue("date", date.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("expiry", candles[0].ExpiryDate!.Value.ToDateTime(TimeOnly.MinValue));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var map = candles.ToDictionary(c => (TimeOnly.FromDateTime(
            (_legacyTimesAreUtc ? c.CandleTime : Services.MarketClock.ToIndia(c.CandleTime)).DateTime), c.Strike!.Value));
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(1) || !decimal.TryParse(reader.GetString(1), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal strike)) continue;
            if (!map.TryGetValue((TimeOnly.FromTimeSpan(reader.GetTimeSpan(0)), strike), out var candle)) continue;
            int ordinal = candle.OptionType == "CE" ? 2 : 3;
            if (!reader.IsDBNull(ordinal) && decimal.TryParse(reader.GetString(ordinal), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal delta)
                && Math.Abs(delta) > 0m && Math.Abs(delta) <= 1m)
                candle.Delta = candle.OptionType == "PE" ? -Math.Abs(delta) : Math.Abs(delta);
        }
    }

    private async Task<string?> FindOpstraTableAsync(NpgsqlConnection connection, int year, CancellationToken ct)
    {
        // Only a validated schema and DateOnly.Year are interpolated. All search values are parameters.
        string name = $"opstraoptiondata{year}";
        string[] names = string.IsNullOrEmpty(_legacySchema)
            ? [name, $"trading.{name}", $"public.{name}"]
            : [$"{_legacySchema}.{name}", name, $"public.{name}"];
        foreach (string candidate in names.Distinct())
        {
            await using var cmd = new NpgsqlCommand("SELECT to_regclass(@name)::text", connection);
            cmd.Parameters.AddWithValue("name", candidate);
            if (await cmd.ExecuteScalarAsync(ct) is string)
                return string.Join(".", candidate.Split('.').Select(x => "\"" + x + "\""));
        }
        return null;
    }
}
