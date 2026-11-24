using System;
using System.IO;
using System.Text;
using Oracle.ManagedDataAccess.Client;

namespace AplicarMigration
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
                Console.WriteLine("║           Aplicador de Migrations - Sistema NFS-e                     ║");
                Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
                Console.WriteLine();

                // Configuração da conexão
                string connectionString = "User Id=NFSE_OWNER;Password=NFSE_OWNER;Data Source=localhost:1521/FREEPDB1;Min Pool Size=2;Connection Timeout=30";

                // Verificar se foi passado arquivo como parâmetro
                string migrationFile = args.Length > 0 ? args[0] : 
                    "D:\\repos2\\desafio-dotnet-senior\\db\\migrations\\FEATURE-CNPJ-ALFANUMERICO-001-ALTER-SCHEMA.sql";

                // Validar arquivo
                if (!File.Exists(migrationFile))
                {
                    Console.WriteLine($"❌ ERRO: Arquivo de migration não encontrado: {migrationFile}");
                    Environment.Exit(1);
                }

                Console.WriteLine($"📄 Arquivo: {Path.GetFileName(migrationFile)}");
                Console.WriteLine();

                // Ler conteúdo do arquivo
                string sqlScript = File.ReadAllText(migrationFile, Encoding.UTF8);

                // Dividir por blocos (/)
                string[] commands = sqlScript.Split(new[] { "\n/" }, StringSplitOptions.RemoveEmptyEntries);

                using (OracleConnection conn = new OracleConnection(connectionString))
                {
                    Console.WriteLine("🔗 Conectando ao Oracle...");
                    conn.Open();
                    Console.WriteLine("✓ Conectado com sucesso!");
                    Console.WriteLine($"   Versão: {conn.ServerVersion}");
                    Console.WriteLine();

                    int successCount = 0;
                    int errorCount = 0;

                    Console.WriteLine("⏱️  Executando migration...");
                    Console.WriteLine();

                    foreach (string command in commands)
                    {
                        string trimmedCommand = command.Trim();

                        if (string.IsNullOrWhiteSpace(trimmedCommand))
                            continue;

                        // Remover SET commands que não são SQL
                        if (trimmedCommand.StartsWith("SET ", StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            using (OracleCommand cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = trimmedCommand;
                                cmd.ExecuteNonQuery();
                                successCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            Console.WriteLine($"⚠️  Erro ao executar comando:");
                            Console.WriteLine($"   {ex.Message}");
                            Console.WriteLine();
                        }
                    }

                    Console.WriteLine();
                    Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
                    if (errorCount == 0)
                    {
                        Console.WriteLine("║  ✓ MIGRATION APLICADA COM SUCESSO!                                   ║");
                    }
                    else
                    {
                        Console.WriteLine($"║  ⚠ MIGRATION COMPLETADA COM {errorCount} ERRO(S)                             ║");
                    }
                    Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
                    Console.WriteLine();
                    Console.WriteLine($"Resumo:");
                    Console.WriteLine($"  Comandos executados com sucesso: {successCount}");
                    Console.WriteLine($"  Erros: {errorCount}");
                    Console.WriteLine();

                    // Validar se migration foi aplicada
                    Console.WriteLine("🔍 Validando resultado...");
                    ValidarMigration(conn);
                }

                Console.WriteLine();
                Console.WriteLine("✓ Processo concluído!");
                Environment.Exit(errorCount > 0 ? 1 : 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("❌ ERRO FATAL:");
                Console.WriteLine($"   {ex.Message}");
                Console.WriteLine();
                Console.WriteLine(ex.StackTrace);
                Environment.Exit(2);
            }
        }

        static void ValidarMigration(OracleConnection conn)
        {
            try
            {
                // Verificar se coluna TIPO existe em CONTRIBUINTES
                string sqlCheckTipo = @"
                    SELECT COUNT(*) FROM user_tab_columns 
                    WHERE table_name = 'CONTRIBUINTES' AND column_name = 'TIPO'";

                using (OracleCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sqlCheckTipo;
                    int tipoExists = Convert.ToInt32(cmd.ExecuteScalar());

                    if (tipoExists > 0)
                    {
                        Console.WriteLine("   ✓ Coluna CONTRIBUINTES.TIPO criada");
                    }
                    else
                    {
                        Console.WriteLine("   ⚠ Coluna CONTRIBUINTES.TIPO NÃO encontrada");
                    }
                }

                // Verificar se coluna TOMADOR_DOCUMENTO_ALFANUMERICO existe em RPS
                string sqlCheckAlfanumerico = @"
                    SELECT COUNT(*) FROM user_tab_columns 
                    WHERE table_name = 'RPS' AND column_name = 'TOMADOR_DOCUMENTO_ALFANUMERICO'";

                using (OracleCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sqlCheckAlfanumerico;
                    int alfanumericoExists = Convert.ToInt32(cmd.ExecuteScalar());

                    if (alfanumericoExists > 0)
                    {
                        Console.WriteLine("   ✓ Coluna RPS.TOMADOR_DOCUMENTO_ALFANUMERICO criada");
                    }
                    else
                    {
                        Console.WriteLine("   ⚠ Coluna RPS.TOMADOR_DOCUMENTO_ALFANUMERICO NÃO encontrada");
                    }
                }

                // Verificar se índice foi criado
                string sqlCheckIndex = @"
                    SELECT COUNT(*) FROM user_indexes 
                    WHERE index_name = 'IX_NFSE_TOMADOR_ALFANUMERICO'";

                using (OracleCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sqlCheckIndex;
                    int indexExists = Convert.ToInt32(cmd.ExecuteScalar());

                    if (indexExists > 0)
                    {
                        Console.WriteLine("   ✓ Índice IX_NFSE_TOMADOR_ALFANUMERICO criado");
                    }
                    else
                    {
                        Console.WriteLine("   ⚠ Índice IX_NFSE_TOMADOR_ALFANUMERICO NÃO encontrado");
                    }
                }

                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ Erro ao validar: {ex.Message}");
            }
        }
    }
}
