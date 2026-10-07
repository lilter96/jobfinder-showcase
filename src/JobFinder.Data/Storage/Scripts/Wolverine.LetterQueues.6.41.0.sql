CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_hiring_letter_draft (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_hiring_letter_draft_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_wolverine_queue_hiring_letter_draft_timestamp ON jobfinder_queues.wolverine_queue_hiring_letter_draft USING btree (timestamp);

CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_hiring_letter_draft_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_hiring_letter_draft_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_wolverine_queue_hiring_letter_draft_scheduled_executio_ca48 ON jobfinder_queues.wolverine_queue_hiring_letter_draft_scheduled USING btree (execution_time);

CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_hiring_letter_review (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_hiring_letter_review_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_wolverine_queue_hiring_letter_review_timestamp ON jobfinder_queues.wolverine_queue_hiring_letter_review USING btree (timestamp);

CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_hiring_letter_review_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_hiring_letter_review_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS idx_wolverine_queue_hiring_letter_review_scheduled_executi_df79 ON jobfinder_queues.wolverine_queue_hiring_letter_review_scheduled USING btree (execution_time);
