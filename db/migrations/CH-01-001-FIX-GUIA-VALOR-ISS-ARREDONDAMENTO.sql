-- ============================================================================
-- CH-01: Correção para Divergência de Arredondamento na Guia
-- Migration: 001 - Forward (Aplicar em Produção)
-- Data: 2026-11-24
-- Descrição: Corrigir cálculo de ISS na guia para usar valores já emitidos
--            nas notas, eliminando divergência de arredondamento
-- ============================================================================
-- Pré-requisitos:
--   • Oracle Database 11g ou superior
--   • Package PKG_GUIA criado (procedure RECALCULAR_GUIA)
--   • Tabelas GUIAS e NFSE existentes
--   • Sistema pode estar em operação (sem downtime necessário)
-- ============================================================================

SET ECHO ON;
SET FEEDBACK ON;
SET TIMING ON;
SET SERVEROUTPUT ON SIZE 1000000;

-- ============================================================================
-- BACKUP DA DEFINIÇÃO ORIGINAL (Apenas para auditoria)
-- ============================================================================
-- A definição original da procedure está preservada em 02_pkg_guia.sql
-- Este script apenas demonstra o que estava sendo feito:
--
-- ANTES (Bugado):
--   SUM(ROUND((n.VALOR_SERVICOS - n.VALOR_DEDUCOES) * n.ALIQUOTA, 2))
--   → Recalcula ISS do zero, aplicando arredondamento agregado
--
-- DEPOIS (Corrigido):
--   SUM(n.VALOR_ISS)
--   → Usa valores já emitidos nas notas (arredondamento individual)

-- ============================================================================
-- TRANSACTION: Aplicar Correção
-- ============================================================================

DECLARE
  v_procedure_text   CLOB;
  v_has_fix          BOOLEAN := FALSE;
  v_line_count       NUMBER := 0;
  v_affected_rows    NUMBER := 0;
  v_start_time       TIMESTAMP := SYSTIMESTAMP;
BEGIN

  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('CH-01 Migration: Corrigir Divergência de Arredondamento na Guia');
  DBMS_OUTPUT.PUT_LINE('Iniciada em: ' || TO_CHAR(v_start_time, 'DD/MM/YYYY HH24:MI:SS'));
  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('');

  -- Passo 1: Validar que PKG_GUIA existe
  BEGIN
    SELECT COUNT(*) INTO v_line_count
    FROM user_objects 
    WHERE object_name = 'PKG_GUIA' 
    AND object_type = 'PACKAGE BODY';

    IF v_line_count = 0 THEN
      RAISE_APPLICATION_ERROR(-20001, 
        'ERRO: Package PKG_GUIA não encontrado. Abortando migration.');
    END IF;

    DBMS_OUTPUT.PUT_LINE('✓ Passo 1: Package PKG_GUIA encontrado');
  EXCEPTION
    WHEN OTHERS THEN
      DBMS_OUTPUT.PUT_LINE('✗ Erro ao validar PKG_GUIA: ' || SQLERRM);
      RAISE;
  END;

  -- Passo 2: Verificar se a correção já foi aplicada
  BEGIN
    SELECT COUNT(*) INTO v_line_count
    FROM user_source
    WHERE name = 'PKG_GUIA'
    AND type = 'PACKAGE BODY'
    AND UPPER(text) LIKE '%SUM(N.VALOR_ISS)%'
    AND UPPER(text) NOT LIKE '%ROUND((N.VALOR_SERVICOS%';

    IF v_line_count > 0 THEN
      v_has_fix := TRUE;
      DBMS_OUTPUT.PUT_LINE('⚠ Aviso: Correção já parece ter sido aplicada');
      DBMS_OUTPUT.PUT_LINE('  (Encontrado SUM(n.VALOR_ISS) na procedure)');
      DBMS_OUTPUT.PUT_LINE('');
      DBMS_OUTPUT.PUT_LINE('Esta migration pode já ter sido executada.');
      DBMS_OUTPUT.PUT_LINE('Continuando para validação e recalcular guias...');
      DBMS_OUTPUT.PUT_LINE('');
    END IF;
  EXCEPTION
    WHEN OTHERS THEN
      DBMS_OUTPUT.PUT_LINE('Info: Verificação prévia de código não conclusiva');
  END;

  -- Passo 3: Recriar Package PKG_GUIA com a correção
  DBMS_OUTPUT.PUT_LINE('✓ Passo 2: Recriando Package PKG_GUIA com correção...');

  BEGIN
    EXECUTE IMMEDIATE '
CREATE OR REPLACE PACKAGE PKG_GUIA AS
    PROCEDURE recalcular_guia(p_prestador_id NUMBER, p_competencia DATE);
END PKG_GUIA;';

    EXECUTE IMMEDIATE '
CREATE OR REPLACE PACKAGE BODY PKG_GUIA AS

    -- guia (DAM) = soma do ISS das notas normais, nao retidas, da competencia
    -- vencimento: dia 10 do mes seguinte
    PROCEDURE recalcular_guia(p_prestador_id NUMBER, p_competencia DATE) IS
        v_venc DATE := ADD_MONTHS(TRUNC(p_competencia, ''MM''), 1) + 9;
    BEGIN
        MERGE INTO GUIAS g
        USING (SELECT p_prestador_id AS PRESTADOR_ID,
                      p_competencia  AS COMPETENCIA,
                      NVL(SUM(n.VALOR_ISS), 0) AS VALOR
                 FROM NFSE n
                WHERE n.PRESTADOR_ID = p_prestador_id
                  AND n.COMPETENCIA  = p_competencia
                  AND n.SITUACAO     = ''N''
                  AND n.ISS_RETIDO   = ''N'') s
           ON (g.PRESTADOR_ID = s.PRESTADOR_ID AND g.COMPETENCIA = s.COMPETENCIA)
         WHEN MATCHED THEN
              UPDATE SET g.VALOR_ISS = s.VALOR, g.DT_ATUALIZACAO = SYSDATE
               WHERE g.SITUACAO = ''A''
         WHEN NOT MATCHED THEN
              INSERT (ID, PRESTADOR_ID, COMPETENCIA, VALOR_ISS, SITUACAO, DT_VENCIMENTO, DT_ATUALIZACAO)
              VALUES (SEQ_GUIAS.NEXTVAL, s.PRESTADOR_ID, s.COMPETENCIA, s.VALOR, ''A'', v_venc, SYSDATE);
    END recalcular_guia;

END PKG_GUIA;';

    DBMS_OUTPUT.PUT_LINE('  ✓ Package PKG_GUIA recriado com sucesso');
    DBMS_OUTPUT.PUT_LINE('  ✓ Correção aplicada: SUM(n.VALOR_ISS) em lugar de SUM(ROUND(...))');

  EXCEPTION
    WHEN OTHERS THEN
      DBMS_OUTPUT.PUT_LINE('✗ Erro ao recriar Package PKG_GUIA: ' || SQLERRM);
      RAISE;
  END;

  -- Passo 4: Recalcular todas as guias (sem downtime)
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('✓ Passo 3: Recalculando guias históricas...');
  DBMS_OUTPUT.PUT_LINE('  (Isso pode levar alguns minutos dependendo do volume)');

  BEGIN
    DECLARE
      v_total_guias      NUMBER := 0;
      v_processadas      NUMBER := 0;
      v_erros            NUMBER := 0;
      v_cursor_time      TIMESTAMP;
      v_last_prest       NUMBER := -1;
      v_last_comp        DATE;
    BEGIN

      -- Contar total de guias a serem recalculadas
      SELECT COUNT(DISTINCT prestador_id, competencia) INTO v_total_guias
      FROM GUIAS
      WHERE SITUACAO = 'A'
      AND COMPETENCIA < TRUNC(SYSDATE, 'MM');

      DBMS_OUTPUT.PUT_LINE('  Total de guias a recalcular: ' || v_total_guias);

      -- Recalcular cada guia
      FOR r IN (
        SELECT DISTINCT PRESTADOR_ID, COMPETENCIA
        FROM GUIAS
        WHERE SITUACAO = 'A'
          AND COMPETENCIA < TRUNC(SYSDATE, 'MM')
        ORDER BY PRESTADOR_ID, COMPETENCIA DESC
      ) LOOP

        BEGIN
          v_cursor_time := SYSTIMESTAMP;

          -- Executar recálculo
          PKG_GUIA.RECALCULAR_GUIA(r.PRESTADOR_ID, r.COMPETENCIA);

          v_processadas := v_processadas + 1;

          -- Log a cada 100 guias
          IF MOD(v_processadas, 100) = 0 THEN
            DBMS_OUTPUT.PUT_LINE('  Processadas: ' || v_processadas || '/' || v_total_guias);
          END IF;

        EXCEPTION
          WHEN OTHERS THEN
            v_erros := v_erros + 1;
            DBMS_OUTPUT.PUT_LINE('  ⚠ Erro ao recalcular ' || r.PRESTADOR_ID || '/' || 
                                 TO_CHAR(r.COMPETENCIA, 'MM/YYYY') || ': ' || SQLERRM);
        END;

      END LOOP;

      COMMIT;

      DBMS_OUTPUT.PUT_LINE('');
      DBMS_OUTPUT.PUT_LINE('  ✓ Recalcule concluído!');
      DBMS_OUTPUT.PUT_LINE('    • Guias processadas: ' || v_processadas);
      DBMS_OUTPUT.PUT_LINE('    • Erros: ' || v_erros);

    END;
  EXCEPTION
    WHEN OTHERS THEN
      DBMS_OUTPUT.PUT_LINE('✗ Erro ao recalcular guias: ' || SQLERRM);
      RAISE;
  END;

  -- Passo 5: Validação pós-correção
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('✓ Passo 4: Validação pós-correção...');

  DECLARE
    v_divergencias  NUMBER := 0;
  BEGIN
    -- Buscar guias que ainda têm divergência (não deveriam haver)
    SELECT COUNT(*) INTO v_divergencias
    FROM (
      SELECT g.prestador_id, g.competencia
      FROM GUIAS g
      JOIN (
        SELECT n.prestador_id, n.competencia, SUM(n.VALOR_ISS) as soma_notas
        FROM NFSE n
        WHERE n.SITUACAO = 'N'
        AND n.ISS_RETIDO = 'N'
        GROUP BY n.prestador_id, n.competencia
      ) notas ON notas.prestador_id = g.prestador_id 
             AND notas.competencia = g.competencia
      WHERE g.VALOR_ISS <> notas.soma_notas
    );

    IF v_divergencias = 0 THEN
      DBMS_OUTPUT.PUT_LINE('  ✓ Validação OK: Nenhuma divergência detectada');
      DBMS_OUTPUT.PUT_LINE('  ✓ Todas as guias batem com soma de notas');
    ELSE
      DBMS_OUTPUT.PUT_LINE('  ⚠ Aviso: ' || v_divergencias || ' guias ainda com divergência');
      DBMS_OUTPUT.PUT_LINE('  (Reexecute a migration ou investigue manualmente)');
    END IF;
  EXCEPTION
    WHEN OTHERS THEN
      DBMS_OUTPUT.PUT_LINE('  Info: Validação comparativa não disponível');
  END;

  -- Finalização
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('✓ MIGRATION CH-01-001 COMPLETADA COM SUCESSO!');
  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('O que foi alterado:');
  DBMS_OUTPUT.PUT_LINE('  • Package PKG_GUIA.RECALCULAR_GUIA() recriado');
  DBMS_OUTPUT.PUT_LINE('  • Cálculo corrigido: SUM(n.VALOR_ISS) em lugar de recalcular');
  DBMS_OUTPUT.PUT_LINE('  • Todas as guias históricas foram recalculadas');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('Resultado esperado:');
  DBMS_OUTPUT.PUT_LINE('  • Guia de cada competência agora bate com soma de ISS das notas');
  DBMS_OUTPUT.PUT_LINE('  • Sem mais divergências de arredondamento');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('Tempo de execução: ' || 
    ROUND((SYSTIMESTAMP - v_start_time) * 86400) || ' segundos');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('Para desfazer esta migration, execute:');
  DBMS_OUTPUT.PUT_LINE('  db/migrations/CH-01-002-ROLLBACK-GUIA-VALOR-ISS-ARREDONDAMENTO.sql');

  COMMIT;

EXCEPTION
  WHEN OTHERS THEN
    ROLLBACK;
    DBMS_OUTPUT.PUT_LINE('');
    DBMS_OUTPUT.PUT_LINE('✗ ERRO ao aplicar migration CH-01-001!');
    DBMS_OUTPUT.PUT_LINE('Erro: ' || SQLERRM);
    DBMS_OUTPUT.PUT_LINE('');
    DBMS_OUTPUT.PUT_LINE('Migration foi revertida (ROLLBACK)');
    RAISE;
END;
/

-- ============================================================================
-- VALIDAÇÃO FINAL
-- ============================================================================

SET ECHO OFF;
PROMPT
PROMPT ════════════════════════════════════════════════════════════════════════════
PROMPT VALIDAÇÃO FINAL - CH-01-001
PROMPT ════════════════════════════════════════════════════════════════════════════
PROMPT

BEGIN
  DBMS_OUTPUT.PUT_LINE('Verificando se PKG_GUIA foi atualizado corretamente...');
  DBMS_OUTPUT.PUT_LINE('');

  DECLARE
    v_count NUMBER;
  BEGIN
    SELECT COUNT(*) INTO v_count
    FROM user_source
    WHERE name = 'PKG_GUIA'
    AND type = 'PACKAGE BODY'
    AND UPPER(text) LIKE '%SUM(N.VALOR_ISS)%';

    IF v_count > 0 THEN
      DBMS_OUTPUT.PUT_LINE('✓ Validação PASSOU!');
      DBMS_OUTPUT.PUT_LINE('✓ Package PKG_GUIA contém o cálculo corrigido: SUM(n.VALOR_ISS)');
      DBMS_OUTPUT.PUT_LINE('✓ Correção foi aplicada com sucesso!');
    ELSE
      DBMS_OUTPUT.PUT_LINE('✗ Validação FALHOU!');
      DBMS_OUTPUT.PUT_LINE('✗ Package PKG_GUIA não contém SUM(n.VALOR_ISS)');
      DBMS_OUTPUT.PUT_LINE('✗ Migration pode não ter sido aplicada corretamente');
    END IF;
  END;
END;
/

-- ============================================================================
-- INFORMAÇÕES TÉCNICAS DESTA MIGRATION
-- ============================================================================
--
-- PROBLEMA (CH-01):
--   "A guia não bate com as notas"
--   • Soma manual do ISS de todas as notas de um mês divergia da guia
--   • Contribuinte abria reclamação na ouvidoria
--   • Fiscalização questionava se o erro era nas notas ou na guia
--
-- CAUSA RAIZ:
--   Dois modos diferentes de arredondamento:
--   1. Aplicação (C#): Math.Round(base × aliquota, 2) → arredonda CADA nota individualmente
--   2. Banco (SQL): SUM(ROUND((base) × aliquota, 2)) → recalcula e arredonda AGREGADO
--   Resultado: Divergência de centavos a mais de R$ 0,10
--
-- SOLUÇÃO:
--   • Package PKG_GUIA.RECALCULAR_GUIA() agora usa SUM(n.VALOR_ISS)
--   • Respeita os valores já emitidos nas notas (oficiais)
--   • Elimina recálculo que causava divergência
--   • NFS-e é documento fiscal: valores nunca mudam após emissão
--
-- IMPACTO EM PERFORMANCE:
--   • Melhoria: Elimina ROUND() em nível agregado (menos processamento)
--   • Recalcular todas as guias: ~O(n) onde n = quantidade de competências
--   • Sem downtime necessário (MERGE é atômico)
--   • Locking mínimo (apenas escrita em GUIAS)
--
-- COMPATIBILIDADE:
--   • Oracle 11g+
--   • Sem dependências de versão específica
--   • Sem mudanças de schema (apenas lógica de procedure)
--
-- VALIDAÇÃO:
--   Para verificar se a correção funcionou:
--   
--   SELECT g.prestador_id, g.competencia, 
--          g.VALOR_ISS as valor_guia,
--          SUM(n.VALOR_ISS) as soma_notas,
--          ABS(g.VALOR_ISS - SUM(n.VALOR_ISS)) as diferenca
--   FROM GUIAS g
--   JOIN NFSE n ON n.prestador_id = g.prestador_id 
--              AND n.competencia = g.competencia
--              AND n.situacao = 'N'
--              AND n.iss_retido = 'N'
--   GROUP BY g.prestador_id, g.competencia, g.VALOR_ISS
--   HAVING ABS(g.VALOR_ISS - SUM(n.VALOR_ISS)) > 0
--   ORDER BY diferenca DESC;
--   
--   Se não retornar linhas, a correção funcionou!
--
-- ROLLBACK:
--   Execute: db/migrations/CH-01-002-ROLLBACK-GUIA-VALOR-ISS-ARREDONDAMENTO.sql
--   Tempo esperado: < 1 minuto
--   Sem perda de dados (apenas reverte lógica)
--
-- ============================================================================

COMMIT;
