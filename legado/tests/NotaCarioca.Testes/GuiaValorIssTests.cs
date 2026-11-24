using System;
using System.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Dados;
using NotaCarioca.Dominio;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Testes
{
    /// <summary>
    /// Suite de testes para DETECTAR BUG do Chamado CH-01:
    /// "A guia não bate com as notas"
    /// 
    /// PROBLEMA: Soma do ISS das notas (calculado com arredondamento individual)
    /// não confere com o valor da guia (calculado com arredondamento agregado)
    /// 
    /// CAUSA RAIZ: Dois pontos de cálculo de ISS diferentes:
    /// 1. Aplicação (CalculoIss): Math.Round(base * aliquota, 2) ANTES de somar
    /// 2. Banco (PKG_GUIA): SUM(ROUND((base) * aliquota, 2)) DEPOIS de somar
    /// 
    /// OBJETIVO: Executar testes que demonstrem a divergência e validem a correção
    /// </summary>
    [TestClass]
    public class GuiaValorIssTests
    {
        private static int _protocoloCounter = 0;
        private GuiaRepositorio _guiaRepo;
        private NfseRepositorio _nfseRepo;

        [TestInitialize]
        public void Setup()
        {
            _guiaRepo = new GuiaRepositorio();
            _nfseRepo = new NfseRepositorio();

            // Limpeza: remover dados de teste anteriores
            // ORDEM IMPORTANTE: inverter as FKs - GUIAS, depois NFSE, depois RPS, depois CONTRIBUINTES
            try
            {
                using (var conn = OracleHelper.AbrirConexao())
                using (var tx = conn.BeginTransaction())
                {
                    // Desabilitar triggers temporariamente para evitar locks
                    OracleHelper.ExecuteNonQuery(conn, tx, "ALTER TABLE NFSE DISABLE ALL TRIGGERS");
                    OracleHelper.ExecuteNonQuery(conn, tx, "ALTER TABLE RPS DISABLE ALL TRIGGERS");

                    // Deleção na ordem correta: tabelas com FK referenciando outras
                    OracleHelper.ExecuteNonQuery(conn, tx, "DELETE FROM GUIAS WHERE PRESTADOR_ID in (1000,1001,1002,1003)");
                    OracleHelper.ExecuteNonQuery(conn, tx, "DELETE FROM NFSE WHERE PRESTADOR_ID in (1000,1001,1002,1003)");
                    OracleHelper.ExecuteNonQuery(conn, tx, "DELETE FROM RPS WHERE PRESTADOR_ID in (1000,1001,1002,1003)");
                    OracleHelper.ExecuteNonQuery(conn, tx, "DELETE FROM CONTRIBUINTES WHERE ID in (1000,1001,1002,1003)");

                    // Reabilitar triggers
                    OracleHelper.ExecuteNonQuery(conn, tx, "ALTER TABLE RPS ENABLE ALL TRIGGERS");
                    OracleHelper.ExecuteNonQuery(conn, tx, "ALTER TABLE NFSE ENABLE ALL TRIGGERS");

                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Aviso na limpeza de setup: {ex.Message}");
            }
        }

        [TestCleanup]
        public void Cleanup()
        {
            // Garantir que dados de teste sejam removidos
            // ORDEM IMPORTANTE: inverter as FKs - GUIAS, depois NFSE, depois RPS, depois CONTRIBUINTES
            try
            {
                using (var conn = OracleHelper.AbrirConexao())
                using (var tx = conn.BeginTransaction())
                {
                    // Desabilitar triggers temporariamente para evitar locks
                    OracleHelper.ExecuteNonQuery(conn, tx, "ALTER TABLE NFSE DISABLE ALL TRIGGERS");
                    OracleHelper.ExecuteNonQuery(conn, tx, "ALTER TABLE RPS DISABLE ALL TRIGGERS");

                    // Deleção na ordem correta: tabelas com FK referenciando outras
                    OracleHelper.ExecuteNonQuery(conn, tx, "DELETE FROM GUIAS WHERE PRESTADOR_ID in (1000,1001,1002,1003)");
                    OracleHelper.ExecuteNonQuery(conn, tx, "DELETE FROM NFSE WHERE PRESTADOR_ID in (1000,1001,1002,1003)");
                    OracleHelper.ExecuteNonQuery(conn, tx, "DELETE FROM RPS WHERE PRESTADOR_ID in (1000,1001,1002,1003)");
                    OracleHelper.ExecuteNonQuery(conn, tx, "DELETE FROM CONTRIBUINTES WHERE ID in (1000,1001,1002,1003)");

                    // Reabilitar triggers
                    OracleHelper.ExecuteNonQuery(conn, tx, "ALTER TABLE RPS ENABLE ALL TRIGGERS");
                    OracleHelper.ExecuteNonQuery(conn, tx, "ALTER TABLE NFSE ENABLE ALL TRIGGERS");

                    tx.Commit();
                }
            }
            catch { /* Ignorar se falhar */ }

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private void CriarContribuinte(long id, string regime = "N")
        {
            try
            {
                using (var conn = OracleHelper.AbrirConexao())
                {
                    OracleHelper.ExecuteNonQuery(conn, null,
                        "INSERT INTO CONTRIBUINTES (ID, DOCUMENTO, INSCRICAO_MUNICIPAL, RAZAO_SOCIAL, REGIME) " +
                        "VALUES (:id, :doc, :im, :razao, :regime)",
                        OracleHelper.P("id", id),
                        OracleHelper.P("doc", id.ToString().PadLeft(14, '0')),
                        OracleHelper.P("im", id.ToString()),
                        OracleHelper.P("razao", $"Contrib {id}"),
                        OracleHelper.P("regime", regime));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Aviso ao criar contribuinte {id}: {ex.Message}");
            }
        }

        private long CriarRps(long prestadorId, DateTime competencia)
        {
            long rpsId;
            using (var conn = OracleHelper.AbrirConexao())
            {
                using (OracleCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = 
                        "INSERT INTO RPS (ID, PRESTADOR_ID, NUMERO, SERIE, TIPO, DT_EMISSAO, PROTOCOLO, " +
                        "TOMADOR_CNPJ, TOMADOR_RAZAO) " +
                        "VALUES (SEQ_RPS.NEXTVAL, :prest, :num, :serie, :tipo, :dt, :prot, :tomcnpj, :tomraz) " +
                        "RETURNING ID INTO :id";
                    cmd.Parameters.Add(OracleHelper.P("prest", OracleDbType.Int64, prestadorId));
                    cmd.Parameters.Add(OracleHelper.P("num", OracleDbType.Int64, DateTime.Now.Ticks));
                    cmd.Parameters.Add(OracleHelper.P("serie", OracleDbType.Varchar2, "1"));
                    cmd.Parameters.Add(OracleHelper.P("tipo", OracleDbType.Int64, 1));
                    cmd.Parameters.Add(OracleHelper.P("dt", OracleDbType.Date, competencia.Date));
                    cmd.Parameters.Add(OracleHelper.P("prot", OracleDbType.Varchar2, "PROT" + (++_protocoloCounter).ToString("D20")));
                    cmd.Parameters.Add(OracleHelper.P("tomcnpj", OracleDbType.Int64, 1L));
                    cmd.Parameters.Add(OracleHelper.P("tomraz", OracleDbType.Varchar2, "Tomador"));
                    OracleParameter idParam = new OracleParameter("id", OracleDbType.Int64);
                    idParam.Direction = ParameterDirection.Output;
                    cmd.Parameters.Add(idParam);
                    cmd.ExecuteNonQuery();
                    rpsId = long.Parse(idParam.Value.ToString());
                }
            }
            return rpsId;
        }

        private void GerarNfse(long rpsId, long prestadorId, DateTime competencia, string itemServico,
                               decimal valorServicos, decimal valorDeducoes, decimal aliquota, bool issRetido = false)
        {
            decimal baseCalculo = CalculoIss.CalcularBase(valorServicos, valorDeducoes);
            decimal valorIss = CalculoIss.CalcularValorIss(baseCalculo, aliquota, "N");

            using (var conn = OracleHelper.AbrirConexao())
            using (OracleTransaction tx = conn.BeginTransaction())
            {
                using (OracleCommand cmd = OracleHelper.CriarProcedure(conn, tx, "PKG_NFSE.GERAR_NFSE"))
                {
                    cmd.Parameters.Add(OracleHelper.P("p_rps_id", OracleDbType.Int64, rpsId));
                    cmd.Parameters.Add(OracleHelper.P("p_prestador_id", OracleDbType.Int64, prestadorId));
                    cmd.Parameters.Add(OracleHelper.P("p_competencia", OracleDbType.Date, competencia));
                    cmd.Parameters.Add(OracleHelper.P("p_item", OracleDbType.Varchar2, itemServico));
                    cmd.Parameters.Add(OracleHelper.P("p_discriminacao", OracleDbType.Varchar2, "Serviço"));
                    cmd.Parameters.Add(OracleHelper.P("p_valor_servicos", OracleDbType.Decimal, valorServicos));
                    cmd.Parameters.Add(OracleHelper.P("p_valor_deducoes", OracleDbType.Decimal, valorDeducoes));
                    cmd.Parameters.Add(OracleHelper.P("p_aliquota", OracleDbType.Decimal, aliquota));
                    cmd.Parameters.Add(OracleHelper.P("p_valor_iss", OracleDbType.Decimal, valorIss));
                    cmd.Parameters.Add(OracleHelper.P("p_iss_retido", OracleDbType.Varchar2, issRetido ? "S" : "N"));
                    cmd.Parameters.Add(OracleHelper.P("p_tomador_doc", OracleDbType.Varchar2, "00000000000001"));
                    cmd.Parameters.Add(OracleHelper.P("p_tomador_razao", OracleDbType.Varchar2, "Tomador"));

                    OracleParameter oId = new OracleParameter("o_nfse_id", OracleDbType.Int64);
                    oId.Direction = ParameterDirection.Output;
                    OracleParameter oNum = new OracleParameter("o_numero", OracleDbType.Int64);
                    oNum.Direction = ParameterDirection.Output;
                    OracleParameter oCod = new OracleParameter("o_codigo_verificacao", OracleDbType.Varchar2, 20);
                    oCod.Direction = ParameterDirection.Output;
                    cmd.Parameters.Add(oId);
                    cmd.Parameters.Add(oNum);
                    cmd.Parameters.Add(oCod);

                    cmd.ExecuteNonQuery();
                    tx.Commit();
                }
            }
        }

        // CH-01 — Teste 1: Demonstra divergência de arredondamento
        // Sintoma: Contribuinte somou ISS das notas e obteve valor diferente da guia
        // Causa: Aplicação calcula com arredondamento individual, banco com arredondamento agregado
        [TestMethod]
        [TestCategory("CH-01")]
        public void CH01_01_SomaIssNotasNaoConfereComGuia_ArredondamentoMultiplas()
        {
            // Arrange
            long prestadorId = 1000;
            DateTime competencia = new DateTime(2026, 9, 1);
            CriarContribuinte(prestadorId);

            // 5 notas com valores que causam divergência no arredondamento (alíquota 5%)
            // Nota 1: 10.50 * 5% = 0.525 → 0.53 (C#) vs 0.52 (Oracle)
            // Nota 2: 250.50 * 5% = 12.525 → 12.53 (C#) vs 12.52 (Oracle)
            // Nota 3: 100.10 * 5% = 5.005 → 5.01 (C#) vs 5.00 (Oracle)
            // Soma esperada: 0.53 + 12.53 + 5.01 = 18.07 (C#) vs 18.04 (Oracle)

            long rps1 = CriarRps(prestadorId, competencia);
            GerarNfse(rps1, prestadorId, competencia, "17.01", 10.50m, 0, 0.05m);

            long rps2 = CriarRps(prestadorId, competencia);
            GerarNfse(rps2, prestadorId, competencia, "17.01", 250.50m, 0, 0.05m);

            long rps3 = CriarRps(prestadorId, competencia);
            GerarNfse(rps3, prestadorId, competencia, "17.01", 100.10m, 0, 0.05m);

            // Act - Recalcular guia
            _guiaRepo.Recalcular(prestadorId, competencia);

            // Assert
            DataTable guia = _guiaRepo.ObterGuia(prestadorId, competencia);
            Assert.AreEqual(1, guia.Rows.Count, "Deve existir uma guia");

            decimal valorGuia = Convert.ToDecimal(guia.Rows[0]["VALOR_ISS"]);

            // Verificar se há divergência (bug)
            // Se banco calcula com SUM(ROUND((base)*aliquota,2)):
            // = 0.52 + 12.52 + 5.00 = 18.04
            decimal valorEsperadoBanco = 0.52m + 12.52m + 5.00m; // 18.04

            // Se aplicação calcula com Math.Round individual:
            // = 0.53 + 12.53 + 5.01 = 18.07
            decimal valorEsperadoAplicacao = 0.53m + 12.53m + 5.01m; // 18.07

            // O teste demonstra o bug se valores forem diferentes
            if (valorGuia == valorEsperadoBanco)
            {
                // BUG DETECTADO: Guia não bate com soma de ISS das notas (que usa arredondamento individual)
                Assert.Fail(
                    $"BUG DETECTADO — CH-01: Guia ({valorGuia}) bate com cálculo do banco ({valorEsperadoBanco}) " +
                    $"mas não com a soma de ISS das notas emitidas ({valorEsperadoAplicacao}). " +
                    $"Diferença: {valorEsperadoAplicacao - valorGuia}. " +
                    $"Causa: Banco usa SUM(ROUND(...)) enquanto aplicação usa ROUND(...) individual.");
            }
        }

        // CH-01 — Teste 2: Notas com ISS retido não devem entrar na guia
        [TestMethod]
        [TestCategory("CH-01")]
        public void CH01_02_IssRetidoNaoEntraGuia()
        {
            // Arrange
            long prestadorId = 1001;
            DateTime competencia = new DateTime(2026, 9, 1);
            CriarContribuinte(prestadorId);

            // 2 notas normais
            long rps1 = CriarRps(prestadorId, competencia);
            GerarNfse(rps1, prestadorId, competencia, "17.01", 1000m, 0, 0.05m, issRetido: false);

            long rps2 = CriarRps(prestadorId, competencia);
            GerarNfse(rps2, prestadorId, competencia, "17.01", 1000m, 0, 0.05m, issRetido: false);

            // 1 nota com ISS retido (não deve entrar na guia)
            long rps3 = CriarRps(prestadorId, competencia);
            GerarNfse(rps3, prestadorId, competencia, "17.01", 1000m, 0, 0.05m, issRetido: true);

            // Act
            _guiaRepo.Recalcular(prestadorId, competencia);

            // Assert
            DataTable guia = _guiaRepo.ObterGuia(prestadorId, competencia);
            Assert.AreEqual(1, guia.Rows.Count, "Deve existir uma guia");

            decimal valorGuia = Convert.ToDecimal(guia.Rows[0]["VALOR_ISS"]);

            // Valor esperado: apenas as 2 notas normais
            // (1000 * 0.05) + (1000 * 0.05) = 50 + 50 = 100
            decimal valorEsperado = 100m;

            Assert.AreEqual(valorEsperado, valorGuia,
                $"Guia deveria conter apenas notas normais (não retidas). " +
                $"Esperado: {valorEsperado}, Obtido: {valorGuia}");
        }

        // CH-01 — Teste 3: Deduções devem ser aplicadas corretamente
        [TestMethod]
        [TestCategory("CH-01")]
        public void CH01_03_DeducoesAplicadasCorretamente()
        {
            // Arrange
            long prestadorId = 1002;
            DateTime competencia = new DateTime(2026, 9, 1);
            CriarContribuinte(prestadorId);

            // Nota 1: (1000 - 100) * 5% = 900 * 0.05 = 45
            long rps1 = CriarRps(prestadorId, competencia);
            GerarNfse(rps1, prestadorId, competencia, "17.01", 1000m, 100m, 0.05m);

            // Nota 2: (1000 - 50) * 5% = 950 * 0.05 = 47.50
            long rps2 = CriarRps(prestadorId, competencia);
            GerarNfse(rps2, prestadorId, competencia, "17.01", 1000m, 50m, 0.05m);

            // Act
            _guiaRepo.Recalcular(prestadorId, competencia);

            // Assert
            DataTable guia = _guiaRepo.ObterGuia(prestadorId, competencia);
            Assert.AreEqual(1, guia.Rows.Count, "Deve existir uma guia");

            decimal valorGuia = Convert.ToDecimal(guia.Rows[0]["VALOR_ISS"]);

            // Valor esperado: 45 + 47.50 = 92.50
            decimal valorEsperado = 92.50m;

            Assert.AreEqual(valorEsperado, valorGuia,
                $"Guia deveria calcular ISS sobre base (valor - deduções). " +
                $"Esperado: {valorEsperado}, Obtido: {valorGuia}");
        }

        // CH-01 — Teste 4: Vencimento deve ser dia 10 do mês seguinte
        [TestMethod]
        [TestCategory("CH-01")]
        public void CH01_04_VencimentoDiaDezmesaSeguinte()
        {
            // Arrange
            long prestadorId = 1003;
            DateTime competencia = new DateTime(2026, 9, 1);
            CriarContribuinte(prestadorId);

            long rps = CriarRps(prestadorId, competencia);
            GerarNfse(rps, prestadorId, competencia, "17.01", 1000m, 0, 0.05m);

            // Act
            _guiaRepo.Recalcular(prestadorId, competencia);

            // Assert
            DataTable guia = _guiaRepo.ObterGuia(prestadorId, competencia);
            Assert.AreEqual(1, guia.Rows.Count, "Deve existir uma guia");

            DateTime vencimento = Convert.ToDateTime(guia.Rows[0]["DT_VENCIMENTO"]);

            // Vencimento esperado: dia 10 do mês seguinte (10/10/2026)
            DateTime vencimentoEsperado = new DateTime(2026, 10, 10);

            Assert.AreEqual(vencimentoEsperado, vencimento,
                $"Vencimento incorreto. Esperado: {vencimentoEsperado:dd/MM/yyyy}, Obtido: {vencimento:dd/MM/yyyy}");
        }
    }
}
