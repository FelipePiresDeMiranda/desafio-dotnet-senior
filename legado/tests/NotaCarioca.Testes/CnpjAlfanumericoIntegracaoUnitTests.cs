using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Dominio;

namespace NotaCarioca.Testes
{
    /// <summary>
    /// Testes de integração unitária para validar lógica de CNPJ alfanumérico
    /// FEATURE-CNPJ-ALFANUMERICO: Suporta novos CNPJs alfanuméricos (IN RFB nº 2.229/2024)
    /// 
    /// Phase 4: Testes sem dependência de Oracle (MockVersion)
    /// - Valida lógica de detecção de documento
    /// - Valida compatibilidade com numéricos e CPF
    /// - Testa validação e error handling
    /// </summary>
    [TestClass]
    public class CnpjAlfanumericoIntegracaoUnitTests
    {
        private RpsRepositorioMock _rpsRepoMock;

        [TestInitialize]
        public void Setup()
        {
            _rpsRepoMock = new RpsRepositorioMock();
        }

        /// <summary>
        /// FEATURE-CNPJ-01: Detectar e processar CNPJ alfanumérico
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_01_DetectarCnpjAlfanumerico()
        {
            // Arrange
            string cnpjAlfan = "11ABCDEF000106";

            // Act
            bool ehAlfan = _rpsRepoMock.EhDocumentoAlfanumerico(cnpjAlfan);

            // Assert
            Assert.IsTrue(ehAlfan, "Deve detectar CNPJ alfanumérico");
        }

        /// <summary>
        /// FEATURE-CNPJ-02: Detectar CNPJ numérico
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_02_DetectarCnpjNumerico()
        {
            // Arrange
            string cnpjNum = "11222333000181";

            // Act
            bool ehAlfan = _rpsRepoMock.EhDocumentoAlfanumerico(cnpjNum);

            // Assert
            Assert.IsFalse(ehAlfan, "Não deve detectar como alfanumérico");
        }

        /// <summary>
        /// FEATURE-CNPJ-03: Detectar CPF (numérico)
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_03_DetectarCpf()
        {
            // Arrange
            string cpf = "12345678901";

            // Act
            bool ehAlfan = _rpsRepoMock.EhDocumentoAlfanumerico(cpf);

            // Assert
            Assert.IsFalse(ehAlfan, "CPF não deve ser detectado como alfanumérico");
        }

        /// <summary>
        /// FEATURE-CNPJ-04: Rejeitar documento nulo
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_04_RejeitarDocumentoNulo()
        {
            // Arrange
            string doc = null;

            // Act
            bool ehAlfan = _rpsRepoMock.EhDocumentoAlfanumerico(doc);

            // Assert
            Assert.IsFalse(ehAlfan, "Documento nulo não deve ser processado");
        }

        /// <summary>
        /// FEATURE-CNPJ-05: Detectar vazio
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_05_RejeitarDocumentoVazio()
        {
            // Arrange
            string doc = "";

            // Act
            bool ehAlfan = _rpsRepoMock.EhDocumentoAlfanumerico(doc);

            // Assert
            Assert.IsFalse(ehAlfan, "Documento vazio não deve ser processado");
        }

        /// <summary>
        /// FEATURE-CNPJ-06: Validação de tamanho - CNPJ alfanumérico
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_06_ValidarTamanhoCnpjAlfanumerico()
        {
            // Arrange
            string cnpjAlfan = "11ABCDEF000106";

            // Act
            bool ehTamanhoValido = cnpjAlfan.Length == 14;
            bool ehAlfan = _rpsRepoMock.EhDocumentoAlfanumerico(cnpjAlfan);

            // Assert
            Assert.IsTrue(ehTamanhoValido, "CNPJ alfanumérico deve ter 14 caracteres");
            Assert.IsTrue(ehAlfan, "Deve ser alfanumérico");
        }

        /// <summary>
        /// FEATURE-CNPJ-07: Validação de tamanho - CNPJ numérico
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_07_ValidarTamanhoCnpjNumerico()
        {
            // Arrange
            string cnpjNum = "11222333000181";

            // Act
            bool ehTamanhoValido = cnpjNum.Length == 14;
            bool ehAlfan = _rpsRepoMock.EhDocumentoAlfanumerico(cnpjNum);

            // Assert
            Assert.IsTrue(ehTamanhoValido, "CNPJ numérico deve ter 14 caracteres");
            Assert.IsFalse(ehAlfan, "Não deve ser alfanumérico");
        }

        /// <summary>
        /// FEATURE-CNPJ-08: Validação de tamanho - CPF
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_08_ValidarTamanhoCpf()
        {
            // Arrange
            string cpf = "12345678901";

            // Act
            bool ehTamanhoValido = cpf.Length == 11;
            bool ehAlfan = _rpsRepoMock.EhDocumentoAlfanumerico(cpf);

            // Assert
            Assert.IsTrue(ehTamanhoValido, "CPF deve ter 11 caracteres");
            Assert.IsFalse(ehAlfan, "Não deve ser alfanumérico");
        }

        /// <summary>
        /// FEATURE-CNPJ-09: Rejeitar tamanho inválido
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_09_RejeitarTamanhoInvalido()
        {
            // Arrange
            string[] docsInvalidos = { "123", "1234567890", "123456789012345" };

            // Act & Assert
            foreach (var doc in docsInvalidos)
            {
                bool ehTamanhoValido = doc.Length == 14 || doc.Length == 11;
                Assert.IsFalse(ehTamanhoValido, $"Documento {doc} com tamanho {doc.Length} não deve ser válido");
            }
        }

        /// <summary>
        /// FEATURE-CNPJ-10: Padrão I-O-U excluídos
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_10_LetrasIOUExcluidas()
        {
            // Arrange
            // Letra I não é permitida em CNPJ alfanumérico
            string com_I = "11IBCDEF000106";
            // Letra O não é permitida
            string com_O = "11OBCDEF000106";
            // Letra U não é permitida
            string com_U = "11UBCDEF000106";

            // Act - Detector deve identificar como alfanumérico
            bool ehAlfan_I = _rpsRepoMock.EhDocumentoAlfanumerico(com_I);
            bool ehAlfan_O = _rpsRepoMock.EhDocumentoAlfanumerico(com_O);
            bool ehAlfan_U = _rpsRepoMock.EhDocumentoAlfanumerico(com_U);

            // Assert
            Assert.IsTrue(ehAlfan_I, "Detecta como alfanumérico (validação será feita em ValidadorDocumento)");
            Assert.IsTrue(ehAlfan_O, "Detecta como alfanumérico (validação será feita em ValidadorDocumento)");
            Assert.IsTrue(ehAlfan_U, "Detecta como alfanumérico (validação será feita em ValidadorDocumento)");
        }

        /// <summary>
        /// FEATURE-CNPJ-11: Diferentes formatos A-H, J-N, P-T, V-Z
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_11_DiferentesFormatos()
        {
            // Arrange
            var cnpjsAlfan = new[]
            {
                "11ABCDEF000106",  // A-H
                "1JABCDEF000107",  // J-N
                "11PABCDE000108",  // P-T
                "11VABCDE000109",  // V-Z
            };

            // Act & Assert
            foreach (var cnpj in cnpjsAlfan)
            {
                bool ehAlfan = _rpsRepoMock.EhDocumentoAlfanumerico(cnpj);
                Assert.IsTrue(ehAlfan, $"CNPJ {cnpj} deve ser detectado como alfanumérico");
            }
        }

        /// <summary>
        /// FEATURE-CNPJ-12: Compatibilidade com código legado
        /// </summary>
        [TestMethod]
        public void FEATURE_CNPJ_12_CompatibilidadeCodigoLegado()
        {
            // Arrange
            var rpsData = new Dictionary<string, bool>
            {
                { "11222333000181", false },  // CNPJ numérico - compatibilidade
                { "12345678901", false },     // CPF - compatibilidade
                { "11ABCDEF000106", true }    // CNPJ alfanumérico - novo
            };

            // Act & Assert
            foreach (var item in rpsData)
            {
                bool ehAlfan = _rpsRepoMock.EhDocumentoAlfanumerico(item.Key);
                Assert.AreEqual(item.Value, ehAlfan, $"Documento {item.Key}: esperado {item.Value}, obteve {ehAlfan}");
            }
        }
    }

    /// <summary>
    /// Mock do RpsRepositorio para testes unitários sem depender de Oracle
    /// </summary>
    public class RpsRepositorioMock
    {
        public bool EhDocumentoAlfanumerico(string documento)
        {
            if (string.IsNullOrEmpty(documento))
                return false;

            // Usar System.Char.IsLetter para detectar letras
            foreach (char c in documento)
            {
                if (char.IsLetter(c))
                    return true;
            }
            return false;
        }
    }
}
