using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PriceSentinel3000.Core.Journaling;
using PriceSentinel3000.Core.LiveTrading;
using PriceSentinel3000.Core.MarketData;
using PriceSentinel3000.Core.Modes;
using PriceSentinel3000.Core.PaperTrading;
using PriceSentinel3000.Core.Strategy;

namespace PriceSentinel3000.Infrastructure.Storage;

public sealed class SqliteTradingJournal : ITradingJournal
{
    private bool _initialized;

    public SqliteTradingJournal(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    public void Initialize()
    {
        string? directory = Path.GetDirectoryName(DatabasePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using SqliteConnection connection = OpenConnection();
        SqliteJournalSchema.Initialize(connection);
        _initialized = true;
    }

    public JournalSession StartSession(
        Instrument instrument,
        TradingMode mode,
        decimal startingBalance,
        string settingsJson,
        DateTimeOffset startedAtUtc)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(settingsJson);

        var session = new JournalSession(
            Guid.NewGuid(),
            instrument,
            mode,
            startedAtUtc.ToUniversalTime(),
            startingBalance,
            settingsJson);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sessions(
                id, symbol, asset_class, mode, started_at_utc,
                starting_balance, settings_json)
            VALUES(
                $id, $symbol, $asset_class, $mode, $started_at_utc,
                $starting_balance, $settings_json);
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$symbol", instrument.Symbol);
        command.Parameters.AddWithValue("$asset_class", instrument.AssetClass.ToString());
        command.Parameters.AddWithValue("$mode", mode.ToString());
        command.Parameters.AddWithValue("$started_at_utc", Format(startedAtUtc));
        command.Parameters.AddWithValue("$starting_balance", (double)startingBalance);
        command.Parameters.AddWithValue("$settings_json", settingsJson);
        command.Prepare();
        command.ExecuteNonQuery();
        return session;
    }

    public void AppendQuotes(
        Guid sessionId,
        IEnumerable<MarketQuote> quotes,
        QuoteIngestionKind ingestionKind)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(quotes);
        MarketQuote[] batch = [.. quotes];

        if (batch.Length == 0)
        {
            return;
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO quotes(
                session_id, symbol, asset_class, observed_at_utc,
                source_at_utc, bid, ask, last, volume,
                open_price, high_price, low_price, close_price, ingestion_kind)
            VALUES(
                $session_id, $symbol, $asset_class, $observed_at_utc,
                $source_at_utc, $bid, $ask, $last, $volume,
                $open_price, $high_price, $low_price, $close_price, $ingestion_kind);
            """;
        SqliteParameter sessionParameter = command.Parameters.Add("$session_id", SqliteType.Text);
        SqliteParameter symbolParameter = command.Parameters.Add("$symbol", SqliteType.Text);
        SqliteParameter assetClassParameter = command.Parameters.Add("$asset_class", SqliteType.Text);
        SqliteParameter observedParameter = command.Parameters.Add("$observed_at_utc", SqliteType.Text);
        SqliteParameter sourceParameter = command.Parameters.Add("$source_at_utc", SqliteType.Text);
        SqliteParameter bidParameter = command.Parameters.Add("$bid", SqliteType.Real);
        SqliteParameter askParameter = command.Parameters.Add("$ask", SqliteType.Real);
        SqliteParameter lastParameter = command.Parameters.Add("$last", SqliteType.Real);
        SqliteParameter volumeParameter = command.Parameters.Add("$volume", SqliteType.Real);
        SqliteParameter openParameter = command.Parameters.Add("$open_price", SqliteType.Real);
        SqliteParameter highParameter = command.Parameters.Add("$high_price", SqliteType.Real);
        SqliteParameter lowParameter = command.Parameters.Add("$low_price", SqliteType.Real);
        SqliteParameter closeParameter = command.Parameters.Add("$close_price", SqliteType.Real);
        SqliteParameter kindParameter = command.Parameters.Add("$ingestion_kind", SqliteType.Text);
        command.Prepare();

        foreach (MarketQuote quote in batch)
        {
            sessionParameter.Value = sessionId.ToString("D");
            symbolParameter.Value = quote.Instrument.Symbol;
            assetClassParameter.Value = quote.Instrument.AssetClass.ToString();
            observedParameter.Value = Format(quote.ObservedAtUtc);
            sourceParameter.Value = Format(quote.SourceTimestampUtc);
            bidParameter.Value = (double)quote.Bid;
            askParameter.Value = (double)quote.Ask;
            lastParameter.Value = (double)quote.Last;
            volumeParameter.Value = (double)quote.Volume;
            openParameter.Value = ToDatabaseValue(quote.OpenPrice);
            highParameter.Value = ToDatabaseValue(quote.HighPrice);
            lowParameter.Value = ToDatabaseValue(quote.LowPrice);
            closeParameter.Value = ToDatabaseValue(quote.ClosePrice);
            kindParameter.Value = ingestionKind.ToString();
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void AppendActivity(
        Guid? sessionId,
        DateTimeOffset occurredAtUtc,
        string level,
        string message)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO activities(session_id, occurred_at_utc, level, message)
            VALUES($session_id, $occurred_at_utc, $level, $message);
            """;
        command.Parameters.AddWithValue(
            "$session_id",
            sessionId is null ? DBNull.Value : sessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$occurred_at_utc", Format(occurredAtUtc));
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$message", message);
        command.Prepare();
        command.ExecuteNonQuery();
    }

    public void AppendDecision(Guid sessionId, StrategyDecision decision)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(decision);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO decisions(
                session_id, evaluated_at_utc, state, signal, confidence, reasons_json)
            VALUES(
                $session_id, $evaluated_at_utc, $state, $signal, $confidence, $reasons_json);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$evaluated_at_utc", Format(decision.EvaluatedAtUtc));
        command.Parameters.AddWithValue("$state", decision.State);
        command.Parameters.AddWithValue("$signal", decision.Signal.ToString());
        command.Parameters.AddWithValue("$confidence", (double)decision.Confidence);
        command.Parameters.AddWithValue("$reasons_json", JsonSerializer.Serialize(decision.Reasons));
        command.Prepare();
        command.ExecuteNonQuery();
    }

    public void AppendPaperFill(
        Guid sessionId,
        Instrument instrument,
        PaperOrder order,
        PaperFill fill,
        PaperAccountSnapshot account)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(account);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand orderCommand = connection.CreateCommand())
        {
            orderCommand.Transaction = transaction;
            orderCommand.CommandText =
                """
                INSERT INTO orders(
                    id, session_id, submitted_at_utc, side, quantity,
                    order_type, limit_price, status, broker_order_id)
                VALUES(
                    $id, $session_id, $submitted_at_utc, $side, $quantity,
                    'PAPER_MARKET', NULL, 'FILLED', NULL);
                """;
            orderCommand.Parameters.AddWithValue("$id", order.Id.ToString("D"));
            orderCommand.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
            orderCommand.Parameters.AddWithValue("$submitted_at_utc", Format(order.SubmittedAtUtc));
            orderCommand.Parameters.AddWithValue("$side", order.Side.ToString());
            orderCommand.Parameters.AddWithValue("$quantity", (double)order.Quantity);
            orderCommand.ExecuteNonQuery();
        }

        using (SqliteCommand fillCommand = connection.CreateCommand())
        {
            fillCommand.Transaction = transaction;
            fillCommand.CommandText =
                """
                INSERT INTO fills(order_id, filled_at_utc, quantity, price, fees)
                VALUES($order_id, $filled_at_utc, $quantity, $price, 0);
                """;
            fillCommand.Parameters.AddWithValue("$order_id", order.Id.ToString("D"));
            fillCommand.Parameters.AddWithValue("$filled_at_utc", Format(fill.FilledAtUtc));
            fillCommand.Parameters.AddWithValue("$quantity", (double)fill.Quantity);
            fillCommand.Parameters.AddWithValue("$price", (double)fill.Price);
            fillCommand.ExecuteNonQuery();
        }

        using (SqliteCommand positionCommand = connection.CreateCommand())
        {
            positionCommand.Transaction = transaction;
            positionCommand.CommandText =
                """
                INSERT INTO positions(
                    session_id, observed_at_utc, symbol, quantity, average_price, market_value)
                VALUES(
                    $session_id, $observed_at_utc, $symbol, $quantity, $average_price, $market_value);
                """;
            positionCommand.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
            positionCommand.Parameters.AddWithValue("$observed_at_utc", Format(fill.FilledAtUtc));
            positionCommand.Parameters.AddWithValue("$symbol", instrument.Symbol);
            positionCommand.Parameters.AddWithValue("$quantity", (double)account.PositionQuantity);
            positionCommand.Parameters.AddWithValue("$average_price", (double)account.AveragePrice);
            positionCommand.Parameters.AddWithValue("$market_value", (double)account.MarketValue);
            positionCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void AppendLiveOrderEvent(
        Guid sessionId,
        Instrument instrument,
        string eventType,
        BrokerOrderIntent intent,
        BrokerOrderReview? review,
        BrokerOrderSnapshot? order,
        DateTimeOffset occurredAtUtc)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(intent);

        string clientReference = intent.ClientReferenceId.ToString("D");
        string state = order?.State.ToString() ??
                       (review is null ? "CREATED" : "REVIEWED");
        string details = JsonSerializer.Serialize(new
        {
            Intent = new { intent.Reason, intent.CreatedAtUtc },
            Review = review is null ? null : new
            {
                review.Accepted,
                review.Blockers,
                review.BidPrice,
                review.AskPrice,
                review.LastPrice,
                review.MarketDataDisclosure,
                OrderChecks = review.RawOrderChecksJson,
            },
            Order = order is null ? null : new
            {
                order.BrokerOrderId,
                State = order.State.ToString(),
                order.FilledQuantity,
                order.AveragePrice,
                order.RejectionReason,
                order.UpdatedAtUtc,
                order.Executions,
            },
        });

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand orderCommand = connection.CreateCommand())
        {
            orderCommand.Transaction = transaction;
            orderCommand.CommandText =
                """
                INSERT INTO orders(
                    id, session_id, submitted_at_utc, side, quantity,
                    order_type, limit_price, status, broker_order_id)
                VALUES(
                    $id, $session_id, $submitted_at_utc, $side, $quantity,
                    'LIVE_MARKET', NULL, $status, $broker_order_id)
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    broker_order_id = COALESCE(excluded.broker_order_id, orders.broker_order_id);
                """;
            orderCommand.Parameters.AddWithValue("$id", clientReference);
            orderCommand.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
            orderCommand.Parameters.AddWithValue("$submitted_at_utc", Format(intent.CreatedAtUtc));
            orderCommand.Parameters.AddWithValue("$side", intent.Side.ToString());
            orderCommand.Parameters.AddWithValue("$quantity", (double)intent.Quantity);
            orderCommand.Parameters.AddWithValue("$status", state);
            orderCommand.Parameters.AddWithValue(
                "$broker_order_id",
                string.IsNullOrWhiteSpace(order?.BrokerOrderId)
                    ? DBNull.Value
                    : order.BrokerOrderId);
            orderCommand.ExecuteNonQuery();
        }

        using (SqliteCommand eventCommand = connection.CreateCommand())
        {
            eventCommand.Transaction = transaction;
            eventCommand.CommandText =
                """
                INSERT INTO live_order_events(
                    session_id, occurred_at_utc, event_type,
                    client_reference_id, broker_order_id, symbol,
                    side, quantity, state, details_json)
                VALUES(
                    $session_id, $occurred_at_utc, $event_type,
                    $client_reference_id, $broker_order_id, $symbol,
                    $side, $quantity, $state, $details_json);
                """;
            eventCommand.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
            eventCommand.Parameters.AddWithValue("$occurred_at_utc", Format(occurredAtUtc));
            eventCommand.Parameters.AddWithValue("$event_type", eventType);
            eventCommand.Parameters.AddWithValue("$client_reference_id", clientReference);
            eventCommand.Parameters.AddWithValue(
                "$broker_order_id",
                string.IsNullOrWhiteSpace(order?.BrokerOrderId)
                    ? DBNull.Value
                    : order.BrokerOrderId);
            eventCommand.Parameters.AddWithValue("$symbol", instrument.Symbol);
            eventCommand.Parameters.AddWithValue("$side", intent.Side.ToString());
            eventCommand.Parameters.AddWithValue("$quantity", (double)intent.Quantity);
            eventCommand.Parameters.AddWithValue("$state", state);
            eventCommand.Parameters.AddWithValue("$details_json", details);
            eventCommand.ExecuteNonQuery();
        }

        if (order is not null)
        {
            AppendLiveExecutions(connection, transaction, clientReference, order.Executions);
        }

        transaction.Commit();
    }

    public decimal? GetLiveStartingBalanceSince(
        DateTimeOffset startedAtGteUtc)
    {
        EnsureInitialized();

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT starting_balance
            FROM sessions
            WHERE mode = $mode AND started_at_utc >= $started_at_gte_utc
            ORDER BY started_at_utc ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$mode", TradingMode.Live.ToString());
        command.Parameters.AddWithValue("$started_at_gte_utc", Format(startedAtGteUtc));
        command.Prepare();
        object? result = command.ExecuteScalar();
        return result is null or DBNull
            ? null
            : Convert.ToDecimal(result, CultureInfo.InvariantCulture);
    }

    public bool HasUnattributedLiveSessionsSince(DateTimeOffset startedAtGteUtc)
    {
        EnsureInitialized();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1 FROM sessions
                WHERE mode = $mode AND started_at_utc >= $day
                  AND CASE WHEN json_valid(settings_json) THEN
                      COALESCE(json_type(settings_json, '$.LiveAccountNumber'), '') <> 'text'
                      OR trim(COALESCE(json_extract(settings_json, '$.LiveAccountNumber'), '')) = ''
                      ELSE 1 END);
            """;
        command.Parameters.AddWithValue("$mode", TradingMode.Live.ToString());
        command.Parameters.AddWithValue("$day", Format(startedAtGteUtc));
        return (long)command.ExecuteScalar()! != 0;
    }

    public decimal? GetLiveDailyStartingBalance(
        string accountNumber,
        DateTimeOffset tradingDayStartUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        EnsureInitialized();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT starting_balance FROM live_daily_baselines
            WHERE account_number = $account_number AND trading_day_start_utc = $day;
            """;
        command.Parameters.AddWithValue("$account_number", accountNumber);
        command.Parameters.AddWithValue("$day", Format(tradingDayStartUtc));
        object? result = command.ExecuteScalar();
        return result is null or DBNull
            ? null
            : decimal.Parse((string)result, CultureInfo.InvariantCulture);
    }

    public decimal GetOrCreateLiveDailyStartingBalance(
        string accountNumber,
        DateTimeOffset tradingDayStartUtc,
        decimal startingBalance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startingBalance);
        EnsureInitialized();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO live_daily_baselines(
                account_number, trading_day_start_utc, starting_balance)
            VALUES ($account_number, $day, $balance);
            SELECT starting_balance FROM live_daily_baselines
            WHERE account_number = $account_number AND trading_day_start_utc = $day;
            """;
        command.Parameters.AddWithValue("$account_number", accountNumber);
        command.Parameters.AddWithValue("$day", Format(tradingDayStartUtc));
        command.Parameters.AddWithValue(
            "$balance", startingBalance.ToString(CultureInfo.InvariantCulture));
        decimal baseline = decimal.Parse(
            (string)command.ExecuteScalar()!, CultureInfo.InvariantCulture);
        transaction.Commit();
        return baseline;
    }

    public void CompleteSession(Guid sessionId, DateTimeOffset endedAtUtc, string outcome)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE sessions
            SET ended_at_utc = $ended_at_utc, outcome = $outcome
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$ended_at_utc", Format(endedAtUtc));
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        command.Prepare();
        command.ExecuteNonQuery();
    }

    public JournalSummary GetSummary(Guid sessionId)
    {
        EnsureInitialized();

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM quotes WHERE session_id = $session_id),
                (SELECT COUNT(*) FROM activities WHERE session_id = $session_id),
                (SELECT COUNT(*) FROM decisions WHERE session_id = $session_id),
                (SELECT COUNT(*) FROM orders WHERE session_id = $session_id),
                (SELECT COUNT(*) FROM fills AS f
                    INNER JOIN orders AS o ON o.id = f.order_id
                    WHERE o.session_id = $session_id);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        command.Prepare();

        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();
        return new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    public ReplaySourceSession? FindLatestReplaySource(Instrument instrument)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(instrument);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.id, s.started_at_utc, COUNT(q.id)
            FROM sessions AS s
            INNER JOIN quotes AS q ON q.session_id = s.id
            WHERE s.symbol = $symbol
              AND s.asset_class = $asset_class
              AND s.mode = 'PaperTrader'
            GROUP BY s.id, s.started_at_utc
            ORDER BY s.started_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$symbol", instrument.Symbol);
        command.Parameters.AddWithValue("$asset_class", instrument.AssetClass.ToString());
        command.Prepare();

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return new(
            Guid.Parse(reader.GetString(0)),
            instrument,
            DateTimeOffset.Parse(
                reader.GetString(1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            reader.GetInt32(2));
    }

    public IReadOnlyList<MarketQuote> ReadSessionQuotes(
        Guid sourceSessionId,
        Instrument instrument)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(instrument);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                q.observed_at_utc, q.source_at_utc, q.bid, q.ask, q.last, q.volume,
                q.open_price, q.high_price, q.low_price, q.close_price
            FROM quotes AS q
            INNER JOIN (
                SELECT source_at_utc, MAX(id) AS latest_id
                FROM quotes
                WHERE session_id = $session_id
                GROUP BY source_at_utc
            ) AS latest ON latest.latest_id = q.id
            ORDER BY q.source_at_utc;
            """;
        command.Parameters.AddWithValue("$session_id", sourceSessionId.ToString("D"));
        command.Prepare();

        using SqliteDataReader reader = command.ExecuteReader();
        var quotes = new List<MarketQuote>();

        while (reader.Read())
        {
            quotes.Add(new(
                instrument,
                DateTimeOffset.Parse(
                    reader.GetString(0),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                Convert.ToDecimal(reader.GetDouble(2), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(3), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(4), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetDouble(5), CultureInfo.InvariantCulture),
                ReadNullableDecimal(reader, 6),
                ReadNullableDecimal(reader, 7),
                ReadNullableDecimal(reader, 8),
                ReadNullableDecimal(reader, 9)));
        }

        return quotes;
    }

    public void Dispose()
    {
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using SqliteCommand foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
        foreignKeys.ExecuteNonQuery();
        return connection;
    }

    private static object ToDatabaseValue(decimal? value) =>
        value.HasValue ? (double)value.Value : DBNull.Value;

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDecimal(reader.GetDouble(ordinal), CultureInfo.InvariantCulture);

    internal static void AppendLiveExecutions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string orderId,
        IEnumerable<BrokerExecution> executions)
    {
        foreach (BrokerExecution execution in executions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(execution.Id);
            using SqliteCommand fillCommand = connection.CreateCommand();
            fillCommand.Transaction = transaction;
            fillCommand.CommandText =
                """
                UPDATE fills SET execution_id = $execution_id
                WHERE id = (
                    SELECT id FROM fills
                    WHERE order_id = $order_id AND execution_id IS NULL
                      AND filled_at_utc = $filled_at_utc
                      AND quantity = $quantity AND price = $price
                    ORDER BY id LIMIT 1)
                  AND NOT EXISTS (
                    SELECT 1 FROM fills
                    WHERE order_id = $order_id AND execution_id = $execution_id);
                INSERT INTO fills(order_id, execution_id, filled_at_utc, quantity, price, fees)
                SELECT $order_id, $execution_id, $filled_at_utc, $quantity, $price, 0
                WHERE NOT EXISTS (
                    SELECT 1 FROM fills
                    WHERE order_id = $order_id AND execution_id = $execution_id);
                """;
            fillCommand.Parameters.AddWithValue("$order_id", orderId);
            fillCommand.Parameters.AddWithValue("$execution_id", execution.Id);
            fillCommand.Parameters.AddWithValue(
                "$filled_at_utc",
                Format(execution.OccurredAtUtc));
            fillCommand.Parameters.AddWithValue("$quantity", (double)execution.Quantity);
            fillCommand.Parameters.AddWithValue("$price", (double)execution.Price);
            fillCommand.ExecuteNonQuery();
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
