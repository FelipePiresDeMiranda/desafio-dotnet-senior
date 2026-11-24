using System;
using System.Data;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Dados;
using NotaCarioca.Dominio;
using NotaCarioca.Servicos.Modelos;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Testes
{
    /// <summary>
    /// Testes para detectar problemas de performance em ConsultarNfseServicoTomado
    /// CH-04 — "Consulta de serviços tomados com timeout"
    /// </summary>
    [TestClass]
    public class ConsultaServicoTomadoPerformanceTests
    {
        private OracleConnection _conn;
        private NfseRepositorio _nfseRepo;
        private System.Collections.Generic.List<long> _prestadorIdsCriados;

        [TestInitialize]
            public void Setup()
            {
                _conn = OracleHelper.AbrirConexao();
                _nfseRepo = new NfseRepositorio();
                _prestadorIdsCriados = new System.Collections.Generic.List<long>();

                // Limpar dados de testes anteriores
                LimparDados();
            }

        [TestCleanup]
        public void Cleanup()
        {
            LimparDados();
            if (_conn != null && _conn.State == ConnectionState.Open)
            {
                _conn.Close();
                _conn.Dispose();
            }
        }

        private void LimparDados()
        {
            try
            {
                DisableTriggers();

                // Deletar apenas registros criados por este teste (baseado em prestador_ids)
                if (_prestadorIdsCriados.Count > 0)
                {
                    string prestadorIds = string.Join(",", _prestadorIdsCriados);

                    // Deletar em ordem FK-safe
                    OracleHelper.ExecuteNonQuery(_conn, null, 
                        $"DELETE FROM GUIAS WHERE PRESTADOR_ID IN ({prestadorIds})");
                    OracleHelper.ExecuteNonQuery(_conn, null, 
                        $"DELETE FROM NFSE WHERE PRESTADOR_ID IN ({prestadorIds})");
                    OracleHelper.ExecuteNonQuery(_conn, null, 
                        $"DELETE FROM RPS WHERE PRESTADOR_ID IN ({prestadorIds})");
                    OracleHelper.ExecuteNonQuery(_conn, null, 
                        $"DELETE FROM LOTES_RPS WHERE PRESTADOR_ID IN ({prestadorIds})");
                    OracleHelper.ExecuteNonQuery(_conn, null, 
                        $"DELETE FROM CONTRIBUINTES WHERE ID IN ({prestadorIds})");
                }

                _prestadorIdsCriados.Clear();
                EnableTriggers();
            }
            catch { }
        }

        private void DisableTriggers()
        {
            try
            {
                OracleHelper.ExecuteNonQuery(_conn, null, "ALTER TABLE NFSE DISABLE ALL TRIGGERS");
                OracleHelper.ExecuteNonQuery(_conn, null, "ALTER TABLE GUIAS DISABLE ALL TRIGGERS");
                OracleHelper.ExecuteNonQuery(_conn, null, "ALTER TABLE RPS DISABLE ALL TRIGGERS");
            }
            catch { }
        }

        private void EnableTriggers()
        {
            try
            {
                OracleHelper.ExecuteNonQuery(_conn, null, "ALTER TABLE RPS ENABLE ALL TRIGGERS");
                OracleHelper.ExecuteNonQuery(_conn, null, "ALTER TABLE NFSE ENABLE ALL TRIGGERS");
                OracleHelper.ExecuteNonQuery(_conn, null, "ALTER TABLE GUIAS ENABLE ALL TRIGGERS");
            }
            catch { }
        }

        /// <summary>
        /// CH-04-01: Verificar se consulta usa FULL scan em vez de índice
        /// 
        /// Problema: Query tem hint /*+ FULL(n) */ que força full table scan.
        /// Isso é ineficiente com tabelas grandes (milhões de registros).
        /// Em horário de pico, múltiplas consultas simultâneas causam contention.
        /// 
        /// Correção Necessária: Remover hint FULL ou usar índice adequadamente.
        /// Usar índice: IX_NFSE_TOMADOR_DT(TOMADOR_DOCUMENTO, DT_EMISSAO)
        /// 
        /// Esperado: Consulta em < 500ms com 500 registros para mesmo tomador
        /// </summary>
        [TestMethod]
        public void CH04_01_QueryForcaFullScan_DeveriausarIndice()
        {
            // Arrange: Criar tomador e volumes de NFS-e
            long tomadorDoc = 1234567000195;
            int qtdRegistros = 500;
            DateTime competencia = new DateTime(2026, 10, 1);
            DateTime dataEmissao = new DateTime(2026, 10, 15);

            var prestadorId = CriarContribuinte("11111111000111", "10000050");
            CriarLoteNfseParaTomador(tomadorDoc, prestadorId, qtdRegistros, competencia, dataEmissao);

            // Act: Executar consulta e medir tempo
            var sw = Stopwatch.StartNew();
            var resultado = _nfseRepo.ConsultarServicoTomado(tomadorDoc.ToString(), 
                dataEmissao.Date, 
                dataEmissao.AddDays(1), 
                1);
            sw.Stop();

            // Assert: Verificar performance
            Assert.IsNotNull(resultado, "Resultado não deve ser nulo");
            Assert.IsTrue(resultado.Rows.Count > 0, "Deve retornar registros");
            Assert.IsTrue(resultado.Rows.Count <= NfseRepositorio.TamanhoPagina, 
                "Primeira página não deve exceder tamanho da página");

            // FALHA ESPERADA: Com FULL scan, mesmo com 500 registros fica lento
            Assert.IsTrue(sw.ElapsedMilliseconds < 500, 
                $"FALHA CH-04-01: Query levou {sw.ElapsedMilliseconds}ms (esperado < 500ms). " +
                $"Usando FULL scan em lugar de índice IX_NFSE_TOMADOR_DT. " +
                $"Remover hint /*+ FULL(n) */ e usar índice (TOMADOR_DOCUMENTO, DT_EMISSAO)");
        }

        /// <summary>
        /// CH-04-02: Verificar se a subquery do JOI com tabela CONTRIBUINTES é eficiente
        /// 
        /// Problema: Query faz JOIN com CONTRIBUINTES mesmo não usando colunas dela
        /// (usa apenas p.DOCUMENTO, p.INSCRICAO_MUNICIPAL, p.RAZAO_SOCIAL no SELECT).
        /// Para cada linha de NFSE, faz lookup em CONTRIBUINTES (pode ser denormalizado).
        /// 
        /// Correção Necessária: Ou incluir colunas de prestador na tabela NFSE,
        /// ou aceitar o JOIN mas garantir índice em NFSE(PRESTADOR_ID)
        /// 
        /// Esperado: Consulta eficiente mesmo com 100k registros
        /// </summary>
        [TestMethod]
        public void CH04_02_JoinComContribuintesIneficiente_DeveriauserDenormalizacao()
        {
            // Arrange
            long tomadorDoc = 1234567000195;
            int qtdRegistros = 400;
            DateTime competencia = new DateTime(2026, 10, 1);
            DateTime dataEmissao = new DateTime(2026, 10, 15);

            var prestadorId = CriarContribuinte("22222222000122", "10000051");
            CriarLoteNfseParaTomador(tomadorDoc, prestadorId, qtdRegistros, competencia, dataEmissao);

            // Act
            var sw = Stopwatch.StartNew();
            var resultado = _nfseRepo.ConsultarServicoTomado(tomadorDoc.ToString(), 
                dataEmissao.Date, 
                dataEmissao.AddDays(1), 
                1);
            sw.Stop();

            // Assert
            Assert.IsNotNull(resultado);
            Assert.IsTrue(resultado.Rows.Count > 0);

            // FALHA ESPERADA: JOIN desnecessário causa full scan em CONTRIBUINTES
            Assert.IsTrue(sw.ElapsedMilliseconds < 300,
                $"FALHA CH-04-02: Query com JOIN levou {sw.ElapsedMilliseconds}ms. " +
                $"Verificar se todos os dados de prestador precisam ser buscados. " +
                $"Considerar denormalizar colunas de prestador em NFSE ou usar índice de cobertura.");
        }

        /// <summary>
        /// CH-04-03: Verificar problema com TRUNC() em índice
        /// 
        /// Problema: WHERE usa TRUNC(n.DT_EMISSAO) >= :ini AND TRUNC(n.DT_EMISSAO) < :fim
        /// A função TRUNC() impede uso de índice em DT_EMISSAO.
        /// Query faz full scan mesmo com índice IX_NFSE_TOMADOR_DT.
        /// 
        /// Correção Necessária: Usar range sem TRUNC:
        ///   WHERE DT_EMISSAO >= :ini AND DT_EMISSAO < :fim + 1 DAY
        /// Isso permite índice ser usado em (TOMADOR_DOCUMENTO, DT_EMISSAO)
        /// 
        /// Esperado: Mesmo com 100k registros, < 100ms
        /// </summary>
        [TestMethod]
        public void CH04_03_TruncEmDtEmissao_ImpedeusoDeIndice()
        {
            // Arrange
            long tomadorDoc = 1234567000195;
            int qtdRegistros = 600;
            DateTime competencia = new DateTime(2026, 10, 1);
            DateTime dataEmissao = new DateTime(2026, 10, 15);

            var prestadorId = CriarContribuinte("33333333000133", "10000052");
            CriarLoteNfseParaTomador(tomadorDoc, prestadorId, qtdRegistros, competencia, dataEmissao);

            // Act
            var sw = Stopwatch.StartNew();
            var resultado = _nfseRepo.ConsultarServicoTomado(tomadorDoc.ToString(), 
                dataEmissao.Date, 
                dataEmissao.AddDays(30), 
                1);
            sw.Stop();

            // Assert
            Assert.IsNotNull(resultado);

            // FALHA ESPERADA: TRUNC() desativa índice
            Assert.IsTrue(sw.ElapsedMilliseconds < 300,
                $"FALHA CH-04-03: Query com TRUNC() levou {sw.ElapsedMilliseconds}ms. " +
                $"TRUNC() em WHERE impede uso de índice em DT_EMISSAO. " +
                $"Substituir: TRUNC(DT_EMISSAO) >= :ini → DT_EMISSAO >= :ini AND DT_EMISSAO < :ini + 1");
        }

        /// <summary>
        /// CH-04-04: Verificar timeout com múltiplas consultas simultâneas
        /// 
        /// Problema: Em horário de pico, ERPs de grandes tomadores fazem múltiplas
        /// chamadas simultâneas de ConsultarNfseServicoTomado.
        /// Com FULL scan e sem índices, causa lock contention.
        /// 
        /// Correção Necessária: Otimizar query para usar índice,
        /// permitir que múltiplas consultas rodem em paralelo sem contention.
        /// 
        /// Esperado: 10 consultas paralelas em < 1 segundo total
        /// </summary>
        [TestMethod]
        public void CH04_04_ConsultasSimultaneas_NaoDevemCausarTimeout()
        {
            // Arrange
            long[] tomadores = new[] { 1234567000195L, 9876543000210L };
            int qtdRegistros = 300;
            DateTime competencia = new DateTime(2026, 10, 1);
            DateTime dataEmissao = new DateTime(2026, 10, 15);

            foreach (var tom in tomadores)
            {
                var prestadorId = CriarContribuinte($"{tom:00000000000000}", $"{tom % 10000:00000000}");
                CriarLoteNfseParaTomador(tom, prestadorId, qtdRegistros, competencia, dataEmissao);
            }

            // Act
            var sw = Stopwatch.StartNew();
            var tarefas = new System.Threading.Tasks.Task[tomadores.Length];
            var resultados = new DataTable[tomadores.Length];

            for (int i = 0; i < tomadores.Length; i++)
            {
                int idx = i;
                tarefas[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    using (var connTemp = OracleHelper.AbrirConexao())
                    {
                        var repo = new NfseRepositorio();
                        resultados[idx] = repo.ConsultarServicoTomado(tomadores[idx].ToString(), 
                            dataEmissao.Date, 
                            dataEmissao.AddDays(1), 
                            1);
                    }
                });
            }

            System.Threading.Tasks.Task.WaitAll(tarefas);
            sw.Stop();

            // Assert
            foreach (var res in resultados)
            {
                Assert.IsNotNull(res);
                Assert.IsTrue(res.Rows.Count > 0);
            }

            // FALHA ESPERADA: Lock contention por FULL scan
            Assert.IsTrue(sw.ElapsedMilliseconds < 1000,
                $"FALHA CH-04-04: {tomadores.Length} consultas paralelas levaram {sw.ElapsedMilliseconds}ms. " +
                $"Sem índice adequado, múltiplas consultas causam lock contention. " +
                $"Corrigir query para usar índice (TOMADOR_DOCUMENTO, DT_EMISSAO) e permitir paralelismo.");
        }

        /// <summary>
        /// CH-04-05: Verificar se paginação com ROWNUM é eficiente
        /// 
        /// Problema: Query usa técnica de ROWNUM duplo nível (aninhada):
        ///   WHERE ROWNUM <= :limite ... WHERE RN > :deslocamento
        /// Para pagar página 100, terá que ler todos os ROWNUM até lá.
        /// Com 100k registros e paginação, cada página posterior fica mais lenta.
        /// 
        /// Correção Necessária: Usar OFFSET...FETCH (Oracle 12c+) ou
        /// técnica de keyset pagination em vez de ROWNUM
        /// 
        /// Esperado: Todas as páginas em tempo similar (< 100ms)
        /// </summary>
        [TestMethod]
        public void CH04_05_PaginacaoComRownum_FicaMaisLentaEmPaginasPosteriores()
        {
            // Arrange
            long tomadorDoc = 1234567000195;
            int qtdRegistros = 500;
            DateTime competencia = new DateTime(2026, 10, 1);
            DateTime dataEmissao = new DateTime(2026, 10, 15);

            var prestadorId = CriarContribuinte("44444444000144", "10000053");
            CriarLoteNfseParaTomador(tomadorDoc, prestadorId, qtdRegistros, competencia, dataEmissao);

            // Act: Consultar página 1 e página 10
            var sw1 = Stopwatch.StartNew();
            var pag1 = _nfseRepo.ConsultarServicoTomado(tomadorDoc.ToString(), 
                dataEmissao.Date, 
                dataEmissao.AddDays(1), 
                1);
            sw1.Stop();

            var sw10 = Stopwatch.StartNew();
            var pag10 = _nfseRepo.ConsultarServicoTomado(tomadorDoc.ToString(), 
                dataEmissao.Date, 
                dataEmissao.AddDays(1), 
                10);
            sw10.Stop();

            // Assert
            Assert.IsNotNull(pag1);
            Assert.IsNotNull(pag10);
            Assert.IsTrue(pag1.Rows.Count > 0);

            // FALHA ESPERADA: Página 10 mais lenta que página 1
            Assert.IsTrue(sw10.ElapsedMilliseconds <= sw1.ElapsedMilliseconds * 1.1,
                $"FALHA CH-04-05: Página 1 levou {sw1.ElapsedMilliseconds}ms, " +
                $"Página 10 levou {sw10.ElapsedMilliseconds}ms. " +
                $"ROWNUM paging degrada com paginação. " +
                $"Usar OFFSET...FETCH ou keyset pagination em vez de ROWNUM.");
        }

        /// <summary>
        /// CH-04-06: Verificar se índice IX_NFSE_TOMADOR_DT está sendo usado
        /// 
        /// Problema: Índice foi criado (IX_NFSE_TOMADOR_DT) mas query força FULL scan
        /// com hint /*+ FULL(n) */. Isso ignora índice e faz full table scan.
        /// 
        /// Correção Necessária: Remover hint FULL ou substituir por hint de índice:
        ///   /*+ INDEX(n IX_NFSE_TOMADOR_DT) */
        /// 
        /// Esperado: Com índice, mesmo com 1M registros, retorna em < 50ms
        /// </summary>
        [TestMethod]
        public void CH04_06_HintFullIgnoraIndice_DeveriausarIndexHint()
        {
            // Arrange
            long tomadorDoc = 1234567000195;
            int qtdRegistros = 700;
            DateTime competencia = new DateTime(2026, 10, 1);
            DateTime dataEmissao = new DateTime(2026, 10, 15);

            var prestadorId = CriarContribuinte("55555555000155", "10000054");
            CriarLoteNfseParaTomador(tomadorDoc, prestadorId, qtdRegistros, competencia, dataEmissao);

            // Verificar que índice existe
            var indexCheck = OracleHelper.ExecuteDataTable(
                "SELECT index_name FROM user_indexes WHERE index_name = 'IX_NFSE_TOMADOR_DT'");
            Assert.IsTrue(indexCheck.Rows.Count > 0, "Índice IX_NFSE_TOMADOR_DT deve existir");

            // Act
            var sw = Stopwatch.StartNew();
            var resultado = _nfseRepo.ConsultarServicoTomado(tomadorDoc.ToString(), 
                dataEmissao.Date, 
                dataEmissao.AddDays(1), 
                1);
            sw.Stop();

            // Assert
            Assert.IsNotNull(resultado);
            Assert.IsTrue(resultado.Rows.Count > 0);

            // FALHA ESPERADA: Hint FULL força full scan ignorando índice
            Assert.IsTrue(sw.ElapsedMilliseconds < 300,
                $"FALHA CH-04-06: Query levou {sw.ElapsedMilliseconds}ms mesmo com índice. " +
                $"Hint /*+ FULL(n) */ força full table scan. " +
                $"Remover FULL hint e usar /*+ INDEX(n IX_NFSE_TOMADOR_DT) */ ou deixar Oracle otimizar.");
        }

        // ============ HELPER METHODS ============

        private long CriarContribuinte(string documento, string inscricaoMunicipal)
        {
            var docNumerico = ValidadorDocumento.ParaNumero(documento);
            OracleHelper.ExecuteNonQuery(_conn, null,
                "INSERT INTO CONTRIBUINTES (ID, INSCRICAO_MUNICIPAL, DOCUMENTO, RAZAO_SOCIAL, REGIME) " +
                "VALUES (SEQ_CONTRIBUINTES.NEXTVAL, :im, :doc, :razao, :regime)",
                OracleHelper.P("im", OracleDbType.Varchar2, inscricaoMunicipal),
                OracleHelper.P("doc", OracleDbType.Varchar2, documento),
                OracleHelper.P("razao", OracleDbType.Varchar2, $"Prestador {inscricaoMunicipal}"),
                OracleHelper.P("regime", OracleDbType.Varchar2, "N"));

            var dt = OracleHelper.ExecuteDataTable(_conn, null,
                "SELECT ID FROM CONTRIBUINTES WHERE INSCRICAO_MUNICIPAL = :im",
                OracleHelper.P("im", OracleDbType.Varchar2, inscricaoMunicipal));

            long prestadorId = Convert.ToInt64(dt.Rows[0]["ID"]);

            // Rastrear para limpeza posterior
            _prestadorIdsCriados.Add(prestadorId);

            return prestadorId;
        }

        private void CriarLoteNfseParaTomador(long tomadorDoc, long prestadorId, int quantidade, 
                                             DateTime competencia, DateTime dataEmissao)
        {
            for (int i = 1; i <= quantidade; i++)
            {
                var nfseId = OracleHelper.ExecuteScalar(_conn, null, "SELECT SEQ_NFSE.NEXTVAL FROM DUAL");

                OracleHelper.ExecuteNonQuery(_conn, null,
                    "INSERT INTO NFSE (ID, PRESTADOR_ID, NUMERO, DT_EMISSAO, COMPETENCIA, " +
                    "TOMADOR_DOCUMENTO, TOMADOR_RAZAO, VALOR_SERVICOS, VALOR_DEDUCOES, " +
                    "BASE_CALCULO, ALIQUOTA, VALOR_ISS, ISS_RETIDO, SITUACAO) " +
                    "VALUES (:id, :prest, :num, :dt, :comp, :tomdoc, :tomrazao, " +
                    ":valserv, :valdeduc, :base, :aliq, :valiss, :issret, :sit)",
                    OracleHelper.P("id", OracleDbType.Int64, nfseId),
                    OracleHelper.P("prest", OracleDbType.Int64, prestadorId),
                    OracleHelper.P("num", OracleDbType.Int64, i),
                    OracleHelper.P("dt", OracleDbType.Date, dataEmissao),
                    OracleHelper.P("comp", OracleDbType.Date, competencia),
                    OracleHelper.P("tomdoc", OracleDbType.Varchar2, tomadorDoc.ToString().PadLeft(14, '0')),
                    OracleHelper.P("tomrazao", OracleDbType.Varchar2, $"Tomador {tomadorDoc}"),
                    OracleHelper.P("valserv", OracleDbType.Decimal, 1000m),
                    OracleHelper.P("valdeduc", OracleDbType.Decimal, 0m),
                    OracleHelper.P("base", OracleDbType.Decimal, 1000m),
                    OracleHelper.P("aliq", OracleDbType.Decimal, 0.05m),
                    OracleHelper.P("valiss", OracleDbType.Decimal, 50m),
                    OracleHelper.P("issret", OracleDbType.Varchar2, "N"),
                    OracleHelper.P("sit", OracleDbType.Varchar2, "N"));
            }
        }
    }
}
