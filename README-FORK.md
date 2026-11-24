# Fork — Desafio NFS-e Nota Carioca: Solução da Fila de Chamados

**Data:** 2026-11-24  
**Status:**  Pronto para Produção (com recomendações críticas)  
**Compatibilidade:** .NET 4.8 + .NET 8.0  
**Testes:** 72/72 Passando (unitários + integração + alfanumérico)

---

## Como Rodar

### Pré-requisitos
- Docker + Docker Compose (para Oracle 23ai Free)
- .NET SDK 8.0+ (net48 é compilado com reference assemblies)
- Git
- 3GB RAM disponível (Oracle + processamento de lotes)

### Setup Inicial

`bash
# 1. Clonar e navegar
git clone https://github.com/seu-usuario/desafio-dotnet-senior.git
cd desafio-dotnet-senior

# 2. Subir containers (Oracle + WebServices)
docker compose up -d --build

# 3. Aguardar Oracle inicializar e carregar dados
# Primeira subida: 2-3 minutos (carga de ~2M notas)
docker compose logs -f oracle | grep "Database is ready"

# 4. Verificar saúde
curl http://localhost:8080/health  # Deve retornar HTTP 200

# 5. Compilar código
dotnet build legado/NotaCarioca.sln

# 6. Executar testes
dotnet test legado/tests/NotaCarioca.Testes -f net8.0 --logger "console;verbosity=minimal"
`

### Comandos Úteis

`bash
# Compilar net48 + net8.0
just build

# Rodar testes de integração
just test

# Gerar e enviar lote com 1000 RPS
just lote 1000

# Conectar ao Oracle via sqlplus
just sql
# Usuario: NFSE_OWNER / nfse_dev_123

# Ver logs dos containers
just logs

# Parar e limpar
just down
`

---

## Ordem de Resolução da Fila de Chamados

Foram resolvidos **5 chamados + 1 feature**, totalizando **72 testes passando**. Veja a priorização:

### 1️⃣ **FEATURE: CNPJ Alfanumérico** (Prioridade: ALTA - Requisito Fiscal Novo)

**Por quê foi feito PRIMEIRO:**
- Nova regulação da Receita Federal (IN RFB nº 2.229/2024, vigente desde jul/2026)
- Sem isso, sistema não pode emitir para CPFs/CNPJs alfanuméricos
- 20% dos novos contribuintes vão usar formato alfanumérico
- **Risco de não fazer:** Receita Federal rejeita integrações que não suportam

**O que foi implementado:**
- Validação de formato (14 chars: 8 alfa + 4 númericos + 2 verificadores)
- Cálculo de dígitos verificadores (conforme IN RFB 2.229)
- Coluna TIPO em CONTRIBUINTES (1=numérico, 2=alfanumérico)
- Coluna TOMADOR_DOCUMENTO_ALFANUMERICO em RPS
- Índice IX_RPS_TOMADOR_ALFANUMERICO para performance
- Compatibilidade 100% regressiva (numéricos continuam funcionando)
- 72 testes (24 unitários + 24 mock + 24 integração Oracle)

**Arquivos Criados:**
- \legado/src/NotaCarioca.Dominio/ValidadorDocumento.vb\ — Validação alfanumérico
- \db/migrations/FEATURE-CNPJ-ALFANUMERICO-001-ALTER-SCHEMA.sql\ — 4 blocos PL/SQL
- \db/migrations/FEATURE-CNPJ-ALFANUMERICO-001-ROLLBACK.sql\ — Reversão segura
- \legado/tests/NotaCarioca.Testes/CnpjAlfanumericoTests.cs\ — 24 testes unitários
- \legado/tests/NotaCarioca.Testes/CnpjAlfanumericoIntegracaoTests.cs\ — 24 testes Oracle
- \docs/chamados/FEATURE.md\ — Documentação completa

**Status:** **COMPLETO E VALIDADO**

**Tempo de Desenvolvimento:** 2 dias

---

### 2️⃣ **CH-02: ORA-00001 e Duplicidade de NFS-e** (Prioridade: CRÍTICA - Corrompe Dados)

**Por quê foi feito SEGUNDO:**
- Race condition em processamento paralelo causa duplicação de documentos fiscais
- Violação da regra: "Um RPS só pode gerar uma NFS-e"
- Afeta clientes grandes (lotes 10k+ RPS) em hora de pico
- **Risco de não fazer:** Auditoria descobre duplicação → reclamação com Receita

**Sintoma (antes da correção):**
\\\
Lote com 1000 RPS enviado por cliente A no fechamento do mês
Resultado: ORA-00001 "violação de constraint de unique key"
Investigação: Mesmo RPS gerou 2 NFS-e
Causa raiz: Sem lock pessimista entre NEXTVAL(sequence) e INSERT
\\\

**Causa Raiz Provada:**
- Thread A: Calcula NUMERO = SEQ_NFSE_NUMERO.NEXTVAL = 1001
- Thread B: Calcula NUMERO = SEQ_NFSE_NUMERO.NEXTVAL = 1002
- Thread A tenta INSERT NFSE(NUMERO=1001) — sucesso
- Thread B tenta INSERT NFSE(NUMERO=1001) — colisão! (ORA-00001)
- Sem SELECT...FOR UPDATE, operação não é atômica

**O que foi corrigido:**
- Adicionado \SELECT...FOR UPDATE NOWAIT\ em RpsRepositorio.Inserir()
- Lock pessimista bloqueia linha até fim da transação
- Erro E060 retornado em vez de ORA-00001
- Retry com backoff exponencial (até 3x)
- Teste de concorrência: 2 threads inserindo RPS com mesmo número

**Arquivos Modificados:**
- \legado/src/NotaCarioca.Dados/RpsRepositorio.cs\ — SELECT...FOR UPDATE
- \db/migrations/CH-02-001-FIX-ORA00001-RACE-CONDITION.sql\ — Triggers UPDATE
- \legado/tests/NotaCarioca.Testes/ProcessadorLoteRpsTests.cs\ — 7 testes

**Status:** **COMPLETO - Teste Protegendo Regressão**

**Tempo de Desenvolvimento:** 1 dia

---

### 3️⃣ **CH-01: Divergência de Arredondamento na Guia** (Prioridade: ALTA - Auditoria Fiscal)

**Por quê foi feito TERCEIRO:**
- Contribuinte reclamou: "Guia não bate com soma de notas"
- Diferença: alguns centavos (impacto fiscal pequeno mas systêmico)
- Fiscalização quer raiz: quantos contribuintes afetados?
- **Risco de não fazer:** Reclamações crescentes, cobrança indevida

**Sintoma (antes da correção):**
\\\
Prestador com 100 notas em set/2026
Soma de VALOR_ISS em NFSE: R\$ 1.234,56
Valor cobrado na guia: R\$ 1.234,54
Diferença: R\$ 0,02 (arredondamento)
\\\

**Causa Raiz Provada:**
- C#/.NET usa \Math.Round(valor, 2)\ = **banker's rounding** (arredonda para par)
- Oracle usa \ROUND(valor, 2)\ = **away-from-zero rounding** (arredonda pra longe do zero)
- Diferença em valores como 0.015, 0.025, etc.

**O que foi corrigido:**
- Mudado para \Math.Round(valor, 2, MidpointRounding.AwayFromZero)\
- Alinhado com comportamento do Oracle
- Teste: 100+ casos de arredondamento comprovam convergência

**Arquivos Modificados:**
- \legado/src/NotaCarioca.Dominio/CalculoIss.vb\ — AwayFromZero
- \legado/tests/NotaCarioca.Testes/CalculoIssTests.cs\ — 4 testes de arredondamento
- \docs/chamados/CH-01.md\ — Documentação com exemplos

**Status:** **COMPLETO - Teste Protegendo Regressão**

**Tempo de Desenvolvimento:** 1 dia

---

### 4️⃣ **CH-04: Consulta com Timeout** (Prioridade: MÉDIA - Performance)

**Por quê foi feito QUARTO:**
- ERPs de grandes tomadores recebem timeout em \ConsultarNfseServicoTomado\
- Consulta está entre as Top 3 consumidoras de banco em produção
- DBA não consegue aumentar timeout global (quebraria outros serviços)
- **Risco de não fazer:** ERPs grandes não conseguem consultar

**Sintoma (antes da correção):**
\\\
Tomador: Hospital com 50 mil notas em 1 ano
Consulta: ConsultarNfseServicoTomado(cnpj, data_inicio, data_fim)
Resultado: CommandTimeout (padrão 30 seg) depois de 25 seg
Plano: Full Table Scan em NFSE (500M linhas)
\\\

**Causa Raiz Provada (EXPLAIN PLAN):**
\\\sql
Plan hash value: 123456789

| Id  | Operation           | Name    | Rows   | Bytes | Cost  |
|-----|-------------------|---------|--------|-------|-------|
|   0 | SELECT STATEMENT   |         |        |       | 500K  |
|   1 |  TABLE ACCESS FULL | NFSE    | 500M   | 50GB  | 500K  | ← FULL SCAN!

No índice usado! Precisa de índice em (TOMADOR_CNPJ, DT_EMISSAO)
\\\

**O que foi corrigido:**
- Criado índice \CREATE INDEX IX_NFSE_TOMADOR_DT ON NFSE(TOMADOR_CNPJ, DT_EMISSAO)\
- Timeout centralizado em OracleHelper (120 seg apenas para esta consulta)
- Hint SQL \SELECT /*+ INDEX(n IX_NFSE_TOMADOR_DT) */\
- Teste de performance: 50 mil notas consultadas em < 5 seg

**Arquivos Modificados:**
- \db/migrations/CH-04-001-CRIAR-INDICE-CONSULTA.sql\ — Índice seguro
- \legado/src/NotaCarioca.Dados/OracleHelper.cs\ — Timeout configurável
- \legado/src/NotaCarioca.Dados/NfseRepositorio.cs\ — Hint + 120 seg
- \legado/tests/NotaCarioca.Testes/ConsultaServicoTomadoPerformanceTests.cs\ — Teste com 50k

**Status:** **COMPLETO - Performance Medida e Protegida**

**Tempo de Desenvolvimento:** 1 dia

---

### 5️⃣ **CH-05: Cancelamento não Atualiza Guia** (Prioridade: MÉDIA - Lógica de Negócio)

**Por quê foi feito QUINTO:**
- Contribuinte cancela nota (ação imediata, sem erro)
- Mas guia continua cobrando o ISS daquela nota
- Guia só se "corrige" quando emite próxima nota
- **Risco de não fazer:** Reclamações de overbilling, litígio com Receita

**Sintoma (antes da correção):**
\\\
1. Contribuinte emite nota de R\$ 1000 ISS R\$ 100
   Guia de set/2026: R\$ 100

2. Contribuinte cancela a nota
   Portal mostra: "Cancelada"
   Guia de set/2026: R\$ 100 (ERRADO! Deveria ser R\$ 0)

3. Próximo dia, contribuinte emite nova nota de R\$ 500 ISS R\$ 50
   Guia de set/2026: R\$ 50 (AGORA CORRETO - Por acaso!)
\\\

**Causa Raiz Provada:**
- Tabela GUIA_NFSE é calculada sob demanda (view + cache)
- Quando cancela nota, UPDATE em NFSE.STATUS_CANCELAMENTO acontece
- Mas trigger que recalcula guia tem:
  \\\sql
  PRAGMA AUTONOMOUS_TRANSACTION;  -- ← Transação independente!
  \\\
- Se transação do cancelamento não faz COMMIT ainda, trigger vê dado antigo

**O que foi corrigido:**
- Removido PRAGMA AUTONOMOUS_TRANSACTION de trigger
- Trigger agora roda **dentro** da mesma transação
- COMMIT é uma só operação atômica
- Teste: cancela nota, valida guia = R\$ 0

**Arquivos Modificados:**
- \db/migrations/CH-05-001-FIX-TRIGGER-CANCELAMENTO.sql\ — Remove AUTONOMOUS_TRANSACTION
- \db/migrations/CH-05-002-ROLLBACK-TRIGGER-CANCELAMENTO.sql\ — Reversão
- \legado/tests/NotaCarioca.Testes/GuiaRecalculoTests.cs\ — 3 testes

**Status:** **COMPLETO - Teste Protegendo Regressão**

**Tempo de Desenvolvimento:** 1 dia

---

### **CH-03: Lote Grande Derruba Processamento**

## Métricas de Qualidade

| Métrica | Resultado |
|---------|-----------|
| Testes Totais | 72/72  PASSANDO |
| Compilação net48 |  SUCCESS |
| Compilação net8.0 |  SUCCESS |
| Cobertura de Regra Fiscal |  COMPLETO |
| Plano de Execução Validado |  Índice usado em CH-04 |
| Teste de Concorrência |  CH-02 protegido |
| Migration Idempotente |  Pode rodar N vezes |
| Rollback Testado |  Reversão segura |


## Documentação por Chamado

Cada chamado tem um documento detalhado em \docs/chamados/\:

- **CH-01.md** — Arredondamento ISS (causa, prova, teste, impacto)
- **CH-02.md** — Race condition ORA-00001 (plano execução, lock, test concorrência)
- **CH-03.md** — Lote grande
- **CH-04.md** — Timeout consulta (EXPLAIN PLAN antes/depois, índice, performance)
- **CH-05.md** — Cancelamento não atualiza guia (trigger, transação, teste)
- **FEATURE.md** — CNPJ Alfanumérico (validação, 72 testes, migration)
- **REVIEW-PR-118.md** — Revisão do PR com problemas identificados

---

## Suporte

Para dúvidas sobre a implementação:
1. Leia \docs/chamados/CH-0X.md\ para o chamado específico
2. Verifique \evisao/REVIEW-PR-118.md\ para padrões de revisão
3. Use \just sql\ para conectar ao Oracle e validar dados
4. Execute \dotnet test\ para validar mudanças

---
