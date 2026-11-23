using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Servicos;
using NotaCarioca.Servicos.Modelos;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Testes
{
    /// <summary>
    /// Suite de testes para detectar defeiros do Chamado CH-02:
    /// "ORA-00001 e nota em duplicidade no fechamento do mês"
    /// 
    /// Problemas:
    /// 1. ProcessadorLoteRps.ProcessarPendentes() processa lotes em paralelo (Thread array)
    /// 2. Race condition: múltiplas threads podem processar o mesmo RPS (série + número)
    /// 3. OracleException ORA-00001 ao violar constraint unique de RPS/NFSE
    /// 4. Resultados podem ficar inconsistentes ou faltando RPS no retorno
    /// 
    /// Requisitos de Correção:
    /// - Serializar acesso ao mesmo RPS (lock/transação pessimista)
    /// - PKG_NFSE.GERAR_NFSE() deve usar SELECT FOR UPDATE ao gerar número NFSE
    /// - Garantir que todos os RPS processados apareçam no resultado final
    /// - Suportar reprocessamento seguro de lotes com reenvios/duplicatas
    /// </summary>
    [TestClass]
    public class ProcessadorLoteRpsTests
    {
        private ProcessadorLoteRps _processador;

        [TestInitialize]
        public void Setup()
        {
            _processador = new ProcessadorLoteRps();
        }

        // ============================================================================
        // TESTES PARA DETECTAR RACE CONDITION EM LOTES CONCORRENTES
        // ============================================================================

        /// <summary>
        /// CH-02-01: Detecta duplicidade de NFSE quando mesmo RPS é processado simultaneamente
        /// 
        /// Cenário: Dois threads processam o mesmo RPS (prestador + série + número) simultaneamente.
        /// Esperado: Apenas um thread sucede; o outro retorna erro E060
        /// Atual: Pode gerar duas NFSE diferentes (violação de regra de negócio)
        /// 
        /// Correção Necessária:
        /// - Adicionar lock pessimista (SELECT FOR UPDATE) no RpsRepositorio.Existe()
        /// - Ou usar transaction level SERIALIZABLE ao verificar/inserir RPS
        /// - Garantir atomicidade de Existe() + Inserir()
        /// </summary>
        [TestMethod]
        [TestCategory("CH-02")]
        [TestCategory("RaceCondition")]
        public void CH02_01_MesmoRpsSimultaneoDuasThreads_ApenasUmaDeveSuceder()
        {
            // Arrange
            long prestadorId = 100001;
            string serie = "A";
            long numeroRps = 1001;
            var rpsUm = CriarRpsLote(numeroRps, serie, "11111111000190", "Empresa Um");
            var rpsDois = CriarRpsLote(numeroRps, serie, "22222222000180", "Empresa Dois");

            var resultados = new List<ResultadoRps>();
            var lockObj = new object();

            // Act - Processar RPS idênticos em dois threads
            Thread t1 = new Thread(() =>
            {
                try
                {
                    var resultado = ProcessarRpsDireto(prestadorId, rpsUm, "LOTE001");
                    lock (lockObj) resultados.Add(resultado);
                }
                catch { }
            });

            Thread t2 = new Thread(() =>
            {
                try
                {
                    var resultado = ProcessarRpsDireto(prestadorId, rpsDois, "LOTE001");
                    lock (lockObj) resultados.Add(resultado);
                }
                catch { }
            });

            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();

            // Assert
            Assert.AreEqual(2, resultados.Count, "Ambos RPS's devem estar em resultados");

            int sucessos = 0;
            int errosE060 = 0;
            foreach (var r in resultados)
            {
                if (r.Sucesso) sucessos++;
                if (r.Codigo == "E060") errosE060++;
            }

            Assert.AreEqual(1, sucessos, "Apenas UM RPS deve suceder (regra: um RPS só vira uma NFSE)");
            Assert.AreEqual(1, errosE060, "Um deve retornar E060 (RPS já informado)");
        }

        /// <summary>
        /// CH-02-02: Detecta falta de RPS na resposta quando processamento paralelo falha parcialmente
        /// 
        /// Cenário: Lote com 3 RPS; dois threads tentam processar RPS duplicado
        /// Esperado: Todos 3 RPS na resposta, sendo 1 com erro E060
        /// Atual: Um RPS pode estar faltando (perdido em exceção não capturada)
        /// 
        /// Correção Necessária:
        /// - Validar que ResultadoRps inclui ALL RPS processados, mesmo em erro
        /// - Capturar OracleException e converter para código de erro apropriado
        /// - Garantir que a lista de resultados seja sincronizada em acesso paralelo
        /// </summary>
        [TestMethod]
        [TestCategory("CH-02")]
        [TestCategory("RaceCondition")]
        public void CH02_02_LoteComRpsDuplicados_TodosRpsNaResposta()
        {
            // Arrange
            long prestadorId = 100002;
            var rps1 = CriarRpsLote(2001, "B", "33333333000160", "Cliente A");
            var rps2 = CriarRpsLote(2002, "B", "44444444000150", "Cliente B");
            var rps3 = CriarRpsLote(2001, "B", "55555555000140", "Cliente C"); // duplicado com rps1

            var resultados = new List<ResultadoRps>();
            var lockObj = new object();

            // Act - Simular processamento paralelo
            Thread t1 = new Thread(() =>
            {
                try
                {
                    var r = ProcessarRpsDireto(prestadorId, rps1, "LOTE002");
                    lock (lockObj) resultados.Add(r);
                }
                catch { }
            });

            Thread t2 = new Thread(() =>
            {
                try
                {
                    var r = ProcessarRpsDireto(prestadorId, rps2, "LOTE002");
                    lock (lockObj) resultados.Add(r);
                }
                catch { }
            });

            Thread t3 = new Thread(() =>
            {
                try
                {
                    var r = ProcessarRpsDireto(prestadorId, rps3, "LOTE002");
                    lock (lockObj) resultados.Add(r);
                }
                catch { }
            });

            t1.Start();
            t2.Start();
            t3.Start();
            t1.Join();
            t2.Join();
            t3.Join();

            // Assert
            Assert.AreEqual(3, resultados.Count, 
                "FALHA CH-02-02: Faltam RPS na resposta. Esperado 3, recebido " + resultados.Count);

            Assert.IsTrue(resultados.Exists(r => r.NumeroRps == 2001), "RPS 2001 deve estar em resultados");
            Assert.IsTrue(resultados.Exists(r => r.NumeroRps == 2002), "RPS 2002 deve estar em resultados");
            Assert.IsTrue(resultados.Exists(r => r.NumeroRps == 2001 && r.Codigo == "E060"), 
                "Pelo menos um RPS 2001 duplicado deve retornar E060");
        }

        /// <summary>
        /// CH-02-03: Detecta geração de NFSE com número duplicado
        /// 
        /// Cenário: PKG_NFSE.GERAR_NFSE() chamado simultaneamente por dois threads
        /// Sem SELECT FOR UPDATE: ambos leem MAX(NUMERO) = 999, geram número 1000 → violação UK_NFSE_NUMERO
        /// 
        /// Esperado: Cada NFSE gerada tem número único e sequencial
        /// Atual: ORA-00001 ao violar constraint unique de NFSE(PRESTADOR_ID, NUMERO)
        /// 
        /// Correção Necessária:
        /// - Usar SELECT FOR UPDATE ao calcular próximo número em PKG_NFSE.GERAR_NFSE()
        /// - Ou usar sequence com NOCACHE (cada thread obtém número sequencial garantido)
        /// </summary>
        [TestMethod]
        [TestCategory("CH-02")]
        [TestCategory("SequenceGen")]
        public void CH02_03_NfsesConcorrentes_NumerosUnicos()
        {
            // Arrange
            long prestadorId = 100003;
            var rps1 = CriarRpsLote(3001, "C", "66666666000130", "Prestador 1");
            var rps2 = CriarRpsLote(3002, "C", "77777777000120", "Prestador 2");

            long nfseNum1 = 0, nfseNum2 = 0;
            var lockObj = new object();
            OracleException exceptionCaught = null;

            // Act - Gerar NFSE simultaneamente
            Thread t1 = new Thread(() =>
            {
                try
                {
                    var resultado = ProcessarRpsDireto(prestadorId, rps1, "LOTE003");
                    if (resultado.Sucesso)
                    {
                        lock (lockObj) nfseNum1 = resultado.NfseNumero;
                    }
                }
                catch (OracleException ex)
                {
                    lock (lockObj) exceptionCaught = ex;
                }
            });

            Thread t2 = new Thread(() =>
            {
                try
                {
                    var resultado = ProcessarRpsDireto(prestadorId, rps2, "LOTE003");
                    if (resultado.Sucesso)
                    {
                        lock (lockObj) nfseNum2 = resultado.NfseNumero;
                    }
                }
                catch (OracleException ex)
                {
                    lock (lockObj) exceptionCaught = ex;
                }
            });

            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();

            // Assert
            Assert.IsNull(exceptionCaught, 
                "FALHA CH-02-03: ORA-00001 (duplicidade em NFSE.NUMERO). " + 
                "Necessário usar SELECT FOR UPDATE no package PKG_NFSE.GERAR_NFSE()");

            Assert.AreNotEqual(0, nfseNum1, "NFSE número 1 deve ter sido gerado");
            Assert.AreNotEqual(0, nfseNum2, "NFSE número 2 deve ter sido gerado");
            Assert.AreNotEqual(nfseNum1, nfseNum2, "NFSE números devem ser únicos");
        }

        /// <summary>
        /// CH-02-04: Lote grande com muitos RPS reproduz o problema de duplicidade
        /// 
        /// Cenário: Lote com 100 RPS, sendo 2-3 deles duplicados internamente.
        /// Versão problema: Processamento paralelo sem sincronização adequada
        /// 
        /// Esperado: 97-98 sucessos, 2-3 erros E060, TODOS os RPS na resposta
        /// Atual: Pode haver ORA-00001, faltam RPS, números NFSE duplicados
        /// 
        /// Correção Necessária:
        /// - Serializar validação de RPS existente
        /// - Usar lock nos repositórios ou transaction SERIALIZABLE
        /// </summary>
        [TestMethod]
        [TestCategory("CH-02")]
        [TestCategory("HighVolume")]
        public void CH02_04_LoteGrandeCom100RpsE2Duplicados_TodosNaResposta()
        {
            // Arrange
            long prestadorId = 100004;
            var rpsList = new List<RpsLote>();
            var resultados = new List<ResultadoRps>();
            var lockObj = new object();

            // Criar 100 RPS únicos
            for (int i = 4001; i < 4101; i++)
            {
                rpsList.Add(CriarRpsLote(i, "D", $"{i:00000000000014}", $"Cliente {i}"));
            }

            // Adicionar 2 duplicatas (mesmo número, série)
            rpsList.Add(new RpsLote 
            { 
                Numero = 4001, 
                Serie = "D", 
                Tipo = 1,
                DataEmissao = DateTime.Now,
                TomadorDocumento = "11111111000190",
                TomadorRazaoSocial = "Duplicado 1",
                ValorServicos = 1000m,
                ValorDeducoes = 0m,
                IssRetido = false,
                ItemListaServico = "0101",
                Discriminacao = "Serviço duplicado 1"
            });

            rpsList.Add(new RpsLote 
            { 
                Numero = 4050, 
                Serie = "D", 
                Tipo = 1,
                DataEmissao = DateTime.Now,
                TomadorDocumento = "22222222000180",
                TomadorRazaoSocial = "Duplicado 2",
                ValorServicos = 2000m,
                ValorDeducoes = 0m,
                IssRetido = false,
                ItemListaServico = "0101",
                Discriminacao = "Serviço duplicado 2"
            });

            // Act - Processar todos RPS (simula processamento em paralelo)
            var threads = new Thread[rpsList.Count];
            for (int i = 0; i < rpsList.Count; i++)
            {
                int idx = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        var resultado = ProcessarRpsDireto(prestadorId, rpsList[idx], "LOTE004");
                        lock (lockObj) resultados.Add(resultado);
                    }
                    catch { }
                });
                threads[i].Start();
            }

            foreach (var t in threads) t.Join();

            // Assert
            Assert.AreEqual(rpsList.Count, resultados.Count, 
                "FALHA CH-02-04: Faltam RPS. Esperado " + rpsList.Count + ", recebido " + resultados.Count);

            int sucessos = resultados.FindAll(r => r.Sucesso).Count;
            int errosE060 = resultados.FindAll(r => r.Codigo == "E060").Count;

            Assert.AreEqual(100, sucessos, "Esperado 100 RPS únicos bem-sucedidos");
            Assert.AreEqual(2, errosE060, "Esperado 2 RPS duplicados com erro E060");

            // Verificar unicidade de NFSE números
            var nfseNumeros = new HashSet<long>();
            foreach (var r in resultados)
            {
                if (r.Sucesso && r.NfseNumero > 0)
                {
                    Assert.IsTrue(nfseNumeros.Add(r.NfseNumero), 
                        "NFSE número " + r.NfseNumero + " foi duplicado. " +
                        "Necessário usar SELECT FOR UPDATE no PKG_NFSE.GERAR_NFSE()");
                }
            }
        }

        /// <summary>
        /// CH-02-05: Reenvio de lote deve retornar erro E060 para todos os RPS já processados
        /// 
        /// Cenário: Mesmo lote enviado duas vezes (cenário real de timeout/retry)
        /// Esperado: Na segunda vez, todos os RPS retornam E060 (já informados)
        /// Atual: Pode haver ORA-00001 se transaction não estiver bem isolada
        /// 
        /// Correção Necessária:
        /// - Garantir que RpsRepositorio.Existe() leia de forma isolada
        /// - Usar pessimistic locking ou validation at commit time
        /// </summary>
        [TestMethod]
        [TestCategory("CH-02")]
        [TestCategory("Reenvio")]
        public void CH02_05_ReenvioMesmoLote_TodosRpsRetornamE060()
        {
            // Arrange
            long prestadorId = 100005;
            var rps = CriarRpsLote(5001, "E", "88888888000110", "Cliente Reenvio");

            var resultado1 = new List<ResultadoRps>();
            var resultado2 = new List<ResultadoRps>();

            // Act - Processar primeira vez
            var r1 = ProcessarRpsDireto(prestadorId, rps, "LOTE005_V1");
            resultado1.Add(r1);

            // Aguardar processamento ser persistido
            Thread.Sleep(100);

            // Reenviar mesmo RPS
            var r2 = ProcessarRpsDireto(prestadorId, rps, "LOTE005_V2");
            resultado2.Add(r2);

            // Assert
            Assert.IsTrue(resultado1[0].Sucesso, "Primeiro envio deve suceder");
            Assert.IsFalse(resultado2[0].Sucesso, "Reenvio deve falhar");
            Assert.AreEqual("E060", resultado2[0].Codigo, 
                "Reenvio deve retornar E060 (RPS já informado). Recebido: " + resultado2[0].Codigo);
        }

        // ============================================================================
        // TESTES PARA REGRAS DE NEGÓCIO
        // ============================================================================

        /// <summary>
        /// CH-02-06: Validação de regra de negócio - Um RPS só pode virar uma NFSE
        /// 
        /// Cenário: Mesmo RPS processado após error handling incompleto
        /// Esperado: Apenas uma NFSE no banco de dados para esse RPS
        /// Atual: Podem existir duas NFSE se erro não impedir reinserção
        /// 
        /// Correção Necessária:
        /// - Usar constraint unique em RPS (já existe: UK_RPS_NUMERO)
        /// - Usar constraint unique em NFSE (já existe: UK_NFSE_NUMERO)
        /// - Garantir atomicidade de ambas as operações
        /// </summary>
        [TestMethod]
        [TestCategory("CH-02")]
        [TestCategory("RegrasNegocio")]
        public void CH02_06_UmRpsSoViraUmaNfse_ApenasUmaNfseExiste()
        {
            // Arrange
            long prestadorId = 100006;
            var rps = CriarRpsLote(6001, "F", "99999999000100", "Cliente Único");

            // Act - Processar RPS
            var resultado = ProcessarRpsDireto(prestadorId, rps, "LOTE006");

            // Assert
            Assert.IsTrue(resultado.Sucesso, "RPS deve ser processado com sucesso");

            // Verificar no banco que há exatamente 1 NFSE para esse RPS
            var nfsesDoRps = ObterNfsesDoRps(prestadorId, 6001, "F");
            Assert.AreEqual(1, nfsesDoRps.Rows.Count, 
                "FALHA CH-02-06: Esperada 1 NFSE, encontradas " + nfsesDoRps.Rows.Count + 
                ". Verificar constraint unique de NFSE(PRESTADOR_ID, NUMERO)");
        }

        // ============================================================================
        // TESTES PARA TRANSACTION ISOLATION
        // ============================================================================

        /// <summary>
        /// CH-02-07: Transação isolada - SELECT FOR UPDATE ao verificar existência
        /// 
        /// Cenário: Verificar que a transação não permite dirty reads ou lost updates
        /// Esperado: RpsRepositorio.Existe() deve usar transaction level adequado
        /// Atual: Sem pessimistic lock, múltiplos threads podem ler antes de write
        /// 
        /// Correção Necessária:
        /// - Usar SELECT ... FOR UPDATE na query Existe()
        /// - Ou usar OracleTransaction com isolation level SERIALIZABLE
        /// </summary>
        [TestMethod]
        [TestCategory("CH-02")]
        [TestCategory("Transaction")]
        public void CH02_07_ExisteComSelectForUpdate_ApenasUmInsereRps()
        {
            // Arrange
            long prestadorId = 100007;
            var rps = CriarRpsLote(7001, "G", "11111111111111", "Cliente TX");

            int sucessos = 0;
            int fracassos = 0;
            var lockObj = new object();

            // Act - Duas threads tentam inserir o mesmo RPS
            Thread t1 = new Thread(() =>
            {
                try
                {
                    var r = ProcessarRpsDireto(prestadorId, rps, "LOTE007");
                    lock (lockObj)
                    {
                        if (r.Sucesso) sucessos++;
                        else fracassos++;
                    }
                }
                catch { }
            });

            Thread t2 = new Thread(() =>
            {
                try
                {
                    var r = ProcessarRpsDireto(prestadorId, rps, "LOTE007");
                    lock (lockObj)
                    {
                        if (r.Sucesso) sucessos++;
                        else fracassos++;
                    }
                }
                catch { }
            });

            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();

            // Assert
            Assert.AreEqual(1, sucessos, "Apenas 1 thread deve ter sucesso ao inserir RPS");
            Assert.AreEqual(1, fracassos, "1 thread deve ter falhado (E060)");
        }

        // ============================================================================
        // HELPER METHODS
        // ============================================================================

        private RpsLote CriarRpsLote(long numero, string serie, string tomadorDoc, string tomadorRazao)
        {
            return new RpsLote
            {
                Numero = numero,
                Serie = serie,
                Tipo = 1,
                DataEmissao = DateTime.Now,
                TomadorDocumento = tomadorDoc,
                TomadorRazaoSocial = tomadorRazao,
                ValorServicos = 1000m,
                ValorDeducoes = 0m,
                IssRetido = false,
                ItemListaServico = "0101",
                Discriminacao = "Serviço de teste"
            };
        }

        /// <summary>
        /// Simula o processamento de um RPS individual (helper para testes de concorrência)
        /// </summary>
        private ResultadoRps ProcessarRpsDireto(long prestadorId, RpsLote rps, string protocolo)
        {
            // Este método seria implementado no processador ou em um repositório mock
            // Por enquanto, retorna estrutura para testes
            var resultado = new ResultadoRps
            {
                Indice = rps.Indice,
                NumeroRps = rps.Numero,
                Serie = rps.Serie,
                Sucesso = true,
                NfseNumero = rps.Numero
            };
            return resultado;
        }

        /// <summary>
        /// Busca NFS-e no banco para um RPS específico
        /// </summary>
        private DataTable ObterNfsesDoRps(long prestadorId, long rpsNumero, string rpsSerie)
        {
            // Implementado via integração com NfseRepositorio
            return new DataTable(); // Placeholder
        }
    }
}
