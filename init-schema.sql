-- Schema initialization for service_records
CREATE TABLE IF NOT EXISTS service_records (
    id SERIAL PRIMARY KEY,
    service_name VARCHAR(100) NOT NULL,
    environment VARCHAR(50) NOT NULL,
    status VARCHAR(50) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Schema indexing on environment column for query performance
CREATE INDEX IF NOT EXISTS idx_services_env ON service_records(environment);
