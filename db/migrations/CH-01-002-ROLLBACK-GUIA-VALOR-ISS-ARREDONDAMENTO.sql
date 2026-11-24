-- ============================================================================
-- CH-01: Reversão da Correção de Arredondamento na Guia
-- Migration: 002 - Rollback (Reverter da Produção)
-- Data: 2026-11-24
-- Descrição: Reverter Package PKG_GUIA para o cálculo original
--            (apenas se necessário - use com cautela)
-- ============================================================================
-- Pré-requisitos:
--   • Oracle Database 11g ou superior
--   • Package PKG_GUIA foi alterado pela migration CH-01-001
--   • Backup de dados recomendado antes de executar
-- ============================================================================

SET ECHO ON;
SET FEEDBACK ON;
SET TIMING ON;
SET SERVEROUTPUT ON SIZE 1000000;

-- ============================================================================
-- ⚠ AVISO: LEIA ANTES DE EXECUTAR
-- ============================================================================
-- Este script REVERTE a correção do CH-01.
-- Após executar este rollback:
--   • Guias voltarão a usar cálculo que causa divergência
--   • Reexecute CH-01-001-FIX-... para reaplicar a correção
--
-- APENAS use este script se:
--   1. Encontrou um bug na correção (incomum)
--   2. Precisa investigar o comportamento anterior
--   3. Tem backup pronto e plano de reaplicação
--
-- NÃO use este script se:
--   • A correção estava funcionando bem
--   • Quer apenas "reverter" para evitar mudança
--   • Não entende completamente o impacto

DECLARE
  v_start_time       TIMESTAMP := SYSTIMESTAMP;
  v_total_guias      NUMBER := 0;
  v_processadas      NUMBER := 0;
  v_erros            NUMBER := 0;
BEGIN

  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('CH-01 Rollback: Revertendo Correção de Arredondamento na Guia');
  DBMS_OUTPUT.PUT_LINE('Iniciada em: ' || TO_CHAR(v_start_time, 'DD/MM/YYYY HH24:MI:SS'));
  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('⚠ AVISO: Este rollback revertará a correção do CH-01!');
  DBMS_OUTPUT.PUT_LINE('');

  -- Passo 1: Validar que PKG_GUIA existe
  BEGIN
    EXECUTE IMMEDIATE 'SELECT 1 FROM user_objects WHERE object_name = ''PKG_GUIA'' AND object_type = ''PACKAGE BODY''';
    DBMS_OUTPUT.PUT_LINE('✓ Passo 1: Package PKG_GUIA encontrado');
  EXCEPTION
    WHEN OTHERS THEN
      RAISE_APPLICATION_ERROR(-20001, 'ERRO: Package PKG_GUIA não encontrado. Abortando rollback.');
  END;

  -- Passo 2: Recriar Package com a lógica original (com ROUND agregado)
  DBMS_OUTPUT.PUT_LINE('✓ Passo 2: Recriando Package PKG_GUIA com lógica original...');

  BEGIN
    EXECUTE IMMEDIATE '
CREATE OR REPLACE PACKAGE PKG_GUIA AS
    PROCEDURE recalcular_guia(p_prestador_id NUMBER, p_competencia DATE);
END PKG_GUIA;';

    -- Restaurar a lógica ORIGINAL que usava SUM(ROUND(...))
    -- Esta é a versão ANTERIOR à correção CH-01
    EXECUTE IMMEDIATE '
CREATE OR REPLACE PACKAGE BODY PKG_GUIA AS

    -- guia (DAM) = soma do ISS das notas normais, nao retidas, da competencia
    -- vencimento: dia 10 do mes seguinte
    -- ⚠ NOTA: Esta versão usa ROUND(AGREGADO) - causa divergência de arredondamento!
    PROCEDURE recalcular_guia(p_prestador_id NUMBER, p_competencia DATE) IS
        v_venc DATE := ADD_MONTHS(TRUNC(p_competencia, ''MM''), 1) + 9;
    BEGIN
        MERGE INTO GUIAS g
        USING (SELECT p_prestador_id AS PRESTADOR_ID,
                      p_competencia  AS COMPETENCIA,
                      NVL(SUM(ROUND((n.VALOR_SERVICOS - n.VALOR_DEDUCOES) * n.ALIQUOTA, 2)), 0) AS VALOR
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

    DBMS_OUTPUT.PUT_LINE('  ✓ Package PKG_GUIA recriado com lógica original');
    DBMS_OUTPUT.PUT_LINE('  ✓ Usando: SUM(ROUND((n.VALOR_SERVICOS - n.VALOR_DEDUCOES) * n.ALIQUOTA, 2))');

  EXCEPTION
    WHEN OTHERS THEN
      DBMS_OUTPUT.PUT_LINE('✗ Erro ao recriar Package PKG_GUIA: ' || SQLERRM);
      RAISE;
  END;

  -- Passo 3: Recalcular todas as guias (volta ao comportamento anterior)
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('✓ Passo 3: Recalculando guias (reverte para cálculo anterior)...');
  DBMS_OUTPUT.PUT_LINE('  (Isso pode levar alguns minutos dependendo do volume)');

  BEGIN
    DECLARE
      v_cursor_time      TIMESTAMP;
    BEGIN

      -- Contar total de guias a serem recalculadas
      SELECT COUNT(DISTINCT prestador_id, competencia) INTO v_total_guias
      FROM GUIAS
      WHERE SITUACAO = 'A'
      AND COMPETENCIA < TRUNC(SYSDATE, 'MM');

      DBMS_OUTPUT.PUT_LINE('  Total de guias a recalcular: ' || v_total_guias);

      -- Recalcular cada guia com a lógica original
      FOR r IN (
        SELECT DISTINCT PRESTADOR_ID, COMPETENCIA
        FROM GUIAS
        WHERE SITUACAO = 'A'
          AND COMPETENCIA < TRUNC(SYSDATE, 'MM')
        ORDER BY PRESTADOR_ID, COMPETENCIA DESC
      ) LOOP

        BEGIN
          v_cursor_time := SYSTIMESTAMP;

          -- Executar recálculo (agora com lógica original)
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
      DBMS_OUTPUT.PUT_LINE('  ✓ Recálcue concluído!');
      DBMS_OUTPUT.PUT_LINE('    • Guias processadas: ' || v_processadas);
      DBMS_OUTPUT.PUT_LINE('    • Erros: ' || v_erros);

    END;
  EXCEPTION
    WHEN OTHERS THEN
      DBMS_OUTPUT.PUT_LINE('✗ Erro ao recalcular guias: ' || SQLERRM);
      RAISE;
  END;

  -- Finalização
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('✓ ROLLBACK CH-01-002 COMPLETADO!');
  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('O que foi alterado:');
  DBMS_OUTPUT.PUT_LINE('  • Package PKG_GUIA.RECALCULAR_GUIA() recriado');
  DBMS_OUTPUT.PUT_LINE('  • Revertido para lógica original: SUM(ROUND(...))');
  DBMS_OUTPUT.PUT_LINE('  • Todas as guias foram recalculadas');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('⚠ NOTA IMPORTANTE:');
  DBMS_OUTPUT.PUT_LINE('  Guias voltarão a apresentar divergência de arredondamento!');
  DBMS_OUTPUT.PUT_LINE('  (Soma manual de ISS das notas não baterá com valor da guia)');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('Para reaplicar a correção, execute:');
  DBMS_OUTPUT.PUT_LINE('  db/migrations/CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO.sql');
  DBMS_OUTPUT.PUT_LINE('');
  DBMS_OUTPUT.PUT_LINE('Tempo de execução: ' || 
    ROUND((SYSTIMESTAMP - v_start_time) * 86400) || ' segundos');

  COMMIT;

EXCEPTION
  WHEN OTHERS THEN
    ROLLBACK;
    DBMS_OUTPUT.PUT_LINE('');
    DBMS_OUTPUT.PUT_LINE('✗ ERRO ao aplicar rollback CH-01-002!');
    DBMS_OUTPUT.PUT_LINE('Erro: ' || SQLERRM);
    DBMS_OUTPUT.PUT_LINE('');
    DBMS_OUTPUT.PUT_LINE('Rollback foi revertido (ROLLBACK)');
    RAISE;
END;
/

-- ============================================================================
-- VALIDAÇÃO FINAL
-- ============================================================================

SET ECHO OFF;
PROMPT
PROMPT ════════════════════════════════════════════════════════════════════════════
PROMPT VALIDAÇÃO FINAL - CH-01 Rollback Completado
PROMPT ════════════════════════════════════════════════════════════════════════════
PROMPT

BEGIN
  DBMS_OUTPUT.PUT_LINE('Verificando se PKG_GUIA foi revertido para a lógica original...');
  DBMS_OUTPUT.PUT_LINE('');

  DECLARE
    v_count_new    NUMBER;
    v_count_old    NUMBER;
  BEGIN
    -- Verificar se tem a nova lógica (NÃO deveria ter)
    SELECT COUNT(*) INTO v_count_new
    FROM user_source
    WHERE name = 'PKG_GUIA'
    AND type = 'PACKAGE BODY'
    AND UPPER(text) LIKE '%SUM(N.VALOR_ISS)%'
    AND UPPER(text) NOT LIKE '%ROUND((N.VALOR_SERVICOS%';

    -- Verificar se tem a lógica original (deveria ter)
    SELECT COUNT(*) INTO v_count_old
    FROM user_source
    WHERE name = 'PKG_GUIA'
    AND type = 'PACKAGE BODY'
    AND UPPER(text) LIKE '%ROUND((N.VALOR_SERVICOS%VALOR_DEDUCOES)%ALIQUOTA%';

    IF v_count_old > 0 AND v_count_new = 0 THEN
      DBMS_OUTPUT.PUT_LINE('✓ Validação PASSOU!');
      DBMS_OUTPUT.PUT_LINE('✓ Package PKG_GUIA foi revertido para a lógica original');
      DBMS_OUTPUT.PUT_LINE('✓ Rollback foi aplicado com sucesso!');
      DBMS_OUTPUT.PUT_LINE('');
      DBMS_OUTPUT.PUT_LINE('⚠ ATENÇÃO: Guias voltarão a apresentar divergência de arredondamento');
    ELSE
      DBMS_OUTPUT.PUT_LINE('⚠ Validação INCONCLUSIVA');
      DBMS_OUTPUT.PUT_LINE('  Verificação de código não foi conclusiva');
      DBMS_OUTPUT.PUT_LINE('  Execute manualmente: SELECT text FROM user_source WHERE name = ''PKG_GUIA''');
    END IF;
  END;
END;
/

-- ============================================================================
-- INFORMAÇÕES TÉCNICAS DO ROLLBACK
-- ============================================================================
--
-- Este rollback reverte a correção CH-01-001.
--
-- ANTES DO ROLLBACK (Corrigido):
--   SUM(n.VALOR_ISS) → Usa valores já emitidos
--   Resultado: Guia bate com soma de notas
--
-- DEPOIS DO ROLLBACK (Original/Bugado):
--   SUM(ROUND((n.VALOR_SERVICOS - n.VALOR_DEDUCOES) * n.ALIQUOTA, 2)) → Recalcula
--   Resultado: Guia diverge de soma de notas (centavos)
--
-- QUANDO USAR ESTE ROLLBACK:
--   • Se descobrir que a correção causou um problema não previsto (raro)
--   • Se precisar investigar o comportamento anterior
--   • Se tiver que reverter por alguma razão excepcional
--
-- QUANDO NÃO USAR:
--   • Se a correção estava funcionando normalmente
--   • Se simplesmente quer "desfazer" a mudança
--   • Se não tem um plano de reaplicação
--
-- IMPACTO:
--   • Guias voltarão a divergir de notas (arredondamento)
--   • Fiscalização voltará a questionar as diferenças
--   • Contribuintes voltarão a reclamar na ouvidoria
--
-- RECUPERAÇÃO:
--   Execute: db/migrations/CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO.sql
--
-- ============================================================================

COMMIT;
