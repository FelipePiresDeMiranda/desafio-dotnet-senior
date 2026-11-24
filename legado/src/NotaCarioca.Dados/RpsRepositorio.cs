using System;
using System.Data;
using System.Linq;
using NotaCarioca.Dominio;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Dados
{
    public class RpsRepositorio
    {
        private bool? _colunaAlfanumericoExiste;

        public bool Existe(OracleConnection conn, OracleTransaction tx, long prestadorId, string serie, long numero)
        {
            string sql = "SELECT ID FROM RPS WHERE PRESTADOR_ID = :prest AND SERIE = :serie AND NUMERO = :num FOR UPDATE";
            object o = OracleHelper.ExecuteScalar(conn, tx, sql,
                OracleHelper.P("prest", prestadorId),
                OracleHelper.P("serie", serie),
                OracleHelper.P("num", numero));
            return o != null && o != DBNull.Value;
        }

        /// <summary>
        /// Verifica se coluna TOMADOR_DOCUMENTO_ALFANUMERICO existe (detecta se migration foi aplicada)
        /// </summary>
        private bool ColunaAlfanumericoExiste(OracleConnection conn)
        {
            if (_colunaAlfanumericoExiste.HasValue)
                return _colunaAlfanumericoExiste.Value;

            try
            {
                // Tentar selecionar a coluna com ROWNUM = 0 (não retorna linhas, apenas verifica se coluna existe)
                OracleHelper.ExecuteScalar(conn, null,
                    "SELECT TOMADOR_DOCUMENTO_ALFANUMERICO FROM RPS WHERE ROWNUM = 0");
                _colunaAlfanumericoExiste = true;
                return true;
            }
            catch (OracleException ex) when (ex.Number == 904)  // ORA-00904: identificador inválido
            {
                _colunaAlfanumericoExiste = false;
                return false;
            }
            catch
            {
                // Em caso de outro erro, assume que a coluna não existe
                _colunaAlfanumericoExiste = false;
                return false;
            }
        }

        /// <summary>
        /// Detecta se um documento contém caracteres alfabéticos (indicador de CNPJ alfanumérico).
        /// FEATURE-CNPJ-ALFANUMERICO: Suporta novos CNPJs alfanuméricos (IN RFB nº 2.229/2024)
        /// </summary>
        private bool EhDocumentoAlfanumerico(string documento)
        {
            if (string.IsNullOrEmpty(documento))
                return false;
            return documento.Any(c => char.IsLetter(c));
        }

        public long Inserir(OracleConnection conn, OracleTransaction tx, long prestadorId, long numero, string serie, int tipo,
                            DateTime dtEmissao, string protocolo, string tomadorDoc, string tomadorRazao)
        {
            if (string.IsNullOrEmpty(tomadorDoc))
                throw new ArgumentNullException(nameof(tomadorDoc), "Documento do tomador não pode ser nulo ou vazio.");

            long id = Convert.ToInt64(OracleHelper.ExecuteScalar(conn, tx, "SELECT SEQ_RPS.NEXTVAL FROM DUAL"));

            // FEATURE-CNPJ-ALFANUMERICO: Detectar e armazenar corretamente cada tipo de documento
            object cnpj = DBNull.Value;
            object cpf = DBNull.Value;
            object docAlfanumerico = DBNull.Value;
            bool ehAlfanumerico = EhDocumentoAlfanumerico(tomadorDoc);

            if (tomadorDoc.Length == 14)
            {
                // CNPJ: pode ser alfanumérico ou numérico
                if (ehAlfanumerico)
                {
                    // LÓGICA 1: CNPJ Alfanumérico - armazenar em TOMADOR_DOCUMENTO_ALFANUMERICO
                    docAlfanumerico = tomadorDoc;
                    // cnpj e cpf continuam NULL
                }
                else
                {
                    // LÓGICA 2: CNPJ Numérico - compatibilidade com código antigo
                    cnpj = ValidadorDocumento.ParaNumero(tomadorDoc);
                    docAlfanumerico = tomadorDoc;  // Armazenar também em docAlfanumerico para auditoria
                }
            }
            else if (tomadorDoc.Length == 11)
            {
                // LÓGICA 3: CPF (sempre numérico, não pode ter letras)
                if (ehAlfanumerico)
                {
                    // CPF com letras é inválido
                    throw new ArgumentException(
                        $"Documento inválido: '{tomadorDoc}' (tamanho: {tomadorDoc.Length}). " +
                        "CPF não pode conter letras.",
                        nameof(tomadorDoc));
                }
                cpf = ValidadorDocumento.ParaNumero(tomadorDoc);
                docAlfanumerico = tomadorDoc;  // Armazenar para auditoria
            }
            else
            {
                // LÓGICA 4: Tamanho inválido
                throw new ArgumentException(
                    $"Documento inválido: '{tomadorDoc}' (tamanho: {tomadorDoc.Length}). " +
                    "Esperado 11 (CPF) ou 14 (CNPJ).",
                    nameof(tomadorDoc));
            }

            // SQL INSERT: Verificar se coluna TOMADOR_DOCUMENTO_ALFANUMERICO existe
            string sql;
            if (ColunaAlfanumericoExiste(conn))
            {
                // Com migration aplicada: usar nova coluna
                sql = "INSERT INTO RPS (ID, PRESTADOR_ID, NUMERO, SERIE, TIPO, DT_EMISSAO, PROTOCOLO, " +
                     "TOMADOR_CNPJ, TOMADOR_CPF, TOMADOR_DOCUMENTO_ALFANUMERICO, TOMADOR_RAZAO) " +
                     "VALUES (:id, :prest, :num, :serie, :tipo, :dt, :prot, :cnpj, :cpf, :docalf, :razao)";

                OracleHelper.ExecuteNonQuery(conn, tx, sql,
                    OracleHelper.P("id", id),
                    OracleHelper.P("prest", prestadorId),
                    OracleHelper.P("num", numero),
                    OracleHelper.P("serie", serie),
                    OracleHelper.P("tipo", tipo),
                    OracleHelper.P("dt", OracleDbType.Date, dtEmissao),
                    OracleHelper.P("prot", protocolo),
                    OracleHelper.P("cnpj", OracleDbType.Int64, cnpj),
                    OracleHelper.P("cpf", OracleDbType.Int64, cpf),
                    OracleHelper.P("docalf", OracleDbType.Varchar2, docAlfanumerico),
                    OracleHelper.P("razao", tomadorRazao));
            }
            else
            {
                // Sem migration: usar schema original (compatibilidade)
                // Alfanuméricos serão armazenados como NULL em TOMADOR_CNPJ
                sql = "INSERT INTO RPS (ID, PRESTADOR_ID, NUMERO, SERIE, TIPO, DT_EMISSAO, PROTOCOLO, " +
                     "TOMADOR_CNPJ, TOMADOR_CPF, TOMADOR_RAZAO) " +
                     "VALUES (:id, :prest, :num, :serie, :tipo, :dt, :prot, :cnpj, :cpf, :razao)";

                OracleHelper.ExecuteNonQuery(conn, tx, sql,
                    OracleHelper.P("id", id),
                    OracleHelper.P("prest", prestadorId),
                    OracleHelper.P("num", numero),
                    OracleHelper.P("serie", serie),
                    OracleHelper.P("tipo", tipo),
                    OracleHelper.P("dt", OracleDbType.Date, dtEmissao),
                    OracleHelper.P("prot", protocolo),
                    OracleHelper.P("cnpj", OracleDbType.Int64, cnpj),
                    OracleHelper.P("cpf", OracleDbType.Int64, cpf),
                    OracleHelper.P("razao", tomadorRazao));
            }

            return id;
        }
    }
}
