-- ============================================================================
-- FEATURE: Suporte a CNPJ e CPF Alfanuméricos
-- Migration: 001 - ROLLBACK (Desfazer alterações)
-- Data: 2026-11-24
-- Descrição: Reverter todas as alterações feitas pela migration 001-ALTER-SCHEMA
--
-- CUIDADO: Este script Remove colunas e índices criados pela migration anterior
--          Dados em RPS.TOMADOR_DOCUMENTO_ALFANUMERICO serão perdidos
--          Mas dados originais em TOMADOR_CNPJ/TOMADOR_CPF são preservados
-- ============================================================================

SET ECHO ON
SET FEEDBACK ON
SET TIMING ON
SET SERVEROUTPUT ON SIZE 1000000

PROMPT
PROMPT ════════════════════════════════════════════════════════════════════════════
PROMPT FEATURE-CNPJ-ALFANUMERICO: ROLLBACK da Alteração de Schema (Phase 2)
PROMPT ════════════════════════════════════════════════════════════════════════════
PROMPT

-- ============================================================================
-- BLOCO 1: Remover índices criados
-- ============================================================================

DECLARE
  v_idx_exists INTEGER;
BEGIN
  DBMS_OUTPUT.PUT_LINE('PASSO 1: Removendo índices criados...');
  DBMS_OUTPUT.PUT_LINE('');

  SELECT COUNT(*) INTO v_idx_exists
  FROM user_indexes
  WHERE index_name = 'IX_RPS_TOMADOR_ALFANUMERICO';

  IF v_idx_exists > 0 THEN
    EXECUTE IMMEDIATE 'DROP INDEX IX_RPS_TOMADOR_ALFANUMERICO';
    DBMS_OUTPUT.PUT_LINE('  ✓ Índice IX_RPS_TOMADOR_ALFANUMERICO removido');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ⚠ Índice IX_RPS_TOMADOR_ALFANUMERICO não existe');
  END IF;

  DBMS_OUTPUT.PUT_LINE('');

EXCEPTION
  WHEN OTHERS THEN
    DBMS_OUTPUT.PUT_LINE('  ✗ Erro: ' || SQLERRM);
    RAISE;
END;
/

-- ============================================================================
-- BLOCO 2: Remover coluna TOMADOR_DOCUMENTO_ALFANUMERICO de RPS
-- ============================================================================

DECLARE
  v_col_exists INTEGER;
BEGIN
  DBMS_OUTPUT.PUT_LINE('PASSO 2: Removendo coluna TOMADOR_DOCUMENTO_ALFANUMERICO de RPS...');
  DBMS_OUTPUT.PUT_LINE('');

  SELECT COUNT(*) INTO v_col_exists
  FROM user_tab_columns
  WHERE table_name = 'RPS' AND column_name = 'TOMADOR_DOCUMENTO_ALFANUMERICO';

  IF v_col_exists > 0 THEN
    EXECUTE IMMEDIATE 'ALTER TABLE RPS DROP COLUMN TOMADOR_DOCUMENTO_ALFANUMERICO';
    DBMS_OUTPUT.PUT_LINE('  ✓ Coluna TOMADOR_DOCUMENTO_ALFANUMERICO removida de RPS');
    DBMS_OUTPUT.PUT_LINE('    AVISO: Dados nesta coluna foram perdidos');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ⚠ Coluna TOMADOR_DOCUMENTO_ALFANUMERICO não existe em RPS');
  END IF;

  DBMS_OUTPUT.PUT_LINE('');

EXCEPTION
  WHEN OTHERS THEN
    DBMS_OUTPUT.PUT_LINE('  ✗ Erro: ' || SQLERRM);
    RAISE;
END;
/

-- ============================================================================
-- BLOCO 3: Remover coluna DT_MIGRACAO_ALFANUMERICO de RPS
-- ============================================================================

DECLARE
  v_col_exists INTEGER;
BEGIN
  DBMS_OUTPUT.PUT_LINE('PASSO 3: Removendo coluna DT_MIGRACAO_ALFANUMERICO de RPS...');
  DBMS_OUTPUT.PUT_LINE('');

  SELECT COUNT(*) INTO v_col_exists
  FROM user_tab_columns
  WHERE table_name = 'RPS' AND column_name = 'DT_MIGRACAO_ALFANUMERICO';

  IF v_col_exists > 0 THEN
    EXECUTE IMMEDIATE 'ALTER TABLE RPS DROP COLUMN DT_MIGRACAO_ALFANUMERICO';
    DBMS_OUTPUT.PUT_LINE('  ✓ Coluna DT_MIGRACAO_ALFANUMERICO removida de RPS');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ⚠ Coluna DT_MIGRACAO_ALFANUMERICO não existe em RPS');
  END IF;

  DBMS_OUTPUT.PUT_LINE('');

EXCEPTION
  WHEN OTHERS THEN
    DBMS_OUTPUT.PUT_LINE('  ✗ Erro: ' || SQLERRM);
    RAISE;
END;
/

-- ============================================================================
-- BLOCO 4: Remover coluna TIPO de CONTRIBUINTES
-- ============================================================================

DECLARE
  v_col_exists INTEGER;
BEGIN
  DBMS_OUTPUT.PUT_LINE('PASSO 4: Removendo coluna TIPO de CONTRIBUINTES...');
  DBMS_OUTPUT.PUT_LINE('');

  SELECT COUNT(*) INTO v_col_exists
  FROM user_tab_columns
  WHERE table_name = 'CONTRIBUINTES' AND column_name = 'TIPO';

  IF v_col_exists > 0 THEN
    EXECUTE IMMEDIATE 'ALTER TABLE CONTRIBUINTES DROP COLUMN TIPO';
    DBMS_OUTPUT.PUT_LINE('  ✓ Coluna TIPO removida de CONTRIBUINTES');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ⚠ Coluna TIPO não existe em CONTRIBUINTES');
  END IF;

  DBMS_OUTPUT.PUT_LINE('');

EXCEPTION
  WHEN OTHERS THEN
    DBMS_OUTPUT.PUT_LINE('  ✗ Erro: ' || SQLERRM);
    RAISE;
END;
/

-- ============================================================================
-- RESUMO FINAL
-- ============================================================================

DECLARE
  v_tipo_exists INTEGER;
  v_alfanumerico_exists INTEGER;
  v_index_exists INTEGER;
BEGIN
  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('VALIDAÇÃO FINAL - VERIFICANDO ROLLBACK');
  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('');

  -- Verificar TIPO em CONTRIBUINTES
  SELECT COUNT(*) INTO v_tipo_exists
  FROM user_tab_columns
  WHERE table_name = 'CONTRIBUINTES' AND column_name = 'TIPO';

  IF v_tipo_exists = 0 THEN
    DBMS_OUTPUT.PUT_LINE('  ✓ CONTRIBUINTES.TIPO: Coluna removida com sucesso');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ✗ CONTRIBUINTES.TIPO: Coluna ainda existe - verifique erros acima');
  END IF;

  -- Verificar TOMADOR_DOCUMENTO_ALFANUMERICO em RPS
  SELECT COUNT(*) INTO v_alfanumerico_exists
  FROM user_tab_columns
  WHERE table_name = 'RPS' AND column_name = 'TOMADOR_DOCUMENTO_ALFANUMERICO';

  IF v_alfanumerico_exists = 0 THEN
    DBMS_OUTPUT.PUT_LINE('  ✓ RPS.TOMADOR_DOCUMENTO_ALFANUMERICO: Coluna removida com sucesso');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ✗ RPS.TOMADOR_DOCUMENTO_ALFANUMERICO: Coluna ainda existe - verifique erros acima');
  END IF;

  -- Verificar índice
  SELECT COUNT(*) INTO v_index_exists
  FROM user_indexes
  WHERE index_name = 'IX_RPS_TOMADOR_ALFANUMERICO';

  IF v_index_exists = 0 THEN
    DBMS_OUTPUT.PUT_LINE('  ✓ IX_RPS_TOMADOR_ALFANUMERICO: Índice removido com sucesso');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ⚠ IX_RPS_TOMADOR_ALFANUMERICO: Índice ainda existe (opcional)');
  END IF;

  DBMS_OUTPUT.PUT_LINE('');

  IF v_tipo_exists = 0 AND v_alfanumerico_exists = 0 AND v_index_exists = 0 THEN
    DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
    DBMS_OUTPUT.PUT_LINE('✓ ROLLBACK FEATURE-CNPJ-ALFANUMERICO-001 COMPLETADO COM SUCESSO!');
    DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
    DBMS_OUTPUT.PUT_LINE('');
    DBMS_OUTPUT.PUT_LINE('Schema restaurado ao estado anterior à migration.');
    DBMS_OUTPUT.PUT_LINE('Dados em TOMADOR_CNPJ e TOMADOR_CPF foram preservados.');
  ELSE
    DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
    DBMS_OUTPUT.PUT_LINE('⚠ ROLLBACK COMPLETADO COM ERROS - Verifique acima');
    DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  END IF;

  DBMS_OUTPUT.PUT_LINE('');

END;
/

PROMPT
PROMPT Para reaplicar esta migration, execute:
PROMPT   @db/migrations/FEATURE-CNPJ-ALFANUMERICO-001-ALTER-SCHEMA.sql
PROMPT

SET ECHO OFF
