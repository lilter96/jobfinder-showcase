DO LANGUAGE plpgsql $tran$
BEGIN

DO $$
BEGIN      IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = 'jobfinder_messages') THEN
        BEGIN
          EXECUTE 'CREATE SCHEMA IF NOT EXISTS jobfinder_messages';
        EXCEPTION
          WHEN duplicate_schema THEN NULL;
          WHEN unique_violation THEN NULL;
        END;
      END IF;

      IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = 'jobfinder_queues') THEN
        BEGIN
          EXECUTE 'CREATE SCHEMA IF NOT EXISTS jobfinder_queues';
        EXCEPTION
          WHEN duplicate_schema THEN NULL;
          WHEN unique_violation THEN NULL;
        END;
      END IF;

END
$$;


CREATE TABLE IF NOT EXISTS jobfinder_messages.wolverine_outgoing_envelopes (
    id              uuid                        NOT NULL,
    owner_id        integer                     NOT NULL,
    destination     varchar                     NOT NULL,
    deliver_by      timestamp with time zone    NULL,
    body            bytea                       NOT NULL,
    attempts        integer                     NULL DEFAULT 0,
    message_type    varchar                     NOT NULL,
CONSTRAINT pkey_wolverine_outgoing_envelopes_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_outgoing_envelopes_owner ON jobfinder_messages.wolverine_outgoing_envelopes USING btree (owner_id) WHERE (owner_id <> 0);

CREATE INDEX idx_wolverine_outgoing_envelopes_recover ON jobfinder_messages.wolverine_outgoing_envelopes USING btree (destination) WHERE (owner_id = 0);
CREATE TABLE IF NOT EXISTS jobfinder_messages.wolverine_incoming_envelopes (
    id                uuid                        NOT NULL,
    status            varchar                     NOT NULL,
    owner_id          integer                     NOT NULL,
    execution_time    timestamp with time zone    NULL DEFAULT NULL,
    attempts          integer                     NULL DEFAULT 0,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    received_at       varchar                     NULL,
    keep_until        timestamp with time zone    NULL,
CONSTRAINT pkey_wolverine_incoming_envelopes_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_incoming_envelopes_owner ON jobfinder_messages.wolverine_incoming_envelopes USING btree (owner_id) WHERE (owner_id <> 0);

CREATE INDEX idx_wolverine_incoming_envelopes_recover ON jobfinder_messages.wolverine_incoming_envelopes USING btree (received_at) WHERE (status = 'Incoming' AND owner_id = 0);

CREATE INDEX idx_wolverine_incoming_envelopes_keep_until ON jobfinder_messages.wolverine_incoming_envelopes USING btree (keep_until) WHERE (status = 'Handled');
CREATE TABLE IF NOT EXISTS jobfinder_messages.wolverine_dead_letters (
    id                   uuid                        NOT NULL,
    execution_time       timestamp with time zone    NULL DEFAULT NULL,
    body                 bytea                       NOT NULL,
    message_type         varchar                     NOT NULL,
    received_at          varchar                     NULL,
    source               varchar                     NULL,
    exception_type       varchar                     NULL,
    exception_message    varchar                     NULL,
    sent_at              timestamp with time zone    NULL,
    replayable           boolean                     NULL,
CONSTRAINT pkey_wolverine_dead_letters_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_dead_letters_replayable ON jobfinder_messages.wolverine_dead_letters USING btree (replayable) WHERE (replayable = true);
CREATE TABLE IF NOT EXISTS jobfinder_messages.wolverine_nodes (
    id              uuid                        NOT NULL,
    node_number     serial                      NOT NULL,
    description     varchar                     NOT NULL,
    uri             varchar                     NOT NULL,
    started         timestamp with time zone    NOT NULL DEFAULT now(),
    health_check    timestamp with time zone    NOT NULL DEFAULT now(),
    version         varchar                     NULL,
    capabilities    text[]                      NULL,
CONSTRAINT pkey_wolverine_nodes_id PRIMARY KEY (id)
);
CREATE TABLE IF NOT EXISTS jobfinder_messages.wolverine_node_assignments (
    id         varchar                     NOT NULL,
    node_id    uuid                        NULL,
    started    timestamp with time zone    NOT NULL DEFAULT now(),
CONSTRAINT pkey_wolverine_node_assignments_id PRIMARY KEY (id)
);

ALTER TABLE jobfinder_messages.wolverine_node_assignments
ADD CONSTRAINT fkey_wolverine_node_assignments_node_id FOREIGN KEY(node_id)
REFERENCES jobfinder_messages.wolverine_nodes(id)ON DELETE CASCADE
;

CREATE TABLE IF NOT EXISTS jobfinder_messages.wolverine_control_queue (
    id              uuid                        NOT NULL,
    message_type    varchar                     NOT NULL,
    node_id         uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    posted          timestamp with time zone    NOT NULL DEFAULT NOW(),
    expires         timestamp with time zone    NULL,
CONSTRAINT pkey_wolverine_control_queue_id PRIMARY KEY (id)
);
CREATE TABLE IF NOT EXISTS jobfinder_messages.wolverine_node_records (
    id             serial                      NOT NULL,
    node_number    integer                     NOT NULL,
    event_name     varchar                     NOT NULL,
    timestamp      timestamp with time zone    NOT NULL DEFAULT now(),
    description    varchar                     NULL,
CONSTRAINT pkey_wolverine_node_records_id PRIMARY KEY (id)
);
CREATE TABLE IF NOT EXISTS jobfinder_messages.wolverine_agent_restrictions (
    id      uuid       NOT NULL,
    uri     varchar    NOT NULL,
    type    varchar    NOT NULL,
    node    integer    NOT NULL DEFAULT 0,
CONSTRAINT pkey_wolverine_agent_restrictions_id PRIMARY KEY (id)
);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_hiring_deliver (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_hiring_deliver_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_hiring_deliver_timestamp ON jobfinder_queues.wolverine_queue_hiring_deliver USING btree (timestamp);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_hiring_deliver_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_hiring_deliver_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_hiring_deliver_scheduled_execution_time ON jobfinder_queues.wolverine_queue_hiring_deliver_scheduled USING btree (execution_time);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_hiring_prepare (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_hiring_prepare_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_hiring_prepare_timestamp ON jobfinder_queues.wolverine_queue_hiring_prepare USING btree (timestamp);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_hiring_prepare_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_hiring_prepare_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_hiring_prepare_scheduled_execution_time ON jobfinder_queues.wolverine_queue_hiring_prepare_scheduled USING btree (execution_time);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_preparation (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_preparation_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_preparation_timestamp ON jobfinder_queues.wolverine_queue_preparation USING btree (timestamp);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_preparation_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_preparation_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_preparation_scheduled_execution_time ON jobfinder_queues.wolverine_queue_preparation_scheduled USING btree (execution_time);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_discovery (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_discovery_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_discovery_timestamp ON jobfinder_queues.wolverine_queue_discovery USING btree (timestamp);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_discovery_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_discovery_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_discovery_scheduled_execution_time ON jobfinder_queues.wolverine_queue_discovery_scheduled USING btree (execution_time);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_notifications (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_notifications_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_notifications_timestamp ON jobfinder_queues.wolverine_queue_notifications USING btree (timestamp);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_notifications_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_notifications_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_notifications_scheduled_execution_time ON jobfinder_queues.wolverine_queue_notifications_scheduled USING btree (execution_time);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_engagement (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_engagement_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_engagement_timestamp ON jobfinder_queues.wolverine_queue_engagement USING btree (timestamp);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_engagement_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_engagement_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_engagement_scheduled_execution_time ON jobfinder_queues.wolverine_queue_engagement_scheduled USING btree (execution_time);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_scoring (
    id              uuid                        NOT NULL,
    body            bytea                       NOT NULL,
    message_type    varchar                     NOT NULL,
    keep_until      timestamp with time zone    NULL,
    timestamp       timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_scoring_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_scoring_timestamp ON jobfinder_queues.wolverine_queue_scoring USING btree (timestamp);
CREATE TABLE IF NOT EXISTS jobfinder_queues.wolverine_queue_scoring_scheduled (
    id                uuid                        NOT NULL,
    body              bytea                       NOT NULL,
    message_type      varchar                     NOT NULL,
    execution_time    timestamp with time zone    NOT NULL,
    keep_until        timestamp with time zone    NULL,
    timestamp         timestamp with time zone    NULL DEFAULT ((now() at time zone 'utc')),
CONSTRAINT pkey_wolverine_queue_scoring_scheduled_id PRIMARY KEY (id)
);

CREATE INDEX idx_wolverine_queue_scoring_scheduled_execution_time ON jobfinder_queues.wolverine_queue_scoring_scheduled USING btree (execution_time);

END;
$tran$;
