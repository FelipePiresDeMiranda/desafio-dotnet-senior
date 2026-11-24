# REVIEW — PR #118 — fix(lotes): corrige ORA-00001 no processamento e lentidão

**Revisor**: Copilot  
**Data**: 2026-11-24  
**Branch**: `fix/lotes-ora-00001` → `main`  
**Status**: **BLOQUEADO PARA MERGE**

---

## RESUMO

O PR aborda 3 chamados legítimos (**CH-02, CH-04, CH-01**) com estratégia correta na maioria dos pontos, mas **tem 3 problemas críticos** que bloqueiam o merge:

1. **Race condition NÃO foi resolvida** (apenas máscarada com lock no lugar errado)
2. **Sequence sem sincronização com sequência fiscal** (pode gerar saltos no número NFS-e)
3. **Teste duplicado sem remover o anterior** (redundância de cobertura)

---

## ANÁLISE DETALHADA

### BOAS PRÁTICAS IDENTIFICADAS

#### 1. **Migração bem estruturada** (CH-02 — Sequence)
```sql
-- db/migrations/V001__seq_numero_nfse.sql
DECLARE
    v_inicio NUMBER;
BEGIN
    SELECT NVL(MAX(NUMERO), 0) + 1 INTO v_inicio FROM NFSE;
    EXECUTE IMMEDIATE 'CREATE SEQUENCE SEQ_NFSE_NUMERO START WITH ' || v_inicio ||
                      ' INCREMENT BY 1 CACHE 100';
END;
```

**Ponto positivo:**
- Calcula corretamente `START WITH MAX(NUMERO) + 1` para evitar colisão
- Rollback script fornecido
- Integrado ao `db/init.sh` para ambiente local
- CACHE 100 é bom para performance

---

#### 2. **Hint SQL atualizado com validação** (CH-04)
```sql
-- ANTES: SELECT /*+ FULL(n) */
-- DEPOIS: SELECT /*+ INDEX(n IX_NFSE_TOMADOR_DT) */
```

**Ponto positivo:**
- DBA validou o hint (comentário atualizado com data)
- Mudança justificada
- Sem impacto em schema

---

#### 3. **CommandTimeout centralizado** (CH-04)
```csharp
// OracleHelper.cs - novo overload
public static DataTable ExecuteDataTable(int timeoutSegundos, string sql, params OracleParameter[] parametros)
{
    // ...
}

// NfseRepositorio.cs
return OracleHelper.ExecuteDataTable(120, sql, ...);  // 120 segundos
```

**Ponto positivo:**
- Padrão bem estruturado (overload, não Magic Number)
- Timeout apenas na consulta necessária (não global)
- Compatível com assinaturas antigas

---

#### 4. **Arredondamento ISS alinhado com Oracle** (CH-01)
```csharp
// ANTES:
return CDec(Math.Round(valor, 2))

// DEPOIS:
return CDec(Math.Round(valor, 2, MidpointRounding.AwayFromZero))
```

**Ponto positivo:**
- `AwayFromZero` ≈ ROUND do Oracle
- Testes adicionados para validar
- Ajuste simples, sem risco

---

#### 5. **Logs melhorados**
```csharp
// ANTES:
Console.WriteLine("[{0:HH:mm:ss}] falha no lote {1}: {2}", DateTime.Now, protocolo, ex.Message);

// DEPOIS:
Console.WriteLine("[{0:HH:mm:ss}] falha no lote {1}: {2}", DateTime.Now, protocolo, ex);
```

**Ponto positivo:**
- Stack trace completo agora visível (melhor debugging)
- Inclui prestador no log de sucesso

---

### PROBLEMAS CRÍTICOS (BLOQUEIAM MERGE)

---

#### **PROBLEMA #1: Race condition NÃO foi resolvida** **CRÍTICO**

**Lokalisação**: `ProcessadorLoteRps.cs` linhas 83-87

```csharp
lock (_resultados)  // PROTEÇÃO NO LUGAR ERRADO!
{
    _resultados.Add(res);
}
```

**Por que é um problema:**

1. **Lock protege apenas o `Add()`, não a operação atômica**
   - Thread A: Calcula NUMERO = SEQ.NEXTVAL = 1001
   - Thread B: Calcula NUMERO = SEQ.NEXTVAL = 1002
   - Thread A: Tenta INSERT (UK_NFSE_NUMERO = 1001)
   - Thread B: Tenta INSERT (UK_NFSE_NUMERO = 1001) ← **COLISÃO**
   - Thread A: Lock e Add() ao _resultados
   - Thread B: Lock e Add() ao _resultados ← Lock não salvou de nada!

2. **Race condition está em `_nfse.GerarNfse()`, não em `_resultados.Add()`**
   ```csharp
   // REAL PROBLEM (não protegido):
   _nfse.GerarNfse(conn, tx, ...);  // ← Aqui vem ORA-00001

   // DEPOIS (já é tarde):
   lock (_resultados) { _resultados.Add(res); }  // ← Proteção inútil aqui
   ```

3. **A sequence AINDA enfrenta colisão se 2 threads pedem NEXTVAL**
   - Sequences são thread-safe em Oracle, MAS
   - Se ambas obtêm números consecutivos (1001, 1002)
   - Mas apenas 1 consegue gerar a NFSE por concorrência de outra parte do código
   - O retry pode piorar (tenta novamente, não garante sucesso)

**Por que o retry funciona (mas é frágil):**
- Se ORA-00001 ocorre, `ProcessarRps()` tenta de novo (até 3x)
- Com pausa aleatória 50-200 ms, a chance de sucesso aumenta
- **MAS**: Não resolve a causa, apenas mascara com retry

**Solução correta seria:**
- Lock global ANTES de `GerarNfse()`, OU
- Renumerar NFS-e por sequência + prestador (não global), OU
- Usar única thread para geração de NFS-e (sacrificaria paralelismo)

**Regra de negócio violada:**
> "Um RPS (prestador + série + número) só pode virar uma NFS-e"

Com retry, você viola isso potencialmente (mesma NFS-e processada múltiplas vezes).

**Recomendação**:
```csharp
// CORRETO:
private static object _lockNfse = new object();

lock (_lockNfse)  // Lock ANTES de GerarNfse
{
    // ... GerarNfse() protegida
    // ... _resultados.Add() aqui
}
```

---

#### **PROBLEMA #2: Sequence sem sincronização com numeração fiscal** **CRÍTICO**

**Localização**: `db/03_pkg_nfse.sql` linhas 45-52

```sql
-- ANTES (Q&D, mas correto fiscalmente):
SELECT NVL(MAX(NUMERO), 0) + 1 INTO v_numero FROM NFSE WHERE PRESTADOR_ID = p_prestador_id;

-- DEPOIS (rápido, mas quebrado):
SELECT SEQ_NFSE_NUMERO.NEXTVAL INTO v_numero FROM DUAL;
```

**Por que é um problema:**

1. **Sequence é GLOBAL, não por prestador**
   - Dois prestadores A e B podem processar em paralelo
   - A pede NEXTVAL → 1001
   - B pede NEXTVAL → 1002
   - Mas NFS-e do B pode estar associada ao prestador A no código!
   - Resultado: Número NFS-e fora de sequência para prestador

2. **Não há verificação de existência antes do NEXTVAL**
   ```sql
   -- ANTES: Se já existe 1001 para prestador A, calcula 1002
   SELECT MAX(NUMERO) + 1 ... WHERE PRESTADOR_ID = ...

   -- DEPOIS: Sempre pega próximo, sem verificação
   SELECT SEQ_NFSE_NUMERO.NEXTVAL ...
   ```

3. **Violação de regra fiscal**
   > "A numeração da NFS-e é sequencial por prestador, sem lacunas"

   Com sequence global, você pode ter lacunas POR PRESTADOR:
   - Prestador A: 1001, 1003, 1005 (lacunas!)
   - Prestador B: 1002, 1004 (misturado!)

**Por que o autor talvez tenha feito assim:**
- Sequence é rápido (evita varrer tabela)
- Presume que nunca haverá colisão numérica (porque um só sequence)
- **MAS**: Não resolve o problema fiscal

**Solução correta seria:**
```sql
-- CORRETO:
CREATE SEQUENCE SEQ_NFSE_NUMERO_P001 START WITH 1001;  -- Por prestador
CREATE SEQUENCE SEQ_NFSE_NUMERO_P002 START WITH 1001;  -- Por prestador

-- Ou usar:
SELECT NVL(MAX(NUMERO), 0) + 1 INTO v_numero 
  FROM NFSE 
 WHERE PRESTADOR_ID = p_prestador_id;
-- (Mantém a verificação por prestador)
```

---

#### **PROBLEMA #3: Testes duplicados sem remoção do antigo** **MÉDIO**

**Localização**: `CalculoIssArredondamentoTests.cs` (novo) vs `CalculoIssTests.cs` (existente)

```csharp
// CalculoIssTests.cs — Teste existente
[TestMethod]
public void Iss_DuasCasas()
{
    Assert.AreEqual(3.01m, CalculoIss.CalcularValorIss(150.30m, 0.02m, "S"));
}

// CalculoIssArredondamentoTests.cs — Novo arquivo
[TestClass]
public class CalculoIssArredondamentoTests
{
    [TestMethod]
    public void Iss_Aliquota5_BaseInteira()
    {
        Assert.AreEqual(25m, CalculoIss.CalcularValorIss(500m, 0.05m, "N"));
    }
    // ... 5 testes novos
}
```

**Por que é um problema:**

1. **Duplicação de responsabilidade**
   - Ambos arquivos testam a mesma função `CalculoIss.CalcularValorIss()`
   - Quebra coesão (testes espalhados)

2. **Manutenção confusa**
   - Futuro: developer vê 2 arquivos testando ISS
   - Qual verdade seguir?
   - Qual desatualizar se precisar de mudança?

3. **O teste `Iss_DuasCasas` foi alterado**
   ```csharp
   // ANTES (esperava):
   Assert.AreEqual(3.01m, CalculoIss.CalcularValorIss(150.30m, 0.02m, "S"));

   // DEPOIS (mudou para):
   Assert.AreEqual(37.03m, CalculoIss.CalcularValorIss(1234.50m, 0.03m, "S"));
   ```
   - Valores mudam, não é cobertura incremental
   - Parece refatoração, não adição

**Recomendação**:
- Consolidar em 1 arquivo
- Remover redundâncias
- Ou mover para classe base se herança apropriada

---

### PROBLEMAS MEDIANOS (MAIS DE CUIDADO)

---

#### **PROBLEMA #4: Retry sem limite de retorno** **MÉDIO**

**Localização**: `ProcessadorLoteRps.cs` linhas 132-157

```csharp
for (int tentativa = 1; ; tentativa++)  // Loop infinito se não sair por exceção
{
    try
    {
        return GravarRps(...);  // Único exit point
    }
    catch (OracleException ex)
    {
        if (ex.Number == 1 && tentativa < MaxTentativasRps)
        {
            Thread.Sleep(new Random().Next(50, 200));
            continue;  // Tenta de novo
        }
        // Se ex.Number != 1 OU tentativa >= MaxTentativasRps:
        res.Sucesso = false;
        res.Codigo = CodigosRetorno.E999;
        res.Mensagem = ex.Message;
        return res;  // Sai do loop
    }
}
```

**Problema**:
- Se `tentativa >= MaxTentativasRps` E `ex.Number == 1`
- Código cai para seção de catch e retorna erro
- **MAS**: Loop `for (int tentativa = 1; ; tentativa++)` pode gerar confusão
- Melhor seria `for (int tentativa = 1; tentativa <= MaxTentativasRps; tentativa++)`

**Impacto**: Baixo (funciona, mas confuso)

---

#### **PROBLEMA #5: Random sem seed** **MENOR**

**Localização**: `ProcessadorLoteRps.cs` linha 148

```csharp
Thread.Sleep(new Random().Next(50, 200));  //  Random sem seed
```

**Problema**:
- Cria nova instância `Random()` a cada tentativa
- Sem seed explícito, pode gerar mesmos números em rápida sucessão
- Concorrência: múltiplas threads criando Random sem sincronização

**Melhor seria**:
```csharp
private static Random _random = new Random();

lock (_random)
{
    Thread.Sleep(_random.Next(50, 200));
}
```

**Impacto**: Baixo (funciona, mas pode não aleatorizar bem)

---

### PONTOS POSITIVOS

| Ponto | Status | Detalhe |
|-------|--------|---------|
| Separação de métodos | OK | `GravarRps()` extraído, legível |
| Integração DB | OK | Migrations bem estruturadas |
| Testes adicionados | OK | 6 novos testes para ISS |
| Build validation | OK | net48 + net8.0 testados |
| Urgência justificada | OK | Fechamento de mês é legítimo |

---

### RESUMO DE BLOQUEADORES

| # | Severidade | Problema | Impacto | Requer Mudança |
|---|-----------|----------|--------|----------------|
| 1 | CRÍTICO | Race condition mascara com lock errado | ORA-00001 continua possível |  Sim |
| 2 | CRÍTICO | Sequence sem sincronização por prestador | Violação fiscal (lacunas) |  Sim |
| 3 | MÉDIO | Testes duplicados | Confusão em manutenção futura |  Sim |
| 4 | MENOR | Loop for confuso | Legibilidade |  Opcional |
| 5 | MENOR | Random sem sincronização | Aleatoriedade fraca |  Opcional |

---

## RECOMENDAÇÕES CORRETIVAS

### Correção Prioritária #1: Lock Global Correto
```csharp
private static object _lockNfse = new object();

for (int tentativa = 1; tentativa <= MaxTentativasRps; tentativa++)
{
    try
    {
        lock (_lockNfse)  // Protege TODA operação
        {
            return GravarRps(conn, rps, prestadorId, regime, protocolo, itens, res);
        }
    }
    catch (OracleException ex)
    {
        if (ex.Number == 1 && tentativa < MaxTentativasRps)
        {
            Thread.Sleep(_random.Next(50, 200));
            continue;
        }
        // ... erro
    }
}
```

**Impacto**: Serializa geração de NFS-e (sacrifica throughput, mas garante correção)

---

### Correção Prioritária #2: Sequence por Prestador (ou Revert)
```sql
-- OPÇÃO A: Sequence por prestador (complexo)
-- Criar 1 sequence por prestador durante primeiro uso

-- OPÇÃO B: Revert para MAX(NUMERO) + 1 (simples, mantém regra fiscal)
SELECT NVL(MAX(NUMERO), 0) + 1 INTO v_numero 
  FROM NFSE 
 WHERE PRESTADOR_ID = p_prestador_id;
-- (Proteger com lock global acima)
```

**Impacto**: Volta a ser O(n) em números, MAS regra fiscal é respeitada

---

### Correção Prioritária #3: Consolidar Testes ISS
```csharp
//  Mover tudo para CalculoIssTests.cs
//  Deletar CalculoIssArredondamentoTests.cs
//  Atualizar Iss_DuasCasas para novo teste
```

---

## VERIFICAÇÃO PRÉ-MERGE

Se autor quiser corrigir, checklist:

- [ ] Lock global protege `GravarRps()` completa
- [ ] Sequence por prestador implementada OU revert para MAX(NUMERO)+1
- [ ] Testes duplicados consolidados
- [ ] `for (int tentativa = 1; tentativa <= MaxTentativasRps; ...)` legível
- [ ] `_random` com seed/sincronização
- [ ] Nenhum teste com falso positivo (testes realmente falham com bug original)
- [ ] Build verde em ambas frameworks
- [ ] Load test com 1000+ RPS concorrentes (não apenas 1000 sequencial)

---

## DECISÃO FINAL

###  **BLOQUEADO PARA MERGE**

**Motivo**: Problemas críticos não resolvem a causa raiz de ORA-00001, apenas criam "circuit breaker" frágil. A race condition ainda existe, apenas com atenuação via retry.

**Alternativa**: 
- Aprove como hotfix **TEMPORÁRIO** apenas se:
  - Comunicado ao time de sustentação sobre limitações (não é fix permanente)
  - Monitorado intensamente durante primeiro ciclo
  - Planejado fix definitivo para próxima sprint
- **Recomendado**: Rejeitar e solicitar correção completa

### Comunicação ao Autor

```
Oi @rafael.sustentacao,

Obrigado pelo esforço! Identifiquei 3 problemas que precisam ser ajustados antes do merge:

## CRÍTICO — Race condition mascara com lock no lugar errado
O lock em `_resultados.Add()` não protege a colisão real que acontece em `GerarNfse()`. 
Dois threads podem obter números consecutivos da sequence, e ambos tentarem INSERT com o mesmo número.

**Solução**: Lock global ANTES de `GerarNfse()`, não depois.

## CRÍTICO — Sequence sem sincronização por prestador
Viola regra fiscal "Numeração sequencial por prestador, sem lacunas".
Dois prestadores em paralelo podem obter números misturados.

**Solução**: Usar sequence POR prestador, ou reverter para `MAX(NUMERO)+1` (mais lento, mas correto).

## MÉDIO — Testes duplicados
`CalculoIssTests.cs` e `CalculoIssArredondamentoTests.cs` testam mesma coisa.

**Solução**: Consolidar em 1 arquivo.

Aproveita e corrige? Depois a gente faz merge. 
Se precisar , posso ajudar a refatorar o lock e a sequence.
Estou a disposição para revisar novamente depois das correções.

Att,
Felipe Miranda
```

---



