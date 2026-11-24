# Migrations do Sistema NFS-e

## Resumo de Migrações

Este arquivo documenta todas as migrations versionadas para aplicação em produção.

---

## 🔄 Histórico de Migrations

### CH-02: Race Condition em GERAR_NFSE (ORA-00001)

| # | Script | Status | Data | Descrição |
|---|--------|--------|------|-----------|
| 001 | `CH-02-001-FIX-ORA00001-RACE-CONDITION.sql` | ✅ Aplicado | 2026-11-23 | Adicionar FOR UPDATE em GERAR_NFSE() para evitar duplicidade |
| 002 | `CH-02-002-ROLLBACK-FIX-ORA00001.sql` | ⏸️ Standby | 2026-11-23 | Reverter correção (caso necessário) |

**Resumo**: Implementar pessimistic locking em PKG_NFSE.GERAR_NFSE() para eliminar race condition que causa ORA-00001 e duplicidade de NFS-e ao processar lotes grandes.

---

### CH-05: Trigger com Autonomous Transaction

| # | Script | Status | Data | Descrição |
|---|--------|--------|------|-----------|
| 03 | `03_fix_ch05_trigger_autonomous_transaction.sql` | ✅ Aplicado | 2026-11-23 | Adicionar AUTONOMOUS_TRANSACTION em trigger para resolver ORA-14667 |
| 03 | `03_rollback_fix_ch05_trigger_autonomous_transaction.sql` | ⏸️ Standby | 2026-11-23 | Reverter correção (caso necessário) |

**Resumo**: Implementar PRAGMA AUTONOMOUS_TRANSACTION em trigger_guia_insert para permitir COMMIT dentro do trigger (necessário para contagem de lotes).

---

### ✅ CH-01: Divergência de Arredondamento na Guia

| # | Script | Status | Data | Descrição |
|---|--------|--------|------|-----------|
| 001 | `CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO.sql` | ✅ Aplicado | 2026-11-24 | Corrigir cálculo de ISS para usar valores já emitidos em notas |
| 002 | `CH-01-002-ROLLBACK-GUIA-VALOR-ISS-ARREDONDAMENTO.sql` | ⏸️ Standby | 2026-11-24 | Reverter correção (caso necessário) |

**Resumo**: Usar SUM(n.VALOR_ISS) em lugar de SUM(ROUND(...)) para eliminar divergência de arredondamento entre guia e soma de notas fiscais.

---

### 🚀 FEATURE: Suporte a CNPJ e CPF Alfanuméricos (IN RFB nº 2.229/2024)

| # | Script | Status | Data | Descrição |
|---|--------|--------|------|-----------|
| 001 | `FEATURE-CNPJ-ALFANUMERICO-001-ALTER-SCHEMA.sql` | ✅ Aplicado | 2026-11-24 | Adicionar colunas TIPO (CONTRIBUINTES) e TOMADOR_DOCUMENTO_ALFANUMERICO (RPS) |
| 002 | `FEATURE-CNPJ-ALFANUMERICO-001-ROLLBACK.sql` | ⏸️ Standby | 2026-11-24 | Reverter alterações de schema (caso necessário) |

**Resumo**: Implementar suporte a CNPJ alfanuméricos com 3 passos:
1. Adicionar coluna TIPO em CONTRIBUINTES (NUMBER(1) DEFAULT 1)
2. Adicionar coluna TOMADOR_DOCUMENTO_ALFANUMERICO em RPS (VARCHAR2(14))
3. Criar índice IX_NFSE_TOMADOR_ALFANUMERICO para otimizar consultas

**Alterações aplicadas:**
- ✅ CONTRIBUINTES.TIPO criada (NUMBER(1) com default 1)
- ✅ RPS.TOMADOR_DOCUMENTO_ALFANUMERICO criada (VARCHAR2(14))
- ✅ RPS.DT_MIGRACAO_ALFANUMERICO criada (DATE)
- ✅ Dados migrados de TOMADOR_CNPJ/TOMADOR_CPF para TOMADOR_DOCUMENTO_ALFANUMERICO
- ✅ Índice IX_NFSE_TOMADOR_ALFANUMERICO criado

**Compatibilidade:** 100% regressiva (números continuam funcionando)

---

## 📋 Como Aplicar Migrations em Produção

### 1️⃣ Pré-requisitos

- [ ] Oracle Database 11g ou superior (19c recomendado)
- [ ] Acesso DBA ao banco de dados
- [ ] Backup completo realizado
- [ ] Janela de manutenção planejada (opcional - sem downtime necessário)

### 2️⃣ Checklist Pré-Aplicação

```bash
# Verificar versão do Oracle
sqlplus -s NFSE_OWNER/PASSWORD @-
SELECT * FROM v$version WHERE banner LIKE '%Oracle%';

# Verificar espaço livre
SELECT tablespace_name, SUM(bytes)/1024/1024/1024 as GB_livre
FROM dba_free_space
GROUP BY tablespace_name;

# Verificar conexões ativas
SELECT COUNT(*) FROM v$session WHERE username = 'NFSE_OWNER';

# Backup prévio (Oracle)
RMAN> BACKUP DATABASE PLUS ARCHIVELOG;
```

### 3️⃣ Aplicar Migration (Exemplo: CH-01-001)

#### Opção A: Por Script SQL

```bash
# No servidor Oracle ou cliente SQL*Plus
sqlplus -s NFSE_OWNER/PASSWORD@DATABASE @db/migrations/CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO.sql
```

#### Opção B: Por Ferramenta IDE (SQL Developer, Toad)

1. Conectar ao banco com usuário DBA
2. Abrir arquivo: `db/migrations/CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO.sql`
3. Executar (F5 ou Execute)
4. Verificar output (DBMS_OUTPUT)

#### Opção C: Por Script PL/SQL (Recomendado para Automatização)

```sql
-- Script de aplicação automática com logging
DECLARE
  v_migration_name VARCHAR2(100) := 'CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO';
  v_start_time     TIMESTAMP := SYSTIMESTAMP;
  v_status         VARCHAR2(50);
BEGIN

  -- Log de início
  INSERT INTO SCHEMA_MIGRATIONS_LOG (migration_name, status, started_at)
  VALUES (v_migration_name, 'RUNNING', v_start_time);
  COMMIT;

  -- Executar migration
  BEGIN
    @db/migrations/CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO.sql
    v_status := 'SUCCESS';
  EXCEPTION
    WHEN OTHERS THEN
      v_status := 'FAILED: ' || SQLERRM;
      ROLLBACK;
  END;

  -- Log de conclusão
  UPDATE SCHEMA_MIGRATIONS_LOG
  SET status = v_status,
      completed_at = SYSTIMESTAMP,
      execution_time = ROUND((SYSTIMESTAMP - v_start_time) * 86400) || 's'
  WHERE migration_name = v_migration_name;
  COMMIT;

END;
/
```

### 4️⃣ Validação Pós-Aplicação

```sql
-- Validar correção CH-01-001
BEGIN
  DECLARE
    v_count NUMBER;
  BEGIN
    SELECT COUNT(*) INTO v_count
    FROM user_source
    WHERE name = 'PKG_GUIA'
    AND type = 'PACKAGE BODY'
    AND UPPER(text) LIKE '%SUM(N.VALOR_ISS)%';

    IF v_count > 0 THEN
      DBMS_OUTPUT.PUT_LINE('✓ Validação OK: Correção CH-01-001 aplicada com sucesso!');
    ELSE
      DBMS_OUTPUT.PUT_LINE('✗ Validação FALHOU: Correção não foi aplicada corretamente');
    END IF;
  END;
END;
/

-- Validar que não há divergências de arredondamento
SELECT g.prestador_id, g.competencia, 
       g.VALOR_ISS as valor_guia,
       SUM(n.VALOR_ISS) as soma_notas,
       ABS(g.VALOR_ISS - SUM(n.VALOR_ISS)) as diferenca
FROM GUIAS g
JOIN NFSE n ON n.prestador_id = g.prestador_id 
           AND n.competencia = g.competencia
           AND n.situacao = 'N'
           AND n.iss_retido = 'N'
GROUP BY g.prestador_id, g.competencia, g.VALOR_ISS
HAVING ABS(g.VALOR_ISS - SUM(n.VALOR_ISS)) > 0
ORDER BY diferenca DESC;
-- Se não retorna linhas, a correção funcionou!
```

### 5️⃣ Pós-Aplicação

- [ ] Validação SQL passou (nenhuma divergência)
- [ ] Application log revisado (nenhum erro)
- [ ] Comunicado à equipe de operações
- [ ] Plano de monitoramento ativado
- [ ] Documentação atualizada

---

## 🔙 Como Reverter (Rollback)

Se precisar reverter uma migration:

```bash
# Exemplo: Reverter CH-01-001
sqlplus -s NFSE_OWNER/PASSWORD@DATABASE @db/migrations/CH-01-002-ROLLBACK-GUIA-VALOR-ISS-ARREDONDAMENTO.sql
```

### ⚠️ Cuidado ao Reverter

- **CH-01-002 Rollback**: Revertará a correção, fazendo guias voltar a divergir de notas
- **CH-02-002 Rollback**: Removerá FOR UPDATE, deixando open a race condition ORA-00001
- **CH-05 Rollback**: Removerá AUTONOMOUS_TRANSACTION, causando ORA-14667 novamente

**Recomendação**: Manter todas as migrations aplicadas. Usar rollback apenas para investigação ou emergência.

---

## 📊 Tabela de Compatibilidade

| Migration | Oracle 11g | Oracle 12c | Oracle 19c | 21c | Downtime |
|-----------|-----------|-----------|-----------|-----|----------|
| CH-01-001 | ✅ Sim | ✅ Sim | ✅ Sim | ✅ Sim | ✅ Não |
| CH-01-002 | ✅ Sim | ✅ Sim | ✅ Sim | ✅ Sim | ✅ Não |
| CH-02-001 | ✅ Sim | ✅ Sim | ✅ Sim | ✅ Sim | ✅ Não |
| CH-02-002 | ✅ Sim | ✅ Sim | ✅ Sim | ✅ Sim | ✅ Não |
| CH-05-003 | ⚠️ Talvez | ✅ Sim | ✅ Sim | ✅ Sim | ✅ Não |

---

## 🔍 Monitoramento Pós-Aplicação

### CH-01-001: Verificar Divergências

```sql
-- Executar diariamente ou semanalmente
SELECT COUNT(*) as divergencias_encontradas
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
-- Esperado: 0 divergências

-- Se encontrar divergências, investigar:
SELECT g.prestador_id, g.competencia, 
       g.VALOR_ISS, notas.soma_notas,
       ABS(g.VALOR_ISS - notas.soma_notas) as diferenca
FROM GUIAS g
LEFT JOIN (
  SELECT n.prestador_id, n.competencia, SUM(n.VALOR_ISS) as soma_notas
  FROM NFSE n
  WHERE n.SITUACAO = 'N' AND n.ISS_RETIDO = 'N'
  GROUP BY n.prestador_id, n.competencia
) notas ON notas.prestador_id = g.prestador_id 
       AND notas.competencia = g.competencia
WHERE g.VALOR_ISS <> NVL(notas.soma_notas, 0)
ORDER BY diferenca DESC;
```

### CH-02-001: Verificar Race Conditions

```sql
-- Monitora ORA-00001 em logs Oracle
SELECT COUNT(*) as erros_ora00001
FROM v$diag_incident
WHERE incident_id LIKE '%ORA-00001%'
AND creation_time > TRUNC(SYSDATE);
-- Esperado: 0 erros

-- Verificar duplicidade de NFSE
SELECT prestador_id, numero, COUNT(*) as qty
FROM NFSE
GROUP BY prestador_id, numero
HAVING COUNT(*) > 1;
-- Esperado: Nenhuma linha (sem duplicatas)
```

---

## 📝 Template para Próximas Migrations

Ao criar novas migrations, seguir este padrão:

```sql
-- ============================================================================
-- CH-XX: [Descrição do Chamado]
-- Migration: NNN - [Forward/Rollback]
-- Data: YYYY-MM-DD
-- Descrição: [O que a migration faz]
-- ============================================================================

SET ECHO ON;
SET FEEDBACK ON;
SET TIMING ON;
SET SERVEROUTPUT ON SIZE 1000000;

DECLARE
  v_start_time       TIMESTAMP := SYSTIMESTAMP;
BEGIN

  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');
  DBMS_OUTPUT.PUT_LINE('CH-XX-NNN: [Descrição]');
  DBMS_OUTPUT.PUT_LINE('Iniciada em: ' || TO_CHAR(v_start_time, 'DD/MM/YYYY HH24:MI:SS'));
  DBMS_OUTPUT.PUT_LINE('═══════════════════════════════════════════════════════════════════════');

  -- ... passos da migration ...

  DBMS_OUTPUT.PUT_LINE('✓ MIGRATION CH-XX-NNN COMPLETADA COM SUCESSO!');

  COMMIT;

EXCEPTION
  WHEN OTHERS THEN
    ROLLBACK;
    DBMS_OUTPUT.PUT_LINE('✗ ERRO: ' || SQLERRM);
    RAISE;
END;
/
```

---

## 📞 Suporte

Para questões sobre migrations em produção:

1. **Consultar documentação** do chamado (ex: `docs/chamados/CH-01.md`)
2. **Revisar logs** de execução (output DBMS_OUTPUT)
3. **Validar com scripts** de pós-aplicação
4. **Contatar time de DBA** se necessário
5. **Usar rollback script** se problema descoberto

---

## Histórico de Alterações

| Data | Autoria | Alteração |
|------|---------|-----------|
| 2026-11-24 | Copilot | Adicionados CH-01-001 e CH-01-002 (Divergência de Arredondamento) |
| 2026-11-23 | Copilot | Adicionados CH-02-001 e CH-02-002 (Race Condition) |
| 2026-11-23 | Copilot | Adicionados CH-05 scripts (Trigger Autonomous Transaction) |

---

**Última atualização**: 2026-11-24  
**Versão**: 1.1.0
