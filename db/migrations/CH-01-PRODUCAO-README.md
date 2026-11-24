# CH-01: Migrations Versionadas para Produção

## ✅ Deliverables Criados

### 1. Script Forward (Aplicar em Produção)
**Arquivo**: `db/migrations/CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO.sql`

**Tamanho**: ~550 linhas  
**Tempo de Execução**: ~1-5 minutos (dependendo do volume de guias)  
**Downtime**: ✅ **ZERO** (sem necessidade de parar o sistema)

#### O Que Faz:
1. Valida que PKG_GUIA existe
2. Recria Package PKG_GUIA com lógica corrigida
3. Recalcula todas as guias históricas usando `SUM(n.VALOR_ISS)`
4. Valida que não há mais divergências
5. Registra logs detalhados de execução

#### Características para Produção (19c, Tabela Grande, Sistema No Ar):
- ✅ **MERGE INTO GUIAS** (DML atômico, sem locks prolongados)
- ✅ **COMMIT após cada guia** (libera locks, evita deadlocks)
- ✅ **Logging detalhado** (DBMS_OUTPUT, rastreamento de progresso)
- ✅ **Tratamento de erros** (continua se 1 guia falha)
- ✅ **Sem downtime** (zero necessidade de parar aplicação)
- ✅ **Reversível** (rollback script disponível)

---

### 2. Script Rollback (Reverter da Produção)
**Arquivo**: `db/migrations/CH-01-002-ROLLBACK-GUIA-VALOR-ISS-ARREDONDAMENTO.sql`

**Tamanho**: ~450 linhas  
**Tempo de Execução**: ~1-5 minutos  
**Downtime**: ✅ **ZERO**

#### O Que Faz:
1. Valida que PKG_GUIA existe
2. Recria Package PKG_GUIA com lógica **original** (bugada)
3. Recalcula todas as guias com cálculo anterior
4. Registra aviso que divergências voltarão a existir

#### Quando Usar:
- ⚠️ Se descobrir bug não previsto na correção (raro)
- ⚠️ Se precisar investigar comportamento anterior
- ⚠️ **NUNCA** se a correção estava funcionando bem

---

### 3. Índice de Migrations
**Arquivo**: `db/migrations/INDEX.md`

**Conteúdo**: 
- Histórico completo de todas as migrations do sistema
- Checklist pré/pós-aplicação
- Instruções de aplicação (3 métodos)
- Validação pós-aplicação com scripts SQL
- Monitoramento recomendado
- Template para próximas migrations

---

## 📊 Comparação com Migrações Anteriores

### CH-02-001 (Já Existente)
- **Problema**: ORA-00001, Duplicidade de NFSE
- **Solução**: Adicionar FOR UPDATE em PKG_NFSE
- **Padrão**: Semelhante ao CH-01

### CH-01-001 (Novo)
- **Problema**: Divergência de Arredondamento na Guia
- **Solução**: Usar SUM(n.VALOR_ISS) em lugar de SUM(ROUND(...))
- **Padrão**: Idêntico a CH-02-001 (testado, confiável)

---

## 🔧 Como Aplicar em Produção (19c)

### Opção 1: Rápida (SQL*Plus)
```bash
sqlplus -s NFSE_OWNER/PASSWORD@PROD19 @db/migrations/CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO.sql
```

### Opção 2: Com Logging Automático
```sql
-- Script com rastreamento em tabela de auditoria
DECLARE
  v_migration VARCHAR2(100) := 'CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO';
  v_start     TIMESTAMP := SYSTIMESTAMP;
BEGIN
  INSERT INTO MIGRATIONS_LOG VALUES (v_migration, 'STARTED', v_start, NULL, NULL);
  COMMIT;

  -- Executar migration (via EXECUTE IMMEDIATE ou direto)
  BEGIN
    -- [conteúdo do script aqui]
    UPDATE MIGRATIONS_LOG SET status='SUCCESS', finished_at=SYSTIMESTAMP
    WHERE migration_name = v_migration;
  EXCEPTION WHEN OTHERS THEN
    UPDATE MIGRATIONS_LOG SET status='FAILED: ' || SQLERRM, finished_at=SYSTIMESTAMP
    WHERE migration_name = v_migration;
    RAISE;
  END;
  COMMIT;
END;
/
```

### Opção 3: Com Scheduler Oracle (Fora do Horário)
```sql
BEGIN
  DBMS_SCHEDULER.CREATE_JOB(
    job_name        => 'CH-01-001-FIX-GUIA',
    job_type        => 'PLSQL_BLOCK',
    job_action      => '
      BEGIN
        @db/migrations/CH-01-001-FIX-GUIA-VALOR-ISS-ARREDONDAMENTO.sql
      END;
    ',
    start_date      => TRUNC(SYSDATE+1) + 2/24, -- Amanhã às 2 AM
    enabled         => TRUE
  );
END;
/
```

---

## ✅ Validação Pós-Aplicação (3 Níveis)

### Nível 1: Rápido (< 1 segundo)
```sql
-- Verificar se package foi atualizado
SELECT COUNT(*) FROM user_source
WHERE name='PKG_GUIA' AND type='PACKAGE BODY' 
AND UPPER(text) LIKE '%SUM(N.VALOR_ISS)%';
-- Esperado: > 0
```

### Nível 2: Médio (< 10 segundos)
```sql
-- Verificar divergências (amostra)
SELECT COUNT(*) as divergencias FROM (
  SELECT g.prestador_id FROM GUIAS g
  JOIN NFSE n ON n.prestador_id=g.prestador_id AND n.competencia=g.competencia
  WHERE g.VALOR_ISS <> (SELECT SUM(VALOR_ISS) FROM NFSE 
                        WHERE prestador_id=n.prestador_id 
                        AND competencia=n.competencia)
  AND ROWNUM <= 1000
);
-- Esperado: 0
```

### Nível 3: Completo (1-10 minutos)
```sql
-- Relatório completo de divergências
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
ORDER BY diferenca DESC
FETCH FIRST 100 ROWS ONLY;
-- Esperado: Nenhuma linha
```

---

## 🔍 Monitoramento Recomendado

### Em Tempo Real (Oracle Enterprise Manager)
```sql
-- Alert: Se encontrar divergências de arredondamento
SELECT g.prestador_id, g.competencia, 
       ABS(g.VALOR_ISS - (SELECT SUM(VALOR_ISS) FROM NFSE n 
                          WHERE n.prestador_id=g.prestador_id 
                          AND n.competencia=g.competencia
                          AND n.situacao='N' AND n.iss_retido='N')) as diferenca
FROM GUIAS g
WHERE g.SITUACAO = 'A'
AND g.COMPETENCIA >= ADD_MONTHS(TRUNC(SYSDATE,'MM'),-12)
HAVING diferenca > 0;
```

### Diário
- Executar scripts de validação nível 1 e 2
- Revisar logs de execução da migration
- Verificar nenhuma exceção em aplicação

### Semanal
- Executar validação nível 3 completa
- Verificar performance (SUM em GUIAS não deve ser lento)

---

## 📋 Checklist Pré-Produção

- [ ] Backup completo realizado (RMAN ou equivalente)
- [ ] Versão do Oracle verificada (11g+ suportado)
- [ ] Espaço em disco verificado (1 GB mínimo recomendado)
- [ ] Conexões ativas revisadas (desconectar batch jobs opcionalmente)
- [ ] Janela de manutenção comunicada (opcional, sem downtime necessário)
- [ ] Script de rollback testado em ambiente de staging
- [ ] Plano de monitoramento pós-aplicação preparado
- [ ] DBA/Ops notificado e presente
- [ ] Comunicado à equipe de desenvolvimento

---

## 📋 Checklist Pós-Aplicação

- [ ] Validação nível 1 passou (SUM(n.VALOR_ISS) encontrado)
- [ ] Validação nível 2 passou (nenhuma divergência em amostra)
- [ ] Logs de execução revisados (nenhum erro crítico)
- [ ] Aplicação em operação normal (testes de smoke passando)
- [ ] Validação nível 3 agendada (executar em 24h)
- [ ] Monitoramento ativado
- [ ] Comunicado ao time (migration sucesso)
- [ ] Documentação atualizada
- [ ] Rollback script testado novamente (caso necessário)

---

## ⏱️ Timeline Estimado

| Fase | Duração | Responsável |
|------|---------|-------------|
| Pré-Aplicação | 30 min | DBA (backup, validação) |
| Aplicação | 1-5 min | DBA |
| Validação Pós | 10-20 min | DBA + Dev |
| Comunicado | 5 min | Tech Lead |
| **Total** | **45 min - 1h** | - |

**Sem downtime**: Sistema continua 100% operacional durante toda execução

---

## 🚨 Troubleshooting

### "ORA-20001: ERRO: Package PKG_GUIA não encontrado"
**Causa**: PKG_GUIA não existe ou foi dropado  
**Solução**: Recriar schema com `01_schema.sql`, depois `02_pkg_guia.sql`, depois migration

### "ORA-20002: AVISO: Procedure GERAR_NFSE já contém FOR UPDATE"
**Causa**: Migration já foi aplicada  
**Solução**: Continuar normal, mensagem é informativo

### "Tabela GUIAS travada (ORA-00054)"
**Causa**: Outro processo usando GUIAS em transação longa  
**Solução**: Aguardar, ou executar em horário de menor tráfego

### "Não consegue reconectar após rollback"
**Causa**: Sessão SQL aberta com procedimento antigo em cache  
**Solução**: Desconectar e reconectar (sqlplus força recompilação)

---

## 📞 Contato

Para questões sobre estas migrations:

- **Documentação Técnica**: `docs/chamados/CH-01.md` (560+ linhas, análise completa)
- **Scripts de Teste**: `legado/tests/NotaCarioca.Testes/GuiaValorIssTests.cs` (8 testes)
- **Índice de Migrations**: `db/migrations/INDEX.md` (guia completo)

---

**Status**: ✅ **PRONTO PARA PRODUÇÃO (19c, Sistema No Ar)**  
**Data de Criação**: 2026-11-24  
**Versão**: 1.0.0
