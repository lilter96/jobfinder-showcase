CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_hiring_replies (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_hiring_replies_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_wolverine_queue_hiring_replies_timestamp ON jobfinder_queues.wolverine_queue_hiring_replies USING btree (timestamp);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_hiring_replies_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_hiring_replies_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_wolverine_queue_hiring_replies_scheduled_execution_time ON jobfinder_queues.wolverine_queue_hiring_replies_scheduled USING btree (execution_time);
