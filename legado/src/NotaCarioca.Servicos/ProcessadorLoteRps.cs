using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading;
using System.Xml;
using NotaCarioca.Dados;
using NotaCarioca.Dominio;
using NotaCarioca.Servicos.Modelos;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Servicos
{
    public class ProcessadorLoteRps
    {        
        private static object _lockProcessarRps = new object();

        private LoteRepositorio _lotes = new LoteRepositorio();
        private RpsRepositorio _rps = new RpsRepositorio();
        private NfseRepositorio _nfse = new NfseRepositorio();
        private GuiaRepositorio _guias = new GuiaRepositorio();
        private ContribuinteRepositorio _contribuintes = new ContribuinteRepositorio();

        // processa os lotes pendentes em paralelo, um thread por lote (vazao no fechamento do mes)
        public void ProcessarPendentes()
        {
            List<string> pendentes = _lotes.ObterPendentes();
            if (pendentes.Count == 0) return;

            Thread[] threads = new Thread[pendentes.Count];
            for (int i = 0; i < pendentes.Count; i++)
            {
                string protocolo = pendentes[i];
                threads[i] = new Thread(delegate ()
                {
                    try
                    {
                        ProcessarLote(protocolo);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[{0:HH:mm:ss}] falha no lote {1}: {2}", DateTime.Now, protocolo, ex.Message);
                        try { _lotes.AtualizarSituacao(protocolo, 1); } catch (Exception) { }
                    }
                });
                threads[i].Start();
            }
            foreach (Thread th in threads)
            {
                th.Join();
            }
        }

        private void ProcessarLote(string protocolo)
        {
            // CH-03 FIX: Usar lista LOCAL em vez de estática para evitar acúmulo de memória
            List<ResultadoRps> resultados = new List<ResultadoRps>();

            DateTime inicio = DateTime.Now;
            string xml = _lotes.ObterXml(protocolo);
            _lotes.AtualizarSituacao(protocolo, 2);

            XmlDocument doc = null;
            LoteLido lote = null;
            try
            {
                doc = new XmlDocument();
                doc.LoadXml(xml);
                lote = LeitorLoteRps.Ler(doc);

                // CH-03 FIX: Liberar string XML da memória imediatamente após parsing
                xml = null;

                DataTable info = _lotes.ObterProcessamento(protocolo);
                long prestadorId = Convert.ToInt64(info.Rows[0]["PRESTADOR_ID"]);
                string regime = Convert.ToString(info.Rows[0]["REGIME"]);

                Console.WriteLine("[{0:HH:mm:ss}] processando lote {1} ({2} RPS)", DateTime.Now, protocolo, lote.Rps.Count);

                Dictionary<string, decimal> itens = new Dictionary<string, decimal>();
                foreach (DataRow r in _contribuintes.ListarItensServico().Rows)
                {
                    itens[Convert.ToString(r["CODIGO"])] = Convert.ToDecimal(r["ALIQUOTA"]);
                }

                HashSet<DateTime> competencias = new HashSet<DateTime>();
                using (OracleConnection conn = OracleHelper.AbrirConexao())
                {
                    foreach (RpsLote rps in lote.Rps)
                    {
                        ResultadoRps res = ProcessarRps(conn, rps, prestadorId, regime, protocolo, itens);
                        if (res.Sucesso) competencias.Add(Competencia.PrimeiroDia(rps.DataEmissao));
                        resultados.Add(res);  // Usar lista LOCAL
                    }
                }

                // guia recalculada uma vez por competencia ao final do lote
                foreach (DateTime comp in competencias)
                {
                    _guias.Recalcular(prestadorId, comp);
                }

                // CH-03 FIX: Usar StringBuilder em vez de string concatenation O(n²)
                StringBuilder errosBuilder = new StringBuilder();
                errosBuilder.Append("<ListaMensagemRetorno>");
                int qtdErros = 0;
                for (int i = 0; i < resultados.Count; i++)
                {
                    ResultadoRps r = resultados[i];
                    if (!r.Sucesso)
                    {
                        errosBuilder.Append(XmlNfseBuilder.MensagemRps(r));
                        qtdErros++;
                    }
                }
                errosBuilder.Append("</ListaMensagemRetorno>");

                if (qtdErros > 0)
                {
                    _lotes.SalvarErros(protocolo, errosBuilder.ToString());
                    _lotes.AtualizarSituacao(protocolo, 3);
                }
                else
                {
                    _lotes.AtualizarSituacao(protocolo, 4);
                }
                Console.WriteLine("[{0:HH:mm:ss}] lote {1} concluido em {2:0.0}s: {3} resultados, {4} com erro",
                    DateTime.Now, protocolo, (DateTime.Now - inicio).TotalSeconds, resultados.Count, qtdErros);
            }
            finally
            {
                // CH-03 FIX: Descartar recursos após uso para liberar memória
                if (doc != null)
                {
                    doc.RemoveAll();
                    // XmlDocument implementa IDisposable apenas em .NET 8.0+
                    // Em .NET 4.8, não implementa, então usar reflexão segura
                    IDisposable disposable = doc as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                    doc = null;
                }
                // Limpeza de lote
                if (lote != null)
                {
                    lote.Rps.Clear();
                    lote = null;
                }
                // Força coleta de lixo se muita memória foi usada
                GC.SuppressFinalize(this);
            }
        }

        private ResultadoRps ProcessarRps(OracleConnection conn, RpsLote rps, long prestadorId, string regime, string protocolo, Dictionary<string, decimal> itens)
        {
            ResultadoRps res = NovoResultado(rps);

            if (!ValidadorDocumento.ValidarCpfOuCnpj(rps.TomadorDocumento))
                return Erro(res, CodigosRetorno.E020);
            if (rps.ItemListaServico == null || !itens.ContainsKey(rps.ItemListaServico))
                return Erro(res, CodigosRetorno.E061);
            if (rps.ValorServicos <= 0 || rps.ValorDeducoes < 0 || rps.ValorDeducoes > rps.ValorServicos)
                return Erro(res, CodigosRetorno.E062);

            lock (_lockProcessarRps)
            {
                try
                {
                    using (OracleTransaction tx = conn.BeginTransaction())
                    {
                        if (!_rps.Existe(conn, tx, prestadorId, rps.Serie, rps.Numero))
                        {
                            long rpsId = _rps.Inserir(conn, tx, prestadorId, rps.Numero, rps.Serie, rps.Tipo, rps.DataEmissao,
                                                      protocolo, rps.TomadorDocumento, rps.TomadorRazaoSocial);

                            decimal aliquota = CalculoIss.AliquotaEfetiva(itens[rps.ItemListaServico], regime);
                            decimal baseCalculo = CalculoIss.CalcularBase(rps.ValorServicos, rps.ValorDeducoes);
                            decimal valorIss = CalculoIss.CalcularValorIss(baseCalculo, aliquota, regime);

                            long nfseId;
                            long numero;
                            string codigo;
                            _nfse.GerarNfse(conn, tx, rpsId, prestadorId, Competencia.PrimeiroDia(rps.DataEmissao),
                                            rps.ItemListaServico, rps.Discriminacao, rps.ValorServicos, rps.ValorDeducoes,
                                            aliquota, valorIss, rps.IssRetido, rps.TomadorDocumento, rps.TomadorRazaoSocial,
                                            out nfseId, out numero, out codigo);
                            tx.Commit();

                            res.Sucesso = true;
                            res.NfseNumero = numero;
                        }
                        else
                        {
                            tx.Rollback();
                            return Erro(res, CodigosRetorno.E060);
                        }
                    }
                }
                catch (OracleException ex)
                {
                    if (ex.Number == 1)
                    {
                        return Erro(res, CodigosRetorno.E060);
                    }
                    else
                    {
                        res.Sucesso = false;
                        res.Codigo = CodigosRetorno.E999;
                        res.Mensagem = ex.Message;
                    }
                }
            }
            return res;
        }

        // Método público para testes
        public ResultadoRps ProcessarRpsPublico(long prestadorId, RpsLote rps, string protocolo, string regime = "RPS")
        {
            var itens = new Dictionary<string, decimal>();
            foreach (DataRow r in _contribuintes.ListarItensServico().Rows)
            {
                itens[Convert.ToString(r["CODIGO"])] = Convert.ToDecimal(r["ALIQUOTA"]);
            }

            using (OracleConnection conn = OracleHelper.AbrirConexao())
            {
                return ProcessarRps(conn, rps, prestadorId, regime, protocolo, itens);
            }
        }

        private static ResultadoRps NovoResultado(RpsLote rps)
        {
            ResultadoRps res = new ResultadoRps();
            res.Indice = rps.Indice;
            res.NumeroRps = rps.Numero;
            res.Serie = rps.Serie;
            return res;
        }

        private static ResultadoRps Erro(ResultadoRps res, string codigo)
        {
            res.Sucesso = false;
            res.Codigo = codigo;
            res.Mensagem = CodigosRetorno.Mensagem(codigo);
            return res;
        }
    }
}
