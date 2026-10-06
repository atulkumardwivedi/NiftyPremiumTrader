CREATE SCHEMA IF NOT EXISTS trading;

CREATE TABLE IF NOT EXISTS trading.option_expiries
(
    expiry_date DATE PRIMARY KEY,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- One row per 5-minute candle per contract.
-- Options and NIFTY cash candles use the same table so backtest and live
-- always read the same data shape.
CREATE TABLE IF NOT EXISTS trading.premium_candles_5m
(
    candle_id BIGSERIAL PRIMARY KEY,
    session_date DATE NOT NULL,
    candle_time TIMESTAMPTZ NOT NULL,
    contract_key VARCHAR(180) NOT NULL,
    stock_code VARCHAR(30) NOT NULL DEFAULT 'NIFTY',
    exchange_code VARCHAR(10) NOT NULL,
    product_type VARCHAR(20) NOT NULL,
    expiry_date DATE,
    strike NUMERIC(12, 2),
    option_type VARCHAR(5),
    open_price NUMERIC(18, 6) NOT NULL,
    high_price NUMERIC(18, 6) NOT NULL,
    low_price NUMERIC(18, 6) NOT NULL,
    close_price NUMERIC(18, 6) NOT NULL,
    volume BIGINT NOT NULL DEFAULT 0,
    open_interest BIGINT NOT NULL DEFAULT 0,
    data_source VARCHAR(30) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_premium_candle UNIQUE(session_date, contract_key, candle_time)
);

CREATE INDEX IF NOT EXISTS ix_premium_candles_option_lookup
ON trading.premium_candles_5m
(
    session_date,
    expiry_date,
    strike,
    option_type,
    candle_time
);

CREATE INDEX IF NOT EXISTS ix_premium_candles_contract_time
ON trading.premium_candles_5m
(
    contract_key,
    candle_time
);

CREATE TABLE IF NOT EXISTS trading.api_daily_budget
(
    usage_date DATE PRIMARY KEY,
    used_count INTEGER NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);


-- Records that a complete historical REST request was made for a contract/day.
-- This prevents replay steps from downloading the same full-day candles again
-- when an exchange candle is naturally missing.
CREATE TABLE IF NOT EXISTS trading.candle_day_loads
(
    session_date DATE NOT NULL,
    load_key VARCHAR(220) NOT NULL,
    candle_count INTEGER NOT NULL DEFAULT 0,
    loaded_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY(session_date, load_key)
);

CREATE TABLE IF NOT EXISTS trading.strategy_trades
(
    trade_id UUID PRIMARY KEY,
    mode VARCHAR(20) NOT NULL,
    session_date DATE NOT NULL,
    direction VARCHAR(10) NOT NULL,
    signal_time TIMESTAMPTZ NOT NULL,
    sold_option_type VARCHAR(5) NOT NULL,
    sold_strike NUMERIC(12, 2) NOT NULL,
    sold_entry_time TIMESTAMPTZ NOT NULL,
    sold_entry_price NUMERIC(18, 6) NOT NULL,
    hedge_distance INTEGER,
    hedge_strike NUMERIC(12, 2),
    hedge_entry_time TIMESTAMPTZ,
    hedge_entry_price NUMERIC(18, 6),
    hedge_trigger_pnl NUMERIC(18, 2),
    exit_time TIMESTAMPTZ,
    sold_exit_price NUMERIC(18, 6),
    hedge_exit_price NUMERIC(18, 6),
    quantity INTEGER NOT NULL,
    lots INTEGER NOT NULL DEFAULT 1,
    lot_size INTEGER NOT NULL DEFAULT 1,
    gross_pnl NUMERIC(18, 2) NOT NULL DEFAULT 0,
    costs NUMERIC(18, 2) NOT NULL DEFAULT 0,
    net_pnl NUMERIC(18, 2) NOT NULL DEFAULT 0,
    best_mark_pnl NUMERIC(18, 2) NOT NULL DEFAULT 0,
    worst_mark_pnl NUMERIC(18, 2) NOT NULL DEFAULT 0,
    exit_reason VARCHAR(50),
    sold_entry_order_id VARCHAR(100),
    hedge_entry_order_id VARCHAR(100),
    sold_exit_order_id VARCHAR(100),
    hedge_exit_order_id VARCHAR(100),
    broker_execution BOOLEAN NOT NULL DEFAULT FALSE,
    status VARCHAR(20) NOT NULL DEFAULT 'OPEN',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_strategy_trades_session
ON trading.strategy_trades(session_date, sold_entry_time);


-- Safe upgrade for databases created by earlier builds.
ALTER TABLE trading.strategy_trades
ADD COLUMN IF NOT EXISTS lots INTEGER NOT NULL DEFAULT 1;

ALTER TABLE trading.strategy_trades
ADD COLUMN IF NOT EXISTS lot_size INTEGER NOT NULL DEFAULT 1;
ALTER TABLE trading.strategy_trades
ADD COLUMN IF NOT EXISTS broker_execution BOOLEAN NOT NULL DEFAULT FALSE;



-- Manual orders placed from this Angular UI.
-- Orders placed directly in the Kotak app are discovered from Kotak and are
-- intentionally not inserted here; unknown broker order ids are classified
-- as MANUAL_APP in the broker dashboard.
CREATE TABLE IF NOT EXISTS trading.manual_broker_orders
(
    local_order_id UUID PRIMARY KEY,
    broker_order_id VARCHAR(100) NOT NULL UNIQUE,
    session_date DATE NOT NULL,
    trading_symbol VARCHAR(150) NOT NULL,
    instrument_token VARCHAR(64),
    strike NUMERIC(12, 2),
    option_type VARCHAR(5),
    side VARCHAR(10) NOT NULL,
    quantity INTEGER NOT NULL,
    lots INTEGER NOT NULL DEFAULT 0,
    lot_size INTEGER NOT NULL DEFAULT 1,
    order_type VARCHAR(20) NOT NULL,
    limit_price NUMERIC(18, 6),
    purpose VARCHAR(20) NOT NULL DEFAULT 'ENTRY',
    status VARCHAR(50),
    average_price NUMERIC(18, 6),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_manual_broker_orders_date
ON trading.manual_broker_orders(session_date, created_at DESC);

-- Additive migration: existing candles were completed-only in the preceding build.
ALTER TABLE trading.premium_candles_5m ADD COLUMN IF NOT EXISTS is_complete BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE trading.premium_candles_5m ADD COLUMN IF NOT EXISTS delta NUMERIC(18,10);
CREATE TABLE IF NOT EXISTS trading.morning_signal_selections
(
    session_date DATE NOT NULL, rule_version TEXT NOT NULL,
    expiry_date DATE NOT NULL, morning_spot NUMERIC(18,6) NOT NULL,
    rounded_strike NUMERIC(12,2) NOT NULL, spot_source TEXT NOT NULL,
    pairs JSONB NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY(session_date, rule_version)
);

ALTER TABLE trading.strategy_trades ADD COLUMN IF NOT EXISTS risk_setup jsonb;
