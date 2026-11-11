-- V0002: UNEXECUTED REVIEW ARTIFACT. Separate execution authorization is required.
-- Verify the target table, nullable bigint column, schema, and ownership before execution.
-- Under a writer maintenance barrier, preflight duplicate non-NULL IDs; this index
-- also fails transactionally if duplicates remain. Do not clean up data automatically.
-- Use the repository's single-transaction migration policy; do not use CONCURRENTLY.

CREATE UNIQUE INDEX uq_virtuous_gift_processing_aplos_transaction_id
    ON public.virtuous_gift_processing (aplos_transaction_id)
    WHERE aplos_transaction_id IS NOT NULL;
