-- Rollback: CH-05 Fix - Restore PRAGMA AUTONOMOUS_TRANSACTION (if needed)
-- Propósito: Reverter correção do trigger (rollback em caso de problemas)
-- Data: 2026-11-23
-- Ticket: CH-05

SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE

-- ============================================================================
-- DROP trigger corrigido
-- ============================================================================
DROP TRIGGER TRG_NFSE_SITUACAO_GUIA;
/

-- ============================================================================
-- Restaurar trigger ORIGINAL (com PRAGMA AUTONOMOUS_TRANSACTION)
-- ============================================================================
-- Nota: Esta versão possui o bug que causa race condition
-- Use apenas para rollback em emergência

CREATE OR REPLACE TRIGGER TRG_NFSE_SITUACAO_GUIA
AFTER UPDATE OF SITUACAO ON NFSE
FOR EACH ROW
DECLARE
    PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
    PKG_GUIA.recalcular_guia(:NEW.PRESTADOR_ID, :NEW.COMPETENCIA);
    COMMIT;
END;
/

SHOW ERRORS
EXIT
