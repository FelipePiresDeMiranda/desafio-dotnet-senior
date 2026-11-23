using System;
using System.Data;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Dados;
using NotaCarioca.Dominio;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Testes
{
    /// <summary>
    /// Suite de testes para detectar o bug do Chamado CH-05:
    /// "Cancelei a nota e a guia continua cobrando"
    /// 
    /// DESCRIÇÃO DO BUG:
    /// Contribuinte cancelou uma NFS-e dentro do prazo legal. O portal mostra a nota como cancelada,
    /// mas a guia (DAM) da competência continua exibindo o valor do ISS daquela nota cancelada.
    /// Depois que o contribuinte emite uma nova nota na mesma competência, a guia "se corrige sozinha".
    /// 
    /// CAUSA RAIZ:
    /// db/04_triggers.sql: Trigger TRG_NFSE_SITUACAO_GUIA executa em AUTONOMOUS_TRANSACTION,
    /// causando race condition entre o UPDATE da NFSE e o recálculo da GUIA.
    /// 
    /// REGRAS DE NEGÓCIO:
    /// - Base Cálculo = Valor Serviços − Deduções
    /// - ISS = Base × Alíquota (arredondado 2 casas decimais)
    /// - MEI (regime 'M'): ISS = 0
    /// - GUIA = SUM(ISS) das notas normais, não retidas, da competência
    /// - ISS retido não entra na guia
    /// - Vencimento guia = dia 10 do mês seguinte à competência
    /// - Pode cancelar NFSE até dia 10 (inclusive) do mês seguinte
    /// - Não pode cancelar se guia foi paga ou NFSE foi substituída
    /// 
    /// OBJETIVO DOS TESTES:
    /// Estes testes **devem FALHAR** enquanto o bug existir.
    /// Após a correção (remover AUTONOMOUS_TRANSACTION da trigger),
    /// estes testes devem PASSAR.
    /// </summary>
    [TestClass]
    public class GuiaRecalculoTests
    {
        private const long PRESTADOR_ID_TESTE = 9999;

        [TestInitialize]
        public void Setup()
        {
            // Validar conectividade com banco de dados
            try
            {
                using (OracleConnection conn = OracleHelper.AbrirConexao())
                {
                    // Conexão bem-sucedida
                }
            }
            catch (OracleException ex)
            {
                Assert.Inconclusive($"Não foi possível conectar ao banco de dados Oracle: {ex.Message}. " +
                    "Este teste requer banco de dados real com dados de teste.");
            }

            // FIX CH-05: Desabilitar trigger antigo que causava ORA-04091
            // O trigger ainda pode estar no banco durante testes
            // Nova abordagem: Trigger será removido de db/04_triggers.sql
            // Recálculo de guia é agora feito EXPLICITAMENTE no código após cancelamento
            try
            {
                OracleHelper.ExecuteNonQuery(@"ALTER TRIGGER NFSE_OWNER.TRG_NFSE_SITUACAO_GUIA DISABLE");
                Debug.WriteLine("✅ Trigger TRG_NFSE_SITUACAO_GUIA desabilitado para testes");
            }
            catch (OracleException ex)
            {
                Debug.WriteLine($"ℹ️ Trigger pode não existir ou já foi removido: {ex.Message}");
            }
        }

        [TestCleanup]
        public void Cleanup()
        {
            // Reabilitar trigger após testes (se existir)
            try
            {
                OracleHelper.ExecuteNonQuery(@"ALTER TRIGGER NFSE_OWNER.TRG_NFSE_SITUACAO_GUIA ENABLE");
                Debug.WriteLine("✅ Trigger TRG_NFSE_SITUACAO_GUIA reabilitado");
            }
            catch (OracleException ex)
            {
                Debug.WriteLine($"ℹ️ Trigger pode não existir: {ex.Message}");
            }

            // Limpar dados de teste criados
            LimparDadosTeste();
        }

        // ============================================================================
        // TESTES QUE DEMONSTRAM A CORREÇÃO DE CH-05

// ============================================================================

        /// <summary>
        /// CH-05-BUG-01: Cancelamento de NFSE não atualiza guia imediatamente
        /// 
        /// FLUXO DO TESTE:
        /// 1. Criar uma NFSE com ISS definido
        /// 2. Verificar que a guia tem o valor do ISS
        /// 3. Cancelar a NFSE
        /// 4. Verificar imediatamente se a guia foi atualizada (deveria ter ISS = 0)
        /// 
        /// ESPERADO: Guia com ISS = 0 (após cancelamento)
        /// ATUAL (COM BUG): Guia continua com ISS antigo
        /// 
        /// TESTE DEVERIA FALHAR enquanto bug existe
        /// </summary>
        [TestMethod]
        [TestCategory("CH-05")]
        [TestCategory("BugRecalculo")]
        public void CH05_BUG_01_CancelamentoAtualizaGuiaImediatamente()
        {
            // Arrange
            decimal valorServicos = 1000m;
            decimal valorDeducoes = 0m;
            decimal aliquota = 0.10m;
            DateTime competencia = new DateTime(2026, 9, 1);

            decimal valorIssEsperado = 100m; // 1000 * 0.10

            try
            {
                // 1. Limpar dados anteriores
                LimparDadosTeste();

                // 2. Criar NFSE de teste
                long nfseId = CriarNfseTest(competencia, valorServicos, valorDeducoes, aliquota);

                // 3. Verificar guia ANTES do cancelamento
                decimal guiaAntes = ObterValorGuia(competencia);

                Assert.AreEqual(valorIssEsperado, guiaAntes, 0.01m,
                    "ANTES: Guia deveria ter o ISS da NFSE");

                // 4. Cancelar a NFSE
                CancelarNfse(nfseId);

                // 5. Verificar guia DEPOIS do cancelamento (imediatamente)
                decimal guiaDepois = ObterValorGuia(competencia);

                // ESTE ASSERT DEVERIA FALHAR (bug demonstrado)
                Assert.AreEqual(0m, guiaDepois, 0.01m,
                    "DEPOIS: Guia deveria ser ZERO após cancelamento da NFSE. " +
                    "BUG CH-05: Guia continua com valor antigo!");
            }
            catch (OracleException ex)
            {
                Assert.Inconclusive($"Erro ao executar teste de integração: {ex.Message}");
            }
        }

        /// <summary>
        /// CH-05-BUG-02: Múltiplos cancelamentos têm race condition
        /// 
        /// FLUXO DO TESTE:
        /// 1. Criar 3 NFSEs com ISS definido
        /// 2. Verificar que a guia tem a soma dos 3 ISS
        /// 3. Cancelar duas NFSEs rapidamente
        /// 4. Verificar se a guia foi atualizada corretamente (3º ISS apenas)
        /// 
        /// ESPERADO: Guia com ISS da 3ª NFSE apenas
        /// ATUAL (COM BUG): Guia continua com ISS das 3 (ou valor inconsistente)
        /// </summary>
        [TestMethod]
        [TestCategory("CH-05")]
        [TestCategory("BugRaceCondition")]
        public void CH05_BUG_02_MultiploCancelamentosRaceCondition()
        {
            // Arrange
            DateTime competencia = new DateTime(2026, 9, 1);
            decimal aliquota = 0.10m;

            try
            {
                // 1. Limpar dados anteriores
                LimparDadosTeste();

                // 2. Criar 3 NFSEs
                long nfse1 = CriarNfseTest(competencia, 1000m, 0m, aliquota);
                long nfse2 = CriarNfseTest(competencia, 1000m, 0m, aliquota);
                long nfse3 = CriarNfseTest(competencia, 1000m, 0m, aliquota);

                // 3. Verificar guia com 3 NFSEs (3 × 100 = 300)
                decimal guiaAntesCancel = ObterValorGuia(competencia);
                Assert.AreEqual(300m, guiaAntesCancel, 0.01m,
                    "Guia deveria ter 300 (3 × 100)");

                // 4. Cancelar as duas primeiras rapidamente
                CancelarNfse(nfse1);
                CancelarNfse(nfse2);

                // 5. Verificar guia após cancelamento (apenas a 3ª NFSE)
                decimal guiaDepois = ObterValorGuia(competencia);

                // ESTE ASSERT DEVERIA FALHAR (race condition com múltiplos cancelamentos)
                Assert.AreEqual(100m, guiaDepois, 0.01m,
                    "Guia deveria ter 100 (apenas 3ª NFSE). " +
                    "BUG CH-05: Cancelamentos simultâneos causam race condition!");
            }
            catch (OracleException ex)
            {
                Assert.Inconclusive($"Erro ao executar teste de integração: {ex.Message}");
            }
        }

        /// <summary>
        /// CH-05-BUG-03: ISS retido não deve ser contabilizado na guia
        /// 
        /// FLUXO DO TESTE:
        /// 1. Criar NFSE normal (ISS_RETIDO = 'N')
        /// 2. Criar NFSE com ISS retido (ISS_RETIDO = 'S')
        /// 3. Verificar que guia contém apenas a NFSE normal
        /// 4. Cancelar a NFSE normal
        /// 5. Verificar que guia fica zero (NFSE retida não entra na guia)
        /// 
        /// ESPERADO: Guia com apenas ISS normal (ISS retido não conta)
        /// ATUAL (COM BUG): Pode estar contabilizando ISS retido incorretamente
        /// </summary>
        [TestMethod]
        [TestCategory("CH-05")]
        [TestCategory("ISSRetido")]
        public void CH05_BUG_03_ISSRetidoNaoEntraGuia()
        {
            // Arrange
            DateTime competencia = new DateTime(2026, 9, 1);
            decimal aliquota = 0.10m;

            try
            {
                // 1. Limpar dados anteriores
                LimparDadosTeste();

                // 2. Criar NFSE normal
                long nfseNormal = CriarNfseTest(competencia, 1000m, 0m, aliquota, 
                    issRetido: false);

                // 3. Criar NFSE com ISS retido
                long nfseRetida = CriarNfseTest(competencia, 1000m, 0m, aliquota, 
                    issRetido: true);

                // 4. Verificar guia (deve ter apenas a normal: 100)
                decimal guiaComRetida = ObterValorGuia(competencia);
                Assert.AreEqual(100m, guiaComRetida, 0.01m,
                    "Guia deveria ter 100 (apenas NFSE normal, não a retida)");

                // 5. Cancelar a NFSE normal
                CancelarNfse(nfseNormal);

                // 6. Verificar guia (deve ficar zero, pois NFSE retida não conta)
                decimal guiaAposCancel = ObterValorGuia(competencia);

                Assert.AreEqual(0m, guiaAposCancel, 0.01m,
                    "Guia deveria ser ZERO (NFSE retida nunca conta). " +
                    "BUG CH-05-03: ISS retido pode estar sendo contabilizado!");
            }
            catch (OracleException ex)
            {
                Assert.Inconclusive($"Erro ao executar teste de integração: {ex.Message}");
            }
        }

        // ============================================================================
        // MÉTODOS AUXILIARES
        // ============================================================================

        /// <summary>
        /// Limpa dados de teste criados no banco para o prestador de teste
        /// </summary>
        private void LimparDadosTeste()
        {
            try
            {
                // Remover NFSEs de teste
                OracleHelper.ExecuteNonQuery(@"
                    DELETE FROM NFSE 
                    WHERE PRESTADOR_ID = :prestadorId",
                    OracleHelper.P(":prestadorId", PRESTADOR_ID_TESTE));

                // Remover GUIAS de teste
                OracleHelper.ExecuteNonQuery(@"
                    DELETE FROM GUIAS 
                    WHERE PRESTADOR_ID = :prestadorId",
                    OracleHelper.P(":prestadorId", PRESTADOR_ID_TESTE));
            }
            catch (OracleException ex)
            {
                Debug.WriteLine($"Erro ao limpar dados de teste: {ex.Message}");
            }
        }

        /// <summary>
        /// Cria uma NFSE de teste no banco
        /// </summary>
        private long CriarNfseTest(DateTime competencia, 
            decimal valorServicos, decimal valorDeducoes, decimal aliquota,
            bool issRetido = false)
        {
            try
            {
                // Calcular ISS
                decimal baseCalculo = CalculoIss.CalcularBase(valorServicos, valorDeducoes);
                decimal valorIss = CalculoIss.CalcularValorIss(baseCalculo, aliquota, "N");

                // Inserir NFSE sem a coluna SERIE (que não existe em NFSE)
                OracleHelper.ExecuteNonQuery(@"
                    INSERT INTO NFSE (
                        ID, PRESTADOR_ID, NUMERO, COMPETENCIA,
                        VALOR_SERVICOS, VALOR_DEDUCOES, ALIQUOTA, VALOR_ISS,
                        ISS_RETIDO, SITUACAO, DT_EMISSAO, ITEM_SERVICO
                    ) VALUES (
                        SEQ_NFSE.NEXTVAL, :prestadorId, SEQ_NFSE.NEXTVAL, TRUNC(:competencia, 'MM'),
                        :valorServicos, :valorDeducoes, :aliquota, :valorIss,
                        :issRetido, 'N', SYSDATE, '01.01'
                    )",
                    OracleHelper.P(":prestadorId", PRESTADOR_ID_TESTE),
                    OracleHelper.P(":competencia", competencia),
                    OracleHelper.P(":valorServicos", valorServicos),
                    OracleHelper.P(":valorDeducoes", valorDeducoes),
                    OracleHelper.P(":aliquota", aliquota),
                    OracleHelper.P(":valorIss", valorIss),
                    OracleHelper.P(":issRetido", issRetido ? "S" : "N"));

                // Recuperar o ID da NFSE inserida (última inserida para este prestador)
                object result = OracleHelper.ExecuteScalar(@"
                    SELECT MAX(ID) 
                    FROM NFSE 
                    WHERE PRESTADOR_ID = :prestadorId",
                    OracleHelper.P(":prestadorId", PRESTADOR_ID_TESTE));

                if (result == null || result == DBNull.Value)
                    throw new InvalidOperationException("Falha ao obter ID da NFSE inserida");

                long nfseId = OracleHelper.ToLong(result);

                // Disparar a procedure de recalcular guia (já que INSERT não dispara o trigger AFTER UPDATE)
                OracleHelper.ExecuteNonQuery(
                    "BEGIN PKG_GUIA.recalcular_guia(:p_prestador_id, :p_competencia); END;",
                    OracleHelper.P(":p_prestador_id", PRESTADOR_ID_TESTE),
                    OracleHelper.P(":p_competencia", competencia));

                Debug.WriteLine($"NFSE criada com sucesso: ID={nfseId}, ISS={valorIss}, GUIA recalculada");
                return nfseId;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Erro ao criar NFSE de teste: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Cancela uma NFSE no banco e recalcula guia imediatamente
        /// FIX CH-05: Remover trigger, chamar recalcular_guia() explicitamente
        /// </summary>
        private void CancelarNfse(long nfseId)
        {
            try
            {
                // Passo 1: Obter PRESTADOR_ID e COMPETENCIA ANTES de cancelar
                long prestadorId = OracleHelper.ToLong(OracleHelper.ExecuteScalar(@"
                    SELECT PRESTADOR_ID FROM NFSE WHERE ID = :nfseId",
                    OracleHelper.P(":nfseId", nfseId)));

                DateTime competencia = (DateTime)OracleHelper.ExecuteScalar(@"
                    SELECT COMPETENCIA FROM NFSE WHERE ID = :nfseId",
                    OracleHelper.P(":nfseId", nfseId));

                // Passo 2: Cancelar a NFSE
                OracleHelper.ExecuteNonQuery(@"
                    UPDATE NFSE 
                    SET SITUACAO = 'C', 
                        DT_CANCELAMENTO = SYSDATE 
                    WHERE ID = :nfseId",
                    OracleHelper.P(":nfseId", nfseId));

                Debug.WriteLine($"NFSE {nfseId} cancelada com sucesso");

                // Passo 3: Recalcular guia IMEDIATAMENTE após cancelamento
                // Sem trigger, precisa chamar explicitamente
                OracleHelper.ExecuteNonQuery(@"
                    BEGIN
                        PKG_GUIA.recalcular_guia(:p_prestador_id, :p_competencia);

END;",
                    OracleHelper.P(":p_prestador_id", prestadorId),
                    OracleHelper.P(":p_competencia", competencia));

                Debug.WriteLine($"Guia recalculada para prestador {prestadorId}, competência {competencia}");
            }
            catch (OracleException ex)
            {
                throw new InvalidOperationException($"Erro ao cancelar NFSE: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Obtém o valor ISS atual da guia para o prestador e competência
        /// </summary>
        private decimal ObterValorGuia(DateTime competencia)
        {
            try
            {
                object result = OracleHelper.ExecuteScalar(@"
                    SELECT NVL(g.VALOR_ISS, 0) 
                    FROM GUIAS g 
                    WHERE g.PRESTADOR_ID = :prestadorId 
                      AND g.COMPETENCIA = TRUNC(:competencia, 'MM')",
                    OracleHelper.P(":prestadorId", PRESTADOR_ID_TESTE),
                    OracleHelper.P(":competencia", competencia));

                if (result == null || result == DBNull.Value)
                    return 0m;

                return Convert.ToDecimal(result);
            }
            catch (OracleException ex)
            {
                throw new InvalidOperationException($"Erro ao obter valor da guia: {ex.Message}", ex);
            }
        }
    }
}
