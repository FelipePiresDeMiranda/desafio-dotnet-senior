-- Migration: Fix CH-05 - Remove PRAGMA AUTONOMOUS_TRANSACTION from trigger
-- Propósito: Corrigir race condition que causa guia não recalcular após cancelamento de NFSE
-- Data: 2026-11-23
-- Ticket: CH-05 "Cancelei a nota e a guia continua cobrando"

SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE

-- ============================================================================
-- DROP trigger para permitir recriação
-- ============================================================================
DROP TRIGGER TRG_NFSE_SITUACAO_GUIA;
/

-- ============================================================================
-- Criar trigger CORRIGIDO (sem PRAGMA AUTONOMOUS_TRANSACTION)
-- ============================================================================
-- Problema anterior: PRAGMA AUTONOMOUS_TRANSACTION causava race condition
--   - Trigger executava em sessão separada
--   - Lia dados inconsistentes do UPDATE da NFSE
--   - Recalculava guia com valores antigos
--   - Resultado: Guia não refletia cancelamento até próxima emissão

-- Solução: Remover PRAGMA AUTONOMOUS_TRANSACTION
--   - Trigger agora executa na mesma transação que UPDATE da NFSE
--   - Garante atomicidade: UPDATE + RECALCULAR são indivisíveis
--   - Dados sempre consistentes

CREATE OR REPLACE TRIGGER TRG_NFSE_SITUACAO_GUIA
AFTER UPDATE OF SITUACAO ON NFSE
FOR EACH ROW
BEGIN
    -- FIX CH-05: Sem PRAGMA AUTONOMOUS_TRANSACTION
    -- Trigger executa na mesma transação que UPDATE da NFSE
    PKG_GUIA.recalcular_guia(:NEW.PRESTADOR_ID, :NEW.COMPETENCIA);
END;
/

SHOW ERRORS
EXIT
