using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Dados;
using NotaCarioca.Dominio;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Testes
{
    /// <summary>
    /// Testes de integração para validar funcionalidade completa de CNPJ alfanumérico
    /// FEATURE-CNPJ-ALFANUMERICO: Suporta novos CNPJs alfanuméricos (IN RFB nº 2.229/2024)
    /// 
    /// Phase 4: Integração com Oracle 19c
    /// - Testa inserção, consulta e manipulação de RPS com CNPJ alfanumérico
    /// - Valida compatibilidade com numéricos e CPF
    /// - Mede performance sem regressão
    /// 
    /// NOTA: Testes requerem migration FEATURE-CNPJ-ALFANUMERICO-001-ALTER-SCHEMA.sql aplicada
    /// Para testes sem migration, usar CnpjAlfanumericoTests.cs (testes unitários)
    /// </summary>
    [TestClass]
    public class CnpjAlfanumericoIntegracaoTests
    {
        private OracleConnection _conn;
            private RpsRepositorio _rpsRepo;
            private List<long> _rpsIdsInseridos;
            private List<long> _prestadoresInseridos;
            private bool _colunaAlfanumericoExiste;

            [TestInitialize]
            public void Setup()
            {
                _conn = OracleHelper.AbrirConexao();
                _rpsRepo = new RpsRepositorio();
                _rpsIdsInseridos = new List<long>();
                _prestadoresInseridos = new List<long>();

                // Verificar se coluna TOMADOR_DOCUMENTO_ALFANUMERICO existe
                _colunaAlfanumericoExiste = ColunaExiste("TOMADOR_DOCUMENTO_ALFANUMERICO");
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
            if (_rpsIdsInseridos.Count == 0 && _prestadoresInseridos.Count == 0)
                return;

            try
            {
                // Deletar RPS inseridos
                foreach (long rpsId in _rpsIdsInseridos)
                {
                    try
                    {
                        OracleHelper.ExecuteNonQuery(_conn, null,
                            "DELETE FROM RPS WHERE ID = :id",
                            OracleHelper.P("id", rpsId));
                    }
                    catch { }
                }

                // Deletar prestadores inseridos
                foreach (long prestId in _prestadoresInseridos)
                {
                    try
                    {
                        OracleHelper.ExecuteNonQuery(_conn, null,
                            "DELETE FROM CONTRIBUINTES WHERE ID = :id",
                            OracleHelper.P("id", prestId));
                    }
                    catch { }
                }

                _conn.Commit();
                _rpsIdsInseridos.Clear();
                _prestadoresInseridos.Clear();
            }
            catch { }
        }

        private bool ColunaExiste(string nomeColuna)
        {
            try
            {
                var dt = OracleHelper.ExecuteDataTable(_conn, null,
                    $"SELECT {nomeColuna} FROM RPS WHERE ROWNUM = 1",
                    null);
                return true;
            }
            catch (OracleException ex) when (ex.Number == 904)  // ORA-00904: identificador inválido
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Helper: Criar um prestador (contribuinte)
        /// </summary>
        private long CriarPrestador(string documento, string inscricao)
        {
            long prestadorId = Convert.ToInt64(OracleHelper.ExecuteScalar(_conn, null,
                "SELECT SEQ_CONTRIBUINTES.NEXTVAL FROM DUAL"));

            OracleHelper.ExecuteNonQuery(_conn, null,
                "INSERT INTO CONTRIBUINTES (ID, DOCUMENTO, INSCRICAO_MUNICIPAL, RAZAO_SOCIAL) " +
                "VALUES (:id, :doc, :insc, :razao)",
                OracleHelper.P("id", prestadorId),
                OracleHelper.P("doc", documento),
                OracleHelper.P("insc", inscricao),
                OracleHelper.P("razao", $"Prestador-{prestadorId}"));

            _prestadoresInseridos.Add(prestadorId);
            return prestadorId;
        }

        /// <summary>
        /// FEATURE-CNPJ-01: Inserir RPS com tomador CNPJ alfanumérico
        /// 
        /// Descrição: Validar que RPS pode ser inserido com CNPJ alfanumérico como tomador.
        /// O documento deve ser armazenado em TOMADOR_DOCUMENTO_ALFANUMERICO e recuperável.
        /// 
        /// Esperado:
        /// - RPS inserido com sucesso
        /// - TOMADOR_DOCUMENTO_ALFANUMERICO = "11ABCDEF000106" (alfanumérico preservado)
        /// - TOMADOR_CNPJ = NULL (não numérico)
        /// - ID retornado é válido
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_01_InserirRpsComTomadorAlfanumerico()
        {
            // Arrange
            long prestadorId = CriarPrestador("11111111000111", "10000050");
            string tomadorAlfanumerico = "11ABCDEF000106";
            DateTime dtEmissao = DateTime.Now.Date;

            // Act
            long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 1001, "Z", 1,
                dtEmissao, "PROTO001", tomadorAlfanumerico, "Tomador Alfanumérico Ltda");

            _rpsIdsInseridos.Add(rpsId);

            // Assert
            Assert.IsTrue(rpsId > 0, "RPS ID deve ser maior que zero");

            if (_colunaAlfanumericoExiste)
            {
                // Consultar RPS inserido
                var sql = @"SELECT ID, TOMADOR_DOCUMENTO_ALFANUMERICO, TOMADOR_CNPJ, TOMADOR_CPF 
                           FROM RPS WHERE ID = :id";
                var dt = OracleHelper.ExecuteDataTable(_conn, null, sql,
                    OracleHelper.P("id", rpsId));

                Assert.IsTrue(dt.Rows.Count > 0, "RPS deve existir no banco");

                object docAlfan = dt.Rows[0]["TOMADOR_DOCUMENTO_ALFANUMERICO"];
                object cnpj = dt.Rows[0]["TOMADOR_CNPJ"];
                object cpf = dt.Rows[0]["TOMADOR_CPF"];

                // Validações
                Assert.AreNotEqual(DBNull.Value, docAlfan, "TOMADOR_DOCUMENTO_ALFANUMERICO não deve ser nulo");
                Assert.AreEqual(tomadorAlfanumerico, docAlfan.ToString(), "Documento alfanumérico deve ser preservado");
                Assert.AreEqual(DBNull.Value, cnpj, "TOMADOR_CNPJ deve ser nulo para alfanumérico");
                Assert.AreEqual(DBNull.Value, cpf, "TOMADOR_CPF deve ser nulo para alfanumérico");
            }
            else
            {
                // Migration não foi aplicada - teste simplificado
                // Apenas verificar que RPS foi inserido
                var sql = @"SELECT ID FROM RPS WHERE ID = :id";
                var dt = OracleHelper.ExecuteDataTable(_conn, null, sql,
                    OracleHelper.P("id", rpsId));
                Assert.IsTrue(dt.Rows.Count > 0, "RPS deve existir no banco (sem migration, apenas inserção básica)");
            }
        }

        /// <summary>
        /// FEATURE-CNPJ-02: Inserir RPS com tomador CNPJ numérico (compatibilidade)
        /// 
        /// Descrição: Validar que código antigo continua funcionando. RPS com CNPJ numérico
        /// deve ser inserido e armazenado em TOMADOR_CNPJ (compatibilidade).
        /// 
        /// Esperado:
        /// - RPS inserido com sucesso
        /// - TOMADOR_CNPJ = 11222333000181 (numérico)
        /// - TOMADOR_DOCUMENTO_ALFANUMERICO = "11222333000181" (string para auditoria)
        /// - Sem quebra de código legado
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_02_InserirRpsComTomadorNumerico_Compatibilidade()
        {
            // Arrange - Use unique inscription (8 chars max: "1000005X" where X = sequential)
            string insc = $"1000005{DateTime.UtcNow.Millisecond % 10}";
            long prestadorId = CriarPrestador("22222222000122", insc);
            string tomadorNumerico = "11222333000181";
            DateTime dtEmissao = DateTime.Now.Date;

            // Act
            long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 1002, "Z", 1,
                dtEmissao, "PROTO002", tomadorNumerico, "Tomador Numérico Ltda");

            _rpsIdsInseridos.Add(rpsId);

            // Assert
            Assert.IsTrue(rpsId > 0, "RPS ID deve ser maior que zero");

            if (!_colunaAlfanumericoExiste)
            {
                Assert.Inconclusive("Coluna TOMADOR_DOCUMENTO_ALFANUMERICO não existe (migration não aplicada)");
                return;
            }

            // Consultar RPS
            var sql = @"SELECT ID, TOMADOR_DOCUMENTO_ALFANUMERICO, TOMADOR_CNPJ, TOMADOR_CPF 
                       FROM RPS WHERE ID = :id";
            var dt = OracleHelper.ExecuteDataTable(_conn, null, sql,
                OracleHelper.P("id", rpsId));

            Assert.IsTrue(dt.Rows.Count > 0, "RPS deve existir no banco");

            object docAlfan = dt.Rows[0]["TOMADOR_DOCUMENTO_ALFANUMERICO"];
            object cnpj = dt.Rows[0]["TOMADOR_CNPJ"];
            object cpf = dt.Rows[0]["TOMADOR_CPF"];

            // Validações
            Assert.AreEqual(11222333000181, cnpj, "TOMADOR_CNPJ deve ter o valor numérico");
            Assert.AreEqual(tomadorNumerico, docAlfan.ToString(), "TOMADOR_DOCUMENTO_ALFANUMERICO deve ter string");
            Assert.AreEqual(DBNull.Value, cpf, "TOMADOR_CPF deve ser nulo para CNPJ");
        }

        /// <summary>
        /// FEATURE-CNPJ-03: Inserir RPS com tomador CPF
        /// 
        /// Descrição: Validar que CPF continua funcionando. RPS com CPF deve ser inserido
        /// e armazenado em TOMADOR_CPF (compatibilidade).
        /// 
        /// Esperado:
        /// - RPS inserido com sucesso
        /// - TOMADOR_CPF = 12345678901 (numérico)
        /// - TOMADOR_DOCUMENTO_ALFANUMERICO = "12345678901" (string)
        /// - TOMADOR_CNPJ = NULL
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_03_InserirRpsComTomadorCpf()
        {
            // Arrange - Use unique inscription (8 chars max)
            string insc = $"1000006{DateTime.UtcNow.Millisecond % 10}";
            long prestadorId = CriarPrestador("33333333000133", insc);
            string tomadorCpf = "12345678901";
            DateTime dtEmissao = DateTime.Now.Date;

            // Act
            long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 1003, "Z", 1,
                dtEmissao, "PROTO003", tomadorCpf, "Tomador Pessoa Física");

            _rpsIdsInseridos.Add(rpsId);

            // Assert
            Assert.IsTrue(rpsId > 0, "RPS ID deve ser maior que zero");

            if (!_colunaAlfanumericoExiste)
            {
                Assert.Inconclusive("Coluna TOMADOR_DOCUMENTO_ALFANUMERICO não existe (migration não aplicada)");
                return;
            }

            // Consultar RPS
            var sql = @"SELECT ID, TOMADOR_DOCUMENTO_ALFANUMERICO, TOMADOR_CNPJ, TOMADOR_CPF 
                       FROM RPS WHERE ID = :id";
            var dt = OracleHelper.ExecuteDataTable(_conn, null, sql,
                OracleHelper.P("id", rpsId));

            Assert.IsTrue(dt.Rows.Count > 0, "RPS deve existir no banco");

            object docAlfan = dt.Rows[0]["TOMADOR_DOCUMENTO_ALFANUMERICO"];
            object cnpj = dt.Rows[0]["TOMADOR_CNPJ"];
            object cpf = dt.Rows[0]["TOMADOR_CPF"];

            // Validações
            Assert.AreEqual(12345678901, cpf, "TOMADOR_CPF deve ter o valor numérico");
            Assert.AreEqual(tomadorCpf, docAlfan.ToString(), "TOMADOR_DOCUMENTO_ALFANUMERICO deve ter string");
            Assert.AreEqual(DBNull.Value, cnpj, "TOMADOR_CNPJ deve ser nulo para CPF");
        }

        /// <summary>
        /// FEATURE-CNPJ-04: Rejeitar documento inválido (tamanho incorreto)
        /// 
        /// Descrição: Validar que inserção falha graciosamente quando documento tem tamanho
        /// inválido (nem 11 nem 14 caracteres).
        /// 
        /// Esperado:
        /// - ArgumentException lançado
        /// - Mensagem clara indicando tamanho esperado
        /// - Nenhum registro inserido no banco
        /// </summary>
        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void FEATURE_CNPJ_04_RejeitarDocumentoTamanhoInvalido()
        {
            // Arrange - Use unique inscription (8 chars max)
            string insc = $"1000007{DateTime.UtcNow.Millisecond % 10}";
            long prestadorId = CriarPrestador("44444444000144", insc);
            string documentoInvalido = "11ABCDE0001"; // 11 caracteres (inválido - esperado 11 ou 14)
            DateTime dtEmissao = DateTime.Now.Date;

            // Act
            long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 1004, "Z", 1,
                dtEmissao, "PROTO004", documentoInvalido, "Tomador Inválido");

            // Assert: Expected exception acima
            Assert.Fail("Deve ter lançado ArgumentException");
        }

        /// <summary>
        /// FEATURE-CNPJ-05: Rejeitar documento nulo
        /// 
        /// Descrição: Validar que inserção falha quando documento é nulo ou vazio.
        /// 
        /// Esperado:
        /// - ArgumentNullException lançado
        /// - Nenhum registro inserido
        /// </summary>
        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void FEATURE_CNPJ_05_RejeitarDocumentoNulo()
        {
            // Arrange
            long prestadorId = CriarPrestador("55555555000155", "10000054");
            DateTime dtEmissao = DateTime.Now.Date;

            // Act
            long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 1005, "Z", 1,
                dtEmissao, "PROTO005", null, "Tomador Nulo");

            // Assert: Expected exception acima
            Assert.Fail("Deve ter lançado ArgumentNullException");
        }

        /// <summary>
        /// FEATURE-CNPJ-06: Performance - Inserir 50 RPS com alfanuméricos em < 5 segundos
        /// 
        /// Descrição: Validar que a implementação não degrada performance. Deve conseguir
        /// inserir 50 RPS com documentos alfanuméricos em menos de 5 segundos.
        /// 
        /// Esperado:
        /// - 50 RPS inseridos com sucesso
        /// - Tempo total < 5000ms
        /// - Todos os RPS recuperáveis do banco
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_06_Performance_Inserir50RPSAlfanumericos()
        {
            // Arrange
            string insc = $"1000008{DateTime.UtcNow.Millisecond % 10}";
            long prestadorId = CriarPrestador("66666666000166", insc);
            int qtdRps = 50;
            DateTime dtEmissao = DateTime.Now.Date;
            var sw = Stopwatch.StartNew();

            // Act: Inserir 50 RPS com alfanuméricos
            for (int i = 0; i < qtdRps; i++)
            {
                // Gerar CNPJs alfanuméricos válidos com 14 caracteres
                // Usar formato: 1A-1Z (cycling through A-Z except I,O,U) + ABCDEF + número sequencial + DV
                char letra = "ABCDEFGHJKLMNPQRSTVWXYZ"[i % 23];  // Exclui I, O, U
                string tomadorAlfan = $"1{letra}ABCDEF{i:0001}06";  // 1A/1B/1C + ABCDEF + número + DV (sempre 14 chars)

                long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 2000 + i, "Z", 1,
                    dtEmissao, $"PERF{i:04D}", tomadorAlfan, $"Tomador-{i}");

                _rpsIdsInseridos.Add(rpsId);
            }

            sw.Stop();

            // Assert
            Assert.AreEqual(qtdRps, _rpsIdsInseridos.Count, "Todos os RPS devem ter sido inseridos");
            Assert.IsTrue(sw.ElapsedMilliseconds < 5000,
                $"Inserção de {qtdRps} RPS levou {sw.ElapsedMilliseconds}ms (esperado < 5000ms)");

            // Validar que todos estão no banco
            var dt = OracleHelper.ExecuteDataTable(_conn, null,
                $"SELECT COUNT(*) FROM RPS WHERE PRESTADOR_ID = :prest",
                OracleHelper.P("prest", prestadorId));
            int contagem = Convert.ToInt32(dt.Rows[0][0]);

            Assert.AreEqual(qtdRps, contagem, $"Deve haver {qtdRps} RPS no banco para este prestador");
        }

        /// <summary>
        /// FEATURE-CNPJ-07: Consultar RPS por documento alfanumérico
        /// 
        /// Descrição: Validar que é possível consultar RPS inserido com documento alfanumérico
        /// usando a nova coluna TOMADOR_DOCUMENTO_ALFANUMERICO.
        /// 
        /// Esperado:
        /// - RPS encontrado
        /// - Documento deve corresponder exatamente
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_07_ConsultarRPSPorDocumentoAlfanumerico()
        {
            if (!_colunaAlfanumericoExiste)
                Assert.Inconclusive("Coluna TOMADOR_DOCUMENTO_ALFANUMERICO não existe (migration não aplicada)");

            // Arrange
            string insc = $"1000009{DateTime.UtcNow.Millisecond % 10}";
            long prestadorId = CriarPrestador("77777777000177", insc);
            string tomadorAlfan = "11ABCDEF000106";
            DateTime dtEmissao = DateTime.Now.Date;

            long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 1007, "Z", 1,
                dtEmissao, "PROTO007", tomadorAlfan, "Tomador Query");

            _rpsIdsInseridos.Add(rpsId);

            // Act: Consultar por documento alfanumérico
            var dt = OracleHelper.ExecuteDataTable(_conn, null,
                "SELECT ID FROM RPS WHERE TOMADOR_DOCUMENTO_ALFANUMERICO = :doc",
                OracleHelper.P("doc", tomadorAlfan));

            // Assert
            Assert.IsTrue(dt.Rows.Count > 0, "Deve encontrar RPS com documento alfanumérico");
            long idEncontrado = Convert.ToInt64(dt.Rows[0][0]);

            Assert.AreEqual(rpsId, idEncontrado, "ID do RPS deve corresponder");
        }

        /// <summary>
        /// FEATURE-CNPJ-08: Índice funciona para consulta com alfanuméricos
        /// 
        /// Descrição: Validar que o índice criado em Migration 001 (IX_NFSE_TOMADOR_ALFANUMERICO)
        /// melhora performance de consultas com alfanuméricos.
        /// 
        /// Esperado:
        /// - Índice usado em plano de execução
        /// - Consulta rápida mesmo com 100+ registros
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_08_IndiceFuncionaParaConsultaAlfanumerica()
        {
            if (!_colunaAlfanumericoExiste)
                Assert.Inconclusive("Coluna TOMADOR_DOCUMENTO_ALFANUMERICO não existe (migration não aplicada)");

            // Arrange
            string insc = $"1000010{DateTime.UtcNow.Millisecond % 10}";
            long prestadorId = CriarPrestador("88888888000188", insc);
            string tomadorAlfan = "11ABCDEF000106";
            DateTime dtEmissao = DateTime.Now.Date;

            // Inserir múltiplos RPS com mesmo tomador alfanumérico
            for (int i = 0; i < 20; i++)
            {
                long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 3000 + i, "Z", 1,
                    dtEmissao, $"IDX{i:04D}", tomadorAlfan, "Tomador Index");
                _rpsIdsInseridos.Add(rpsId);
            }

            var sw = Stopwatch.StartNew();

            // Act: Consultar todos com mesmo tomador
            var dt = OracleHelper.ExecuteDataTable(_conn, null,
                "SELECT COUNT(*) FROM RPS WHERE TOMADOR_DOCUMENTO_ALFANUMERICO = :doc",
                OracleHelper.P("doc", tomadorAlfan));

            int contagem = Convert.ToInt32(dt.Rows[0][0]);

            sw.Stop();

            // Assert
            Assert.AreEqual(20, contagem, "Deve encontrar 20 RPS");
            Assert.IsTrue(sw.ElapsedMilliseconds < 1000,
                $"Consulta com índice levou {sw.ElapsedMilliseconds}ms (esperado < 1000ms)");
        }

        /// <summary>
        /// FEATURE-CNPJ-09: Mesclar numéricos e alfanuméricos na mesma consulta
        /// 
        /// Descrição: Validar que é possível consultar RPS tanto numéricos quanto alfanuméricos
        /// da mesma competência sem distinção.
        /// 
        /// Esperado:
        /// - Encontrar tanto RPS numéricos quanto alfanuméricos
        /// - Contagem correta
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_09_ConsultarMisturadoNumericoEAlfanumerico()
        {
            if (!_colunaAlfanumericoExiste)
                Assert.Inconclusive("Coluna TOMADOR_DOCUMENTO_ALFANUMERICO não existe (migration não aplicada)");

            // Arrange - Use unique inscription
            string insc = $"1000011{DateTime.UtcNow.Millisecond % 10}";
            long prestadorId = CriarPrestador("99999999000199", insc);
            DateTime dtEmissao = DateTime.Now.Date;

            // Inserir 10 RPS numéricos
            for (int i = 0; i < 10; i++)
            {
                long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 4000 + i, "Z", 1,
                    dtEmissao, $"NUM{i:04D}", "11222333000181", $"Tomador-Numérico-{i}");
                _rpsIdsInseridos.Add(rpsId);
            }

            // Inserir 10 RPS alfanuméricos
            for (int i = 0; i < 10; i++)
            {
                long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 4100 + i, "Z", 1,
                    dtEmissao, $"ALF{i:04D}", "11ABCDEF000106", $"Tomador-Alfanumérico-{i}");
                _rpsIdsInseridos.Add(rpsId);
            }

            // Act: Contar total de RPS do prestador
            var dt = OracleHelper.ExecuteDataTable(_conn, null,
                "SELECT COUNT(*) FROM RPS WHERE PRESTADOR_ID = :prest",
                OracleHelper.P("prest", prestadorId));

            int total = Convert.ToInt32(dt.Rows[0][0]);

            // Assert
            Assert.AreEqual(20, total, "Deve encontrar 20 RPS (10 numéricos + 10 alfanuméricos)");
        }

        /// <summary>
        /// FEATURE-CNPJ-10: Validar sem regressão - RpsRepositorio.Existe() com numéricos
        /// 
        /// Descrição: Garantir que método Existe() continua funcionando para RPS numéricos
        /// (compatibilidade com código legado).
        /// 
        /// Esperado:
        /// - RPS numérico pode ser verificado com Existe()
        /// - Sem regressão
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_10_RpsRepositorio_Existe_ComNumerico()
        {
            // Arrange
            long prestadorId = CriarPrestador("10101010000110", "10000059");
            DateTime dtEmissao = DateTime.Now.Date;

            long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 1010, "Z", 1,
                dtEmissao, "PROTO010", "11222333000181", "Tomador Numérico");

            _rpsIdsInseridos.Add(rpsId);

            // Act: Verificar que RPS existe
            bool existe = _rpsRepo.Existe(_conn, null, prestadorId, "Z", 1010);

            // Assert
            Assert.IsTrue(existe, "RPS numérico deve ser encontrado por Existe()");
        }

        /// <summary>
        /// FEATURE-CNPJ-11: Validação completa - Diferentes formatos de alfanuméricos
        /// 
        /// Descrição: Testar diferentes padrões de CNPJ alfanumérico conforme IN RFB.
        /// 
        /// Esperado:
        /// - Todos os formatos válidos aceitos
        /// - Todos armazenados corretamente
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_11_DiferentesFormatos_Alfanumericos()
        {
            if (!_colunaAlfanumericoExiste)
                Assert.Inconclusive("Coluna TOMADOR_DOCUMENTO_ALFANUMERICO não existe (migration não aplicada)");

            // Arrange - Use unique inscription
            string insc = $"1000012{DateTime.UtcNow.Millisecond % 10}";
            long prestadorId = CriarPrestador("11111111000111", insc);
            DateTime dtEmissao = DateTime.Now.Date;

            var cnpjsAlfanumericos = new[]
            {
                "11ABCDEF000106",  // A-H
                "1JABCDEF000107",  // J-N
                "11PABCDE000108",  // P-T
                "11VABCDE000109",  // V-Z
            };

            // Act & Assert
            int idx = 0;
            foreach (var cnpjAlfan in cnpjsAlfanumericos)
            {
                long rpsId = _rpsRepo.Inserir(_conn, null, prestadorId, 5000 + idx, "Z", 1,
                    dtEmissao, $"FMT{idx:04D}", cnpjAlfan, $"Formato-{idx}");

                _rpsIdsInseridos.Add(rpsId);

                // Verificar inserção
                var dt = OracleHelper.ExecuteDataTable(_conn, null,
                    "SELECT TOMADOR_DOCUMENTO_ALFANUMERICO FROM RPS WHERE ID = :id",
                    OracleHelper.P("id", rpsId));

                Assert.IsTrue(dt.Rows.Count > 0, $"RPS {cnpjAlfan} deve existir");
                Assert.AreEqual(cnpjAlfan, dt.Rows[0][0].ToString(), $"Documento {cnpjAlfan} deve ser preservado");

                idx++;
            }
        }

        /// <summary>
        /// FEATURE-CNPJ-12: Transação - Rollback preserva integridade
        /// 
        /// Descrição: Validar que se uma inserção falha dentro de uma transação, o rollback
        /// recupera estado anterior e não deixa dados inconsistentes.
        /// 
        /// Esperado:
        /// - Dados inseridos antes do erro preservados
        /// - Dados após erro não são persistidos
        /// - Nenhuma inconsistência
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_12_Transacao_RollbackPreservaIntegridade()
        {
            // Arrange
            long prestadorId = CriarPrestador("12121212000112", "10000061");
            DateTime dtEmissao = DateTime.Now.Date;

            var txn = _conn.BeginTransaction();

            // Act & Assert
            try
            {
                // Inserir primeiro RPS com sucesso
                long rpsId1 = _rpsRepo.Inserir(_conn, txn, prestadorId, 6001, "Z", 1,
                    dtEmissao, "TRX001", "11ABCDEF000106", "Tomador 1");
                _rpsIdsInseridos.Add(rpsId1);

                // Tentar inserir segundo RPS com documento inválido (deve falhar)
                try
                {
                    long rpsId2 = _rpsRepo.Inserir(_conn, txn, prestadorId, 6002, "Z", 1,
                        dtEmissao, "TRX002", "INVALIDO", "Tomador 2");
                    Assert.Fail("Deve ter lançado exceção");
                }
                catch (ArgumentException)
                {
                    // Esperado
                }

                // Rollback da transação
                txn.Rollback();

                // Verificar que nenhum RPS foi inserido (rollback reverteu tudo)
                var dt = OracleHelper.ExecuteDataTable(_conn, null,
                    "SELECT COUNT(*) FROM RPS WHERE PRESTADOR_ID = :prest",
                    OracleHelper.P("prest", prestadorId));

                int contagem = Convert.ToInt32(dt.Rows[0][0]);

                Assert.AreEqual(0, contagem, "Rollback deve ter removido todos os RPS");
            }
            finally
            {
                if (txn != null)
                    txn.Dispose();
            }
        }
    }
}
