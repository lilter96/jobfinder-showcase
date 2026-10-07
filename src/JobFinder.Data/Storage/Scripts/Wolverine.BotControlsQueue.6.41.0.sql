CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_bot_controls (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_bot_controls_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_wolverine_queue_bot_controls_timestamp ON jobfinder_queues.wolverine_queue_bot_controls USING btree (timestamp);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_bot_controls_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_bot_controls_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_wolverine_queue_bot_controls_scheduled_execution_time ON jobfinder_queues.wolverine_queue_bot_controls_scheduled USING btree (execution_time);
