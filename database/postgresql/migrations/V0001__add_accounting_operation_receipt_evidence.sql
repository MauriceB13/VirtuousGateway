-- V0001: UNEXECUTED REVIEW ARTIFACT. Separate execution authorization is required.
-- Baseline: PostgreSQL 17; namespace: public; UTF-8 without BOM; LF endings.
-- Prerequisite: public.virtuous_gift_processing with its existing gift_id PK.
-- An eventual explicit executor must apply this entire file in one transaction.
-- No bootstrap, backfill, runtime registration, permissions, or posting is enabled.
-- Deliberately no IF NOT EXISTS: unexpected existing objects must fail closed.

CREATE TABLE public.accounting_operations
(
    operation_id uuid NOT NULL,
    gift_id bigint NOT NULL,
    connector_identity text COLLATE "C" NOT NULL,
    destination_reference text COLLATE "C" NOT NULL,
    source_system text COLLATE "C" NOT NULL,
    source_organization_reference text COLLATE "C" NOT NULL,
    source_record_id text COLLATE "C" NOT NULL,
    operation_type smallint NOT NULL,
    created_at_utc timestamp(6) with time zone NOT NULL,
    origin text COLLATE "C" NOT NULL,
    source_fingerprint text COLLATE "C" NULL,

    CONSTRAINT pk_accounting_operations PRIMARY KEY (operation_id),
    CONSTRAINT uq_accounting_operations_gift UNIQUE (gift_id),
    CONSTRAINT uq_accounting_operations_binding
        UNIQUE (operation_id, connector_identity, destination_reference),
    CONSTRAINT fk_accounting_operations_gift FOREIGN KEY (gift_id)
        REFERENCES public.virtuous_gift_processing (gift_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    CONSTRAINT ck_accounting_operations_uuid
        CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_accounting_operations_initial_scope
        CHECK (connector_identity = 'aplos' AND source_system = 'virtuous'
            AND operation_type = 1 AND gift_id > 0),
    -- Exact equality to a positive bigint's text enforces canonical source digits,
    -- Int64 range, and gift/source correspondence without casting untrusted text.
    CONSTRAINT ck_accounting_operations_source_record
        CHECK (source_record_id = gift_id::text),
    CONSTRAINT ck_accounting_operations_destination
        CHECK (pg_catalog.char_length(destination_reference) BETWEEN 1 AND 256
            AND destination_reference !~ '[^!-~]'),
    CONSTRAINT ck_accounting_operations_source_org
        CHECK (pg_catalog.char_length(source_organization_reference) BETWEEN 1 AND 256
            AND source_organization_reference !~ '[^!-~]'),
    CONSTRAINT ck_accounting_operations_origin
        CHECK (origin IN ('new_claim', 'legacy_backfill')),
    CONSTRAINT ck_accounting_operations_created
        CHECK (pg_catalog.isfinite(created_at_utc)
            AND created_at_utc >= TIMESTAMPTZ '0001-01-01 00:00:00.000001+00'
            AND created_at_utc <= TIMESTAMPTZ '9999-12-31 23:59:59.999999+00')
);

CREATE TABLE public.accounting_receipts
(
    receipt_id uuid NOT NULL,
    operation_id uuid NOT NULL,
    schema_version integer NOT NULL,
    producing_attempt_id uuid NOT NULL,
    connector_identity text COLLATE "C" NOT NULL,
    destination_reference text COLLATE "C" NOT NULL,
    external_entity_type text COLLATE "C" NOT NULL,
    external_transaction_id text COLLATE "C" NOT NULL,
    observed_at_utc timestamp(6) with time zone NULL,
    recorded_at_utc timestamp(6) with time zone NOT NULL,
    provenance smallint NOT NULL,
    evidence_reference text COLLATE "C" NULL,
    verified_by_reference text COLLATE "C" NULL,

    CONSTRAINT pk_accounting_receipts PRIMARY KEY (receipt_id),
    CONSTRAINT uq_accounting_receipts_operation UNIQUE (operation_id),
    CONSTRAINT uq_accounting_receipts_external
        UNIQUE (connector_identity, destination_reference, external_entity_type, external_transaction_id),
    CONSTRAINT fk_accounting_receipts_operation FOREIGN KEY (operation_id)
        REFERENCES public.accounting_operations (operation_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    CONSTRAINT fk_accounting_receipts_binding
        FOREIGN KEY (operation_id, connector_identity, destination_reference)
        REFERENCES public.accounting_operations (operation_id, connector_identity, destination_reference)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    CONSTRAINT ck_accounting_receipts_uuids
        CHECK (receipt_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND operation_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND producing_attempt_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND producing_attempt_id <> operation_id),
    CONSTRAINT ck_accounting_receipts_version CHECK (schema_version = 1),
    CONSTRAINT ck_accounting_receipts_initial_scope
        CHECK (connector_identity = 'aplos' AND external_entity_type = 'transaction'),
    CONSTRAINT ck_accounting_receipts_destination
        CHECK (pg_catalog.char_length(destination_reference) BETWEEN 1 AND 256
            AND destination_reference !~ '[^!-~]'),
    -- Text remains provider-neutral in type. Initial Aplos eligibility alone is
    -- numeric. Length and C-collation lexical comparison avoid overflow/cast errors.
    CONSTRAINT ck_accounting_receipts_aplos_id
        CHECK (pg_catalog.char_length(external_transaction_id) BETWEEN 1 AND 19
            AND external_transaction_id ~ '^[1-9][0-9]*$'
            AND external_transaction_id !~ '[^0-9]'
            AND (pg_catalog.char_length(external_transaction_id) < 19
                OR external_transaction_id <= '9223372036854775807')),
    CONSTRAINT ck_accounting_receipts_evidence_reference
        CHECK (evidence_reference IS NULL
            OR (pg_catalog.char_length(evidence_reference) BETWEEN 1 AND 256
                AND evidence_reference !~ '[^!-~]')),
    CONSTRAINT ck_accounting_receipts_verifier_reference
        CHECK (verified_by_reference IS NULL
            OR (pg_catalog.char_length(verified_by_reference) BETWEEN 1 AND 256
                AND verified_by_reference !~ '[^!-~]')),
    CONSTRAINT ck_accounting_receipts_recorded
        CHECK (pg_catalog.isfinite(recorded_at_utc)
            AND recorded_at_utc >= TIMESTAMPTZ '0001-01-01 00:00:00.000001+00'
            AND recorded_at_utc <= TIMESTAMPTZ '9999-12-31 23:59:59.999999+00'),
    CONSTRAINT ck_accounting_receipts_observed
        CHECK (observed_at_utc IS NULL
            OR (pg_catalog.isfinite(observed_at_utc)
                AND observed_at_utc >= TIMESTAMPTZ '0001-01-01 00:00:00.000001+00'
                AND observed_at_utc <= TIMESTAMPTZ '9999-12-31 23:59:59.999999+00'
                AND observed_at_utc <= recorded_at_utc)),
    CONSTRAINT ck_accounting_receipts_provenance
        CHECK (
            (provenance = 1 AND observed_at_utc IS NOT NULL AND verified_by_reference IS NULL)
            OR (provenance = 2 AND observed_at_utc IS NOT NULL
                AND evidence_reference IS NOT NULL AND verified_by_reference IS NOT NULL)
            OR (provenance = 3 AND observed_at_utc IS NULL
                AND evidence_reference IS NOT NULL AND verified_by_reference IS NULL)
        )
);

CREATE TABLE public.accounting_operation_evidence
(
    event_id uuid NOT NULL,
    operation_id uuid NOT NULL,
    attempt_id uuid NULL,
    event_version integer NOT NULL,
    event_kind text COLLATE "C" NOT NULL,
    reason_code text COLLATE "C" NULL,
    candidate_external_entity_type text COLLATE "C" NULL,
    candidate_external_id text COLLATE "C" NULL,
    recorded_at_utc timestamp(6) with time zone NOT NULL,
    evidence_reference text COLLATE "C" NULL,
    verified_by_reference text COLLATE "C" NULL,

    CONSTRAINT pk_accounting_operation_evidence PRIMARY KEY (event_id),
    -- PostgreSQL 17 NULLS NOT DISTINCT closes the null-attempt backfill loophole.
    CONSTRAINT uq_accounting_evidence_event_slot
        UNIQUE NULLS NOT DISTINCT (operation_id, attempt_id, event_kind),
    CONSTRAINT fk_accounting_evidence_operation FOREIGN KEY (operation_id)
        REFERENCES public.accounting_operations (operation_id)
        ON UPDATE RESTRICT ON DELETE RESTRICT,
    CONSTRAINT ck_accounting_evidence_uuids
        CHECK (event_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND operation_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND (attempt_id IS NULL
                OR (attempt_id <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND attempt_id <> operation_id))),
    CONSTRAINT ck_accounting_evidence_version CHECK (event_version = 1),
    -- confirmed_rejection deliberately has no admission or reason-code branch.
    CONSTRAINT ck_accounting_evidence_kind
        CHECK (event_kind IN ('identity_backfilled', 'confirmed_success',
            'definitely_not_dispatched', 'outcome_unknown', 'reconciliation_required',
            'manual_confirmed_posted', 'manual_confirmed_not_posted', 'legacy_projected')),
    CONSTRAINT ck_accounting_evidence_attempt
        CHECK ((event_kind = 'identity_backfilled' AND attempt_id IS NULL)
            OR (event_kind <> 'identity_backfilled' AND attempt_id IS NOT NULL)),
    -- Required reason branches use explicit IS NOT NULL: CHECK must not pass UNKNOWN.
    CONSTRAINT ck_accounting_evidence_reason
        CHECK (
            (event_kind = 'confirmed_success' AND reason_code IS NULL)
            OR (reason_code IS NOT NULL AND (
                (event_kind = 'identity_backfilled' AND reason_code = 'LegacyIdentityAssigned')
                OR (event_kind = 'definitely_not_dispatched' AND reason_code IN (
                    'CanceledBeforeDispatch', 'UnsupportedOrInvalidInput', 'MappingFailed', 'NotDispatched'))
                OR (event_kind = 'outcome_unknown' AND reason_code IN (
                    'HttpResponseOutcomeUnknown', 'DispatchOutcomeUnknown', 'UnexpectedPostingFailure',
                    'UnconfirmedResponse', 'MissingConnectorResult', 'OperationIdentityMismatch', 'ReceiptValidationFailed'))
                OR (event_kind = 'reconciliation_required' AND reason_code IN (
                    'OrphanedProcessingAttempt', 'LegacyFailureOutcomeUnverified', 'CompletionPersistenceUncertain'))
                OR (event_kind = 'manual_confirmed_posted' AND reason_code = 'VerifiedPosted')
                OR (event_kind = 'manual_confirmed_not_posted' AND reason_code = 'VerifiedNotPosted')
                OR (event_kind = 'legacy_projected' AND reason_code = 'LegacyCompletedProjected')
            ))
        ),
    CONSTRAINT ck_accounting_evidence_candidates
        CHECK ((candidate_external_entity_type IS NULL AND candidate_external_id IS NULL)
            OR (candidate_external_entity_type IS NOT NULL AND candidate_external_id IS NOT NULL
                AND event_kind IN ('outcome_unknown', 'reconciliation_required'))),
    CONSTRAINT ck_accounting_evidence_candidate_type
        CHECK (candidate_external_entity_type IS NULL
            OR (pg_catalog.char_length(candidate_external_entity_type) BETWEEN 1 AND 256
                AND candidate_external_entity_type !~ '[^!-~]')),
    CONSTRAINT ck_accounting_evidence_candidate_id
        CHECK (candidate_external_id IS NULL
            OR (pg_catalog.char_length(candidate_external_id) BETWEEN 1 AND 256
                AND candidate_external_id !~ '[^!-~]')),
    CONSTRAINT ck_accounting_evidence_reference
        CHECK (evidence_reference IS NULL
            OR (pg_catalog.char_length(evidence_reference) BETWEEN 1 AND 256
                AND evidence_reference !~ '[^!-~]')),
    CONSTRAINT ck_accounting_evidence_verifier_reference
        CHECK (verified_by_reference IS NULL
            OR (pg_catalog.char_length(verified_by_reference) BETWEEN 1 AND 256
                AND verified_by_reference !~ '[^!-~]')),
    CONSTRAINT ck_accounting_evidence_required_reference
        CHECK (event_kind NOT IN ('identity_backfilled', 'reconciliation_required',
                'manual_confirmed_posted', 'manual_confirmed_not_posted', 'legacy_projected')
            OR evidence_reference IS NOT NULL),
    CONSTRAINT ck_accounting_evidence_verifier
        CHECK ((event_kind IN ('manual_confirmed_posted', 'manual_confirmed_not_posted')
                AND verified_by_reference IS NOT NULL)
            OR (event_kind NOT IN ('manual_confirmed_posted', 'manual_confirmed_not_posted')
                AND verified_by_reference IS NULL)),
    CONSTRAINT ck_accounting_evidence_recorded
        CHECK (pg_catalog.isfinite(recorded_at_utc)
            AND recorded_at_utc >= TIMESTAMPTZ '0001-01-01 00:00:00.000001+00'
            AND recorded_at_utc <= TIMESTAMPTZ '9999-12-31 23:59:59.999999+00')
);

-- One initial disposition per attempt; an orphan recovery occupies the same slot.
CREATE UNIQUE INDEX uq_accounting_evidence_disposition
    ON public.accounting_operation_evidence (operation_id, attempt_id)
    WHERE event_kind IN ('confirmed_success', 'definitely_not_dispatched',
        'outcome_unknown', 'reconciliation_required');

-- Unknown/recovery may be followed by one manual resolution, never both resolutions.
CREATE UNIQUE INDEX uq_accounting_evidence_manual_resolution
    ON public.accounting_operation_evidence (operation_id, attempt_id)
    WHERE event_kind IN ('manual_confirmed_posted', 'manual_confirmed_not_posted');

-- Direct/manual/projected success cannot each assert a different canonical receipt.
CREATE UNIQUE INDEX uq_accounting_evidence_success
    ON public.accounting_operation_evidence (operation_id)
    WHERE event_kind IN ('confirmed_success', 'manual_confirmed_posted', 'legacy_projected');

CREATE INDEX ix_accounting_evidence_operation_recorded
    ON public.accounting_operation_evidence (operation_id, recorded_at_utc, event_id);

-- These constraints do NOT enforce ledger state, actual account binding, producing
-- attempt ownership, authenticated verifier identity, append-only permissions,
-- or complete cross-event transition ordering. No write path is activated here.
