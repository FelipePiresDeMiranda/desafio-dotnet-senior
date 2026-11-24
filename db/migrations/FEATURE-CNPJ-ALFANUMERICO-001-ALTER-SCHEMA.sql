-- ============================================================================
-- FEATURE: Suporte a CNPJ e CPF Alfanuméricos
-- Migration: 001 - Forward (Aplicar em Produção)
-- Data: 2026-11-24
-- Descrição: Alterar schema para permitir armazenamento de CNPJ alfanuméricos
--            conforme IN RFB nº 2.229/2024 (desde julho/2026)
--
-- Requisitos:
--   • Oracle Database 19c (producao) ou 11g+ (desenvolvimento)
--   • Sistema pode estar em operação (com impacto mínimo)
--   • Tabelas CONTRIBUINTES, RPS, NFSE existentes
--   • Sem perda de dados (apenas alteração de coluna)
-- ============================================================================

SET ECHO ON
SET FEEDBACK ON
SET TIMING ON
SET SERVEROUTPUT ON SIZE 1000000

PROMPT
PROMPT ════════════════════════════════════════════════════════════════════════════
PROMPT FEATURE-CNPJ-ALFANUMERICO: Alteração de Schema (Phase 2)
PROMPT ════════════════════════════════════════════════════════════════════════════
PROMPT

-- ============================================================================
-- BLOCO 1: Adicionar coluna TIPO em CONTRIBUINTES
-- ============================================================================

DECLARE
  v_col_exists INTEGER;
BEGIN
  DBMS_OUTPUT.PUT_LINE('PASSO 1: Adicionando coluna TIPO em CONTRIBUINTES...');
  DBMS_OUTPUT.PUT_LINE('');

  SELECT COUNT(*) INTO v_col_exists FROM user_tab_columns
  WHERE table_name = 'CONTRIBUINTES' AND column_name = 'TIPO';

  IF v_col_exists = 0 THEN
    EXECUTE IMMEDIATE 'ALTER TABLE CONTRIBUINTES ADD (TIPO NUMBER(1) DEFAULT 1)';
    DBMS_OUTPUT.PUT_LINE('  ✓ Coluna TIPO adicionada em CONTRIBUINTES');
    DBMS_OUTPUT.PUT_LINE('    Tipo: NUMBER(1)');
    DBMS_OUTPUT.PUT_LINE('    Default: 1');
    DBMS_OUTPUT.PUT_LINE('    Nulável: Sim');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ⚠ Coluna TIPO já existe em CONTRIBUINTES');
    DBMS_OUTPUT.PUT_LINE('    (Migration pode ter sido executada anteriormente)');
  END IF;

  DBMS_OUTPUT.PUT_LINE('');

EXCEPTION
  WHEN OTHERS THEN
    DBMS_OUTPUT.PUT_LINE('  ✗ Erro: ' || SQLERRM);
    RAISE;
END;
/

-- ============================================================================
-- BLOCO 2: Adicionar coluna TOMADOR_DOCUMENTO_ALFANUMERICO em RPS
-- ============================================================================

DECLARE
  v_col_exists INTEGER;
  v_rows_updated INTEGER;
BEGIN
  DBMS_OUTPUT.PUT_LINE('PASSO 2: Adicionando suporte alfanumérico em RPS...');
  DBMS_OUTPUT.PUT_LINE('');

  SELECT COUNT(*) INTO v_col_exists FROM user_tab_columns
  WHERE table_name = 'RPS' AND column_name = 'TOMADOR_DOCUMENTO_ALFANUMERICO';

  IF v_col_exists = 0 THEN
    -- Adicionar nova coluna com dados de TOMADOR_CNPJ ou TOMADOR_CPF
    EXECUTE IMMEDIATE '
    ALTER TABLE RPS ADD (
      TOMADOR_DOCUMENTO_ALFANUMERICO VARCHAR2(14),
      DT_MIGRACAO_ALFANUMERICO DATE
    )';

    DBMS_OUTPUT.PUT_LINE('  ✓ Coluna TOMADOR_DOCUMENTO_ALFANUMERICO adicionada em RPS');
    DBMS_OUTPUT.PUT_LINE('    Tipo: VARCHAR2(14)');
    DBMS_OUTPUT.PUT_LINE('    Nulável: Sim');
    DBMS_OUTPUT.PUT_LINE('');

    -- Preencher com dados existentes (formato numérico - CNPJ)
    DBMS_OUTPUT.PUT_LINE('  Migrando dados de TOMADOR_CNPJ...');
    EXECUTE IMMEDIATE '
    UPDATE RPS SET 
      TOMADOR_DOCUMENTO_ALFANUMERICO = LPAD(TOMADOR_CNPJ, 14, ''0'')
    WHERE TOMADOR_CNPJ IS NOT NULL
    AND TOMADOR_DOCUMENTO_ALFANUMERICO IS NULL';

    v_rows_updated := SQL%ROWCOUNT;
    DBMS_OUTPUT.PUT_LINE('    Atualizado: ' || v_rows_updated || ' registros (CNPJ)');

    -- Se CNPJ null, tentar CPF
    DBMS_OUTPUT.PUT_LINE('  Migrando dados de TOMADOR_CPF...');
    EXECUTE IMMEDIATE '
    UPDATE RPS SET 
      TOMADOR_DOCUMENTO_ALFANUMERICO = LPAD(TOMADOR_CPF, 14, ''0'')
    WHERE TOMADOR_CNPJ IS NULL
    AND TOMADOR_CPF IS NOT NULL
    AND TOMADOR_DOCUMENTO_ALFANUMERICO IS NULL';

    v_rows_updated := SQL%ROWCOUNT;
    DBMS_OUTPUT.PUT_LINE('    Atualizado: ' || v_rows_updated || ' registros (CPF)');

    COMMIT;
    DBMS_OUTPUT.PUT_LINE('');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ⚠ Coluna TOMADOR_DOCUMENTO_ALFANUMERICO já existe em RPS');
    DBMS_OUTPUT.PUT_LINE('    (Migration pode ter sido executada anteriormente)');
    DBMS_OUTPUT.PUT_LINE('');
  END IF;

EXCEPTION
  WHEN OTHERS THEN
    DBMS_OUTPUT.PUT_LINE('  ✗ Erro: ' || SQLERRM);
    RAISE;
END;
/

-- ============================================================================
-- BLOCO 3: Criar índice em RPS para melhor performance
-- ============================================================================

DECLARE
  v_idx_exists INTEGER;
BEGIN
  DBMS_OUTPUT.PUT_LINE('PASSO 3: Criando índice para suportar alfanuméricos...');
  DBMS_OUTPUT.PUT_LINE('');

  SELECT COUNT(*) INTO v_idx_exists FROM user_indexes
  WHERE index_name = 'IX_RPS_TOMADOR_ALFANUMERICO';

  IF v_idx_exists = 0 THEN
    EXECUTE IMMEDIATE 'CREATE INDEX IX_RPS_TOMADOR_ALFANUMERICO ON RPS (TOMADOR_DOCUMENTO_ALFANUMERICO)';
    DBMS_OUTPUT.PUT_LINE('  ✓ Índice IX_RPS_TOMADOR_ALFANUMERICO criado');
    DBMS_OUTPUT.PUT_LINE('    Tabela: RPS');
    DBMS_OUTPUT.PUT_LINE('    Coluna: TOMADOR_DOCUMENTO_ALFANUMERICO');
    DBMS_OUTPUT.PUT_LINE('    Propósito: Otimizar consultas por documento alfanumérico');
    DBMS_OUTPUT.PUT_LINE('');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ⚠ Índice IX_RPS_TOMADOR_ALFANUMERICO já existe');
    DBMS_OUTPUT.PUT_LINE('    (Migration pode ter sido executada anteriormente)');
    DBMS_OUTPUT.PUT_LINE('');
  END IF;

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
  DBMS_OUTPUT.PUT_LINE('VALIDAÇÃO FINAL - VERIFICANDO ALTERAÇÕES');
  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('');

  -- Verificar TIPO em CONTRIBUINTES
  SELECT COUNT(*) INTO v_tipo_exists
  FROM user_tab_columns
  WHERE table_name = 'CONTRIBUINTES' AND column_name = 'TIPO';

  IF v_tipo_exists > 0 THEN
    DBMS_OUTPUT.PUT_LINE('  ✓ CONTRIBUINTES.TIPO: Coluna criada com sucesso');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ✗ CONTRIBUINTES.TIPO: NÃO foi criada - verifique erros acima');
  END IF;

  -- Verificar TOMADOR_DOCUMENTO_ALFANUMERICO em RPS
  SELECT COUNT(*) INTO v_alfanumerico_exists
  FROM user_tab_columns
  WHERE table_name = 'RPS' AND column_name = 'TOMADOR_DOCUMENTO_ALFANUMERICO';

  IF v_alfanumerico_exists > 0 THEN
    DBMS_OUTPUT.PUT_LINE('  ✓ RPS.TOMADOR_DOCUMENTO_ALFANUMERICO: Coluna criada com sucesso');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ✗ RPS.TOMADOR_DOCUMENTO_ALFANUMERICO: NÃO foi criada - verifique erros acima');
  END IF;

  -- Verificar índice
  SELECT COUNT(*) INTO v_index_exists
  FROM user_indexes
  WHERE index_name = 'IX_RPS_TOMADOR_ALFANUMERICO';

  IF v_index_exists > 0 THEN
    DBMS_OUTPUT.PUT_LINE('  ✓ IX_RPS_TOMADOR_ALFANUMERICO: Índice criado com sucesso');
  ELSE
    DBMS_OUTPUT.PUT_LINE('  ⚠ IX_RPS_TOMADOR_ALFANUMERICO: Índice NÃO foi criado (opcional)');
  END IF;

  DBMS_OUTPUT.PUT_LINE('');

  IF v_tipo_exists > 0 AND v_alfanumerico_exists > 0 AND v_index_exists > 0 THEN
    DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
    DBMS_OUTPUT.PUT_LINE('✓ MIGRATION FEATURE-CNPJ-ALFANUMERICO-001 COMPLETADA COM SUCESSO!');
    DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
    DBMS_OUTPUT.PUT_LINE('');
    DBMS_OUTPUT.PUT_LINE('Próximas fases:');
    DBMS_OUTPUT.PUT_LINE('  FEATURE-CNPJ-ALFANUMERICO-002: NfseRepositorio.ConsultarNfseServicoTomado()');
    DBMS_OUTPUT.PUT_LINE('  FEATURE-CNPJ-ALFANUMERICO-003: ProcessadorLoteRps.Processar()');
    DBMS_OUTPUT.PUT_LINE('  FEATURE-CNPJ-ALFANUMERICO-004: Testes de Integração');
  ELSE
    DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
    DBMS_OUTPUT.PUT_LINE('⚠ MIGRATION COMPLETADA COM ERROS - Verifique acima');
    DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  END IF;

  DBMS_OUTPUT.PUT_LINE('');

END;
/

PROMPT
PROMPT Para desfazer esta migration, execute:
PROMPT   @db/migrations/FEATURE-CNPJ-ALFANUMERICO-001-ROLLBACK.sql
PROMPT

SET ECHO OFF
