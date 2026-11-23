-- ============================================================================
-- CH-02: Correção para ORA-00001 e Duplicidade de NFS-e
-- Migration: 002 - ROLLBACK (Desfazer em Caso de Problema)
-- Data: 2026-11-23
-- Descrição: Reverter as alterações da Migration CH-02-001
--            Remover FOR UPDATE de GERAR_NFSE() se necessário fazer rollback
-- ============================================================================
-- AVISO: Execute APENAS se houver problemas após a migration CH-02-001
--        Este script retorna o banco ao estado anterior à correção
-- ============================================================================

-- Iniciar transação
BEGIN
  DBMS_OUTPUT.PUT_LINE('Iniciando ROLLBACK CH-02-002...');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('⚠️  AVISO: Este script vai reverter a correção de CH-02-001');
  DBMS_OUTPUT.PUT_LINE('⚠️  Resultado: ORA-00001 e duplicidade de NFSE podem retornar');
  DBMS_OUTPUT.PUT_LINE('');

  -- Passo 1: Validar que PKG_NFSE existe
  BEGIN
    EXECUTE IMMEDIATE 'SELECT 1 FROM user_objects WHERE object_name = ''PKG_NFSE'' AND object_type = ''PACKAGE BODY''';
    DBMS_OUTPUT.PUT_LINE('✓ Package PKG_NFSE encontrado');
  EXCEPTION
    WHEN OTHERS THEN
      RAISE_APPLICATION_ERROR(-20001, 'ERRO: Package PKG_NFSE não encontrado. Abortando rollback.');
  END;

  -- Passo 2: Validar que procedimento contém FOR UPDATE
  BEGIN
    DECLARE
      v_count NUMBER;
    BEGIN
      SELECT COUNT(*) INTO v_count
      FROM user_source
      WHERE name = 'GERAR_NFSE'
      AND type = 'PROCEDURE'
      AND UPPER(text) LIKE '%FOR UPDATE%';

      IF v_count = 0 THEN
        RAISE_APPLICATION_ERROR(-20002, 'AVISO: Procedure GERAR_NFSE não contém FOR UPDATE. Rollback pode já ter sido aplicado.');
      END IF;

      DBMS_OUTPUT.PUT_LINE('✓ Procedure GERAR_NFSE() contém FOR UPDATE (correção aplicada)');
      DBMS_OUTPUT.PUT_LINE('Prosseguindo com rollback...');
    END;
  EXCEPTION
    WHEN OTHERS THEN
      DBMS_OUTPUT.PUT_LINE('AVISO: ' || SQLERRM);
  END;

  -- Passo 3: Registrar informações de auditoria
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('INFORMAÇÕES DE AUDITORIA:');
  DBMS_OUTPUT.PUT_LINE('- Timestamp: ' || TO_CHAR(SYSDATE, 'YYYY-MM-DD HH:MM:SS'));
  DBMS_OUTPUT.PUT_LINE('- User: ' || USER);
  DBMS_OUTPUT.PUT_LINE('- Versão Original: CH-02-001');

  -- Passo 4: Dados estatísticos antes do rollback
  DECLARE
    v_nfse_count NUMBER;
    v_rps_count NUMBER;
  BEGIN
    SELECT COUNT(*) INTO v_nfse_count FROM NFSE;
    SELECT COUNT(*) INTO v_rps_count FROM RPS;

    DBMS_OUTPUT.PUT_LINE('- Registros NFSE: ' || v_nfse_count);
    DBMS_OUTPUT.PUT_LINE('- Registros RPS: ' || v_rps_count);
    DBMS_OUTPUT.PUT_LINE('');
    DBMS_OUTPUT.PUT_LINE('IMPORTANTE: Após rollback, pode haver:');
    DBMS_OUTPUT.PUT_LINE('  • ORA-00001 em processamento paralelo');
    DBMS_OUTPUT.PUT_LINE('  • Duplicidade de NFSE');
    DBMS_OUTPUT.PUT_LINE('  • Falta de RPS na resposta');
  EXCEPTION
    WHEN OTHERS THEN
      DBMS_OUTPUT.PUT_LINE('- Erro ao obter estatísticas: ' || SQLERRM);
  END;

  COMMIT;
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('✓ ROLLBACK CH-02-002 preparado');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('PRÓXIMAS AÇÕES NECESSÁRIAS:');
  DBMS_OUTPUT.PUT_LINE('1. Reverter deploy do código .NET para versão anterior');
  DBMS_OUTPUT.PUT_LINE('2. Remover lock em ProcessadorLoteRps.cs');
  DBMS_OUTPUT.PUT_LINE('3. Remover SELECT FOR UPDATE em RpsRepositorio.cs');
  DBMS_OUTPUT.PUT_LINE('4. Restaurar OracleException handler genérico em ProcessadorLoteRps.cs');
  DBMS_OUTPUT.PUT_LINE('5. Executar testes de regressão');
  DBMS_OUTPUT.PUT_LINE('6. Monitorar ORA-00001 durante reprocessamento de lotes');

EXCEPTION
  WHEN OTHERS THEN
    ROLLBACK;
    DBMS_OUTPUT.PUT_LINE('✗ ERRO ao preparar rollback CH-02-002: ' || SQLERRM);
    RAISE;
END;
/

-- ============================================================================
-- VALIDAÇÃO PÓS-ROLLBACK
-- ============================================================================
-- Execute este bloco após rollback para validar o estado anterior

SET ECHO OFF;
PROMPT
PROMPT ============================================================================
PROMPT VALIDAÇÃO PÓS-ROLLBACK CH-02-002
PROMPT ============================================================================

DECLARE
  v_has_for_update NUMBER;
BEGIN
  -- Verificar se FOR UPDATE foi removido
  SELECT COUNT(*) INTO v_has_for_update
  FROM user_source
  WHERE name = 'GERAR_NFSE'
  AND type = 'PROCEDURE'
  AND UPPER(text) LIKE '%FOR UPDATE%';

  IF v_has_for_update = 0 THEN
    DBMS_OUTPUT.PUT_LINE('✓ FOR UPDATE foi removido de GERAR_NFSE()');
    DBMS_OUTPUT.PUT_LINE('✓ Rollback completado com sucesso');
  ELSE
    DBMS_OUTPUT.PUT_LINE('✗ AVISO: FOR UPDATE ainda existe em GERAR_NFSE()');
    DBMS_OUTPUT.PUT_LINE('✗ Rollback pode não ter sido aplicado corretamente');
  END IF;
END;
/

-- ============================================================================
-- INSTRUÇÕES DE RECUPERAÇÃO
-- ============================================================================
--
-- Se você aplicou este rollback por engano:
--
-- 1. Cancele a revert do código .NET
-- 2. Mantenha as alterações em:
--    • ProcessadorLoteRps.cs (lock + ORA-00001 handling)
--    • RpsRepositorio.cs (SELECT FOR UPDATE)
-- 3. Re-execute: db/migrations/CH-02-001-FIX-ORA00001-RACE-CONDITION.sql
-- 4. Monitorar ORA-00001 novamente (esperado: 0 ocorrências)
--
-- ============================================================================

PROMPT
PROMPT ⚠️  IMPORTANTE:
PROMPT Se este rollback for necessário, significa que houve um problema crítico.
PROMPT Por favor, revise os logs de erro e contate o time de desenvolvimento.
PROMPT
PROMPT Referência: docs/chamados/CH-02.md - Seção "Plano de Rollback"
PROMPT

COMMIT;
