SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE

-- FIX CH-05: Remover gatilho que causava ORA-04091
-- Problema: Gatilho AFTER UPDATE chamava PKG_GUIA.recalcular_guia() enquanto NFSE estava mutante
-- Solução: Chamar recalcular_guia() explicitamente após UPDATE + COMMIT no código da aplicação
-- Benefício adicional: Permite transações que cancelam múltiplas notas antes de recalcular guia

SHOW ERRORS
EXIT
