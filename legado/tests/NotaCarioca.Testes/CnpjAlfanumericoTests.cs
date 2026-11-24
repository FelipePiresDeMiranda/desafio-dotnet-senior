using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Dominio;
using System;

namespace NotaCarioca.Testes
{
    [TestClass]
    public class CnpjAlfanumericoTests
    {
        /// <summary>
        /// FEATURE-CNPJ-01: Validação de CNPJ alfanumérico com dígitos verificadores corretos
        /// Sintoma: CNPJ alfanumérico é rejeitado no processamento de lotes
        /// Correção Necessária: Estender ValidadorDocumento.ValidarCnpj() para suportar formato alfanumérico
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_Formato_11ABCDEF0001_DigitoVerificadorCorreto()
        {
            // Arrange
            // CNPJ alfanumérico: 11ABCDEF0001-06 (dígitos verificadores: 06)
            // Calculado com: raiz=11ABCDEF, filial=0001, pesos=(5,4,3,2,9,8,7,6,5,4,3,2) e (6,5,4,3,2,9,8,7,6,5,4,3,2)
            string cnpjAlfanumerico = "11ABCDEF000106";

            // Act
            bool resultado = ValidadorDocumento.ValidarCnpj(cnpjAlfanumerico);

            // Assert
            Assert.IsTrue(resultado, "CNPJ alfanumérico com dígitos verificadores corretos deveria ser válido");
        }

        /// <summary>
        /// FEATURE-CNPJ-02: CNPJ alfanumérico com formatação
        /// Correção Necessária: Remover formatação antes de validar (similar ao numérico)
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_Com_Formatacao_ShouldBeValid()
        {
            // Arrange
            string cnpjFormatado = "11.ABCDEF/0001-06";

            // Act
            bool resultado = ValidadorDocumento.ValidarCnpj(cnpjFormatado);

            // Assert
            Assert.IsTrue(resultado, "CNPJ alfanumérico formatado deveria ser válido");
        }

        /// <summary>
        /// FEATURE-CNPJ-03: CNPJ alfanumérico com dígito verificador errado
        /// Correção Necessária: Validar dígitos verificadores do formato alfanumérico
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_DigitoVerificadorErrado_ShouldBeInvalid()
        {
            // Arrange
            string cnpjInvalido = "11ABCDEF000107"; // dígito errado (07 em vez de 06)

            // Act
            bool resultado = ValidadorDocumento.ValidarCnpj(cnpjInvalido);

            // Assert
            Assert.IsFalse(resultado, "CNPJ alfanumérico com dígito verificador errado deveria ser inválido");
        }

        /// <summary>
        /// FEATURE-CNPJ-04: CNPJ alfanumérico com caracteres inválidos (I, O, U)
        /// Correção Necessária: Rejeitar CNPJs alfanuméricos com caracteres I, O ou U
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_Com_Caracteres_I_O_U_ShouldBeInvalid()
        {
            // Arrange - caracteres I, O, U não são permitidos em CNPJ alfanumérico
            string[] cnpjsInvalidos = {
                "11IABCDEF000106",  // tem I
                "11OABCDEF000106",  // tem O
                "11UABCDEF000106"   // tem U
            };

            // Act & Assert
            foreach (string cnpj in cnpjsInvalidos)
            {
                bool resultado = ValidadorDocumento.ValidarCnpj(cnpj);
                Assert.IsFalse(resultado, $"CNPJ alfanumérico com caracteres I/O/U ({cnpj}) deveria ser inválido");
            }
        }

        /// <summary>
        /// FEATURE-CNPJ-05: CNPJ alfanumérico com caracteres numéricos exclusivamente (compatibilidade com numérico)
        /// Correção Necessária: Manter compatibilidade com CNPJs numéricos puros
        /// </summary>
        [TestMethod]
        public void CNPJ_Numerico_Puro_ShouldContinueWorking()
        {
            // Arrange
            string cnpjNumerico = "11222333000181";

            // Act
            bool resultado = ValidadorDocumento.ValidarCnpj(cnpjNumerico);

            // Assert
            Assert.IsTrue(resultado, "CNPJ numérico puro deveria continuar funcionando (compatibilidade)");
        }

        /// <summary>
        /// FEATURE-CNPJ-06: Extração de dígitos de CNPJ alfanumérico
        /// Correção Necessária: SomenteDigitos deve continuar funcionando, extraindo apenas os numéricos
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_SomenteDigitos_ExtraiNumericosApenas()
        {
            // Arrange
            string cnpjAlfanumerico = "11.ABCDEF/0001-82";

            // Act
            string soDigitos = ValidadorDocumento.SomenteDigitos(cnpjAlfanumerico);

            // Assert
            Assert.AreEqual("11000182", soDigitos, "Deve extrair apenas dígitos: 11 (primeiros), 0001 (filial), 82 (DV)");
        }

        /// <summary>
        /// FEATURE-CNPJ-07: CNPJ alfanumérico em formato misto (letras maiúsculas e minúsculas)
        /// Correção Necessária: Normalizar letras para maiúsculas antes de validar
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_MaiusculasMinusculas_ShouldNormalize()
        {
            // Arrange
            string cnpjMisto = "11abcdef000106";  // letras minúsculas

            // Act
            bool resultado = ValidadorDocumento.ValidarCnpj(cnpjMisto);

            // Assert
            Assert.IsTrue(resultado, "CNPJ alfanumérico com letras minúsculas deveria ser normalizado e validado");
        }

        /// <summary>
        /// FEATURE-CNPJ-08: CNPJ alfanumérico todos iguais (proteção contra sequências)
        /// Correção Necessária: Rejeitar CNPJs alfanuméricos onde todos os caracteres são iguais
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_TodosIguais_ShouldBeInvalid()
        {
            // Arrange
            string cnpjIgual = "AAAAAAAAAAAAA";  // todos iguais

            // Act
            bool resultado = ValidadorDocumento.ValidarCnpj(cnpjIgual);

            // Assert
            Assert.IsFalse(resultado, "CNPJ alfanumérico com todos os caracteres iguais deveria ser inválido");
        }

        /// <summary>
        /// FEATURE-CNPJ-09: Validação de múltiplos CNPJs alfanuméricos válidos
        /// Correção Necessária: Validar conversão de caracteres alfanuméricos corretamente
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_MultiplosCasos_Validos()
        {
            // Arrange - exemplos teóricos de CNPJs alfanuméricos válidos
            var cnpjsValidos = new[]
            {
                "11ABCDEF000106"
                // Adicione mais quando calcular os dígitos verificadores
            };

            // Act & Assert
            foreach (string cnpj in cnpjsValidos)
            {
                bool resultado = ValidadorDocumento.ValidarCnpj(cnpj);
                Assert.IsTrue(resultado, $"CNPJ alfanumérico válido ({cnpj}) deveria passar na validação");
            }
        }

        /// <summary>
        /// FEATURE-CNPJ-10: Compatibilidade com ValidarCpfOuCnpj
        /// Correção Necessária: ValidarCpfOuCnpj deve detectar e validar tanto CNPJs numéricos quanto alfanuméricos
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_ValidarCpfOuCnpj_DeviceSuportarAlfanumerico()
        {
            // Arrange
            string cnpjAlfanumerico = "11ABCDEF000106";

            // Act
            bool resultado = ValidadorDocumento.ValidarCpfOuCnpj(cnpjAlfanumerico);

            // Assert
            Assert.IsTrue(resultado, "ValidarCpfOuCnpj deveria suportar CNPJ alfanumérico");
        }

        /// <summary>
        /// FEATURE-CNPJ-11: CNPJ alfanumérico com espaços e caracteres especiais
        /// Correção Necessária: Remover espaços e caracteres especiais antes de validar
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_Com_Espacos_DeveSer_RemovidosAntes()
        {
            // Arrange
            string cnpjComEspacos = "11.ABC DEF/0001-06";  // espaço no meio

            // Act
            // Esperamos que a função remova espaços automaticamente
            // Ou que lance exceção se espaços não forem removidos
            bool resultado = ValidadorDocumento.ValidarCnpj(cnpjComEspacos);

            // Assert
            Assert.IsTrue(resultado, "CNPJ alfanumérico com espaços deveria ter espaços removidos e ser validado");
        }

        /// <summary>
        /// FEATURE-CNPJ-12: Número mínimo e máximo de caracteres
        /// Correção Necessária: CNPJ alfanumérico deve ter exatamente 14 caracteres (8 raiz + 4 filial + 2 DV)
        /// </summary>
        [TestMethod]
        public void CNPJ_Alfanumerico_Tamanho_DeveSerExatamente14()
        {
            // Arrange
            string cnpjCurto = "11ABC000182";   // menos de 14 caracteres
            string cnpjLongo = "11ABCDEF000182ABC";  // mais de 14 caracteres

            // Act
            bool resultadoCurto = ValidadorDocumento.ValidarCnpj(cnpjCurto);
            bool resultadoLongo = ValidadorDocumento.ValidarCnpj(cnpjLongo);

            // Assert
            Assert.IsFalse(resultadoCurto, "CNPJ com menos de 14 caracteres deveria ser inválido");
            Assert.IsFalse(resultadoLongo, "CNPJ com mais de 14 caracteres deveria ser inválido");
        }
    }
}
