-- Catalogue, markets, append-only price history and the derived "current price" per market and product.

CREATE TABLE raf.market (
    code        text        PRIMARY KEY CHECK (code ~ '^[a-z][a-z0-9-]{1,31}$'),
    name        text        NOT NULL,
    active      boolean     NOT NULL DEFAULT true,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- One row per sellable item, identified by its canonical GTIN-14 (docs/adr/0002).
CREATE TABLE raf.product (
    id                 uuid          PRIMARY KEY,
    gtin               char(14)      NOT NULL UNIQUE CHECK (gtin ~ '^[0-9]{14}$'),
    name               text          NOT NULL,
    brand              text          NOT NULL,
    category           text          NOT NULL,
    size_dimension     text          NOT NULL CHECK (size_dimension IN ('mass', 'volume', 'count')),
    size_base_amount   numeric(12,3) NOT NULL CHECK (size_base_amount > 0),
    substitution_group text          NULL,
    created_at         timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX product_substitution_group_ix ON raf.product (substitution_group) WHERE substitution_group IS NOT NULL;
CREATE INDEX product_name_ix ON raf.product (lower(name) text_pattern_ops);

-- Feeds that key items by the market's own code (no barcode) are matched through this curated table.
CREATE TABLE raf.market_sku (
    market_code text NOT NULL REFERENCES raf.market (code),
    sku         text NOT NULL,
    product_id  uuid NOT NULL REFERENCES raf.product (id),
    PRIMARY KEY (market_code, sku)
);

-- Every ingestion attempt, successful or not: the raw material of feed health.
CREATE TABLE raf.feed_run (
    id                    bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    market_code           text        NOT NULL REFERENCES raf.market (code),
    started_at            timestamptz NOT NULL,
    finished_at           timestamptz NOT NULL,
    status                text        NOT NULL CHECK (status IN ('succeeded', 'failed', 'skipped')),
    http_status           integer     NULL,
    pages                 integer     NOT NULL DEFAULT 0,
    items_received        integer     NOT NULL DEFAULT 0,
    items_matched         integer     NOT NULL DEFAULT 0,
    items_unmatched       integer     NOT NULL DEFAULT 0,
    items_rejected        integer     NOT NULL DEFAULT 0,
    observations_inserted integer     NOT NULL DEFAULT 0,
    prices_changed        integer     NOT NULL DEFAULT 0,
    error                 text        NULL
);
CREATE INDEX feed_run_market_started_ix ON raf.feed_run (market_code, started_at DESC);

-- Rolling health per market, updated by every run and by the watchdog job.
CREATE TABLE raf.market_feed_state (
    market_code          text        PRIMARY KEY REFERENCES raf.market (code),
    status               text        NOT NULL CHECK (status IN ('healthy', 'degraded', 'down', 'unknown')),
    last_attempt_at      timestamptz NULL,
    last_success_at      timestamptz NULL,
    consecutive_failures integer     NOT NULL DEFAULT 0,
    last_error           text        NULL,
    changed_at           timestamptz NOT NULL DEFAULT now()
);

-- Append-only price history. A row is a market's statement "this price was valid at observed_at".
-- Re-reading the same feed produces the same (market, product, observed_at) and is a no-op: ingestion is idempotent.
CREATE TABLE raf.price_observation (
    id           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    market_code  text        NOT NULL REFERENCES raf.market (code),
    product_id   uuid        NOT NULL REFERENCES raf.product (id),
    price_kurus  bigint      NOT NULL CHECK (price_kurus > 0),
    observed_at  timestamptz NOT NULL,
    source       text        NOT NULL,
    feed_run_id  bigint      NULL REFERENCES raf.feed_run (id) ON DELETE SET NULL,
    ingested_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT price_observation_once UNIQUE (market_code, product_id, observed_at)
);
CREATE INDEX price_observation_product_ix ON raf.price_observation (product_id, market_code, observed_at DESC);

-- Latest evidence per market and product, maintained by the same transaction that appends observations.
-- observed_at only ever moves forward, so a late or replayed older feed cannot roll a price back.
CREATE TABLE raf.current_price (
    market_code  text        NOT NULL REFERENCES raf.market (code),
    product_id   uuid        NOT NULL REFERENCES raf.product (id),
    price_kurus  bigint      NOT NULL CHECK (price_kurus > 0),
    observed_at  timestamptz NOT NULL,
    price_since  timestamptz NOT NULL,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (market_code, product_id)
);
CREATE INDEX current_price_product_ix ON raf.current_price (product_id);

-- Baskets are owned by a device (no accounts yet); version enables optimistic concurrency via If-Match.
CREATE TABLE raf.basket (
    id          uuid        PRIMARY KEY,
    name        text        NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
    version     integer     NOT NULL DEFAULT 1,
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE raf.basket_item (
    basket_id         uuid    NOT NULL REFERENCES raf.basket (id) ON DELETE CASCADE,
    product_id        uuid    NOT NULL REFERENCES raf.product (id),
    quantity          integer NOT NULL CHECK (quantity BETWEEN 1 AND 99),
    allow_substitutes boolean NOT NULL DEFAULT true,
    PRIMARY KEY (basket_id, product_id)
);

-- History is evidence: the application may add observations but never change or remove them.
REVOKE UPDATE, DELETE, TRUNCATE ON raf.price_observation FROM raf_app;
-- The catalogue and markets are reference data maintained by the seed/migrations, read-only for the API.
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON raf.product, raf.market FROM raf_app;
