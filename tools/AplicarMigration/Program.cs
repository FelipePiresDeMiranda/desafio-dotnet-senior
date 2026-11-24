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
                string connectionString = "User Id=NFSE_OWNER;Password=nfse_dev_123;Data Source=localhost:1521/FREEPDB1;Min Pool Size=2;Connection Timeout=30";

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

                // Dividir por "/" (separador de comando Oracle)
                string[] blocks = sqlScript.Split(new[] { "\n/" }, StringSplitOptions.RemoveEmptyEntries);

                // Filtrar e limpar os blocos
                var commands = new System.Collections.Generic.List<string>();
                foreach (string block in blocks)
                {
                    var lines = block.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                    var filteredLines = new System.Collections.Generic.List<string>();

                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed))
                            continue;
                        if (trimmed.StartsWith("--"))
                            continue;
                        if (trimmed.StartsWith("SET "))
                            continue;
                        if (trimmed.StartsWith("PROMPT"))
                            continue;
                        filteredLines.Add(line);
                    }

                    string cleanedBlock = string.Join("\n", filteredLines).Trim();
                    if (!string.IsNullOrWhiteSpace(cleanedBlock))
                    {
                        commands.Add(cleanedBlock);
                    }
                }

                int successCount = 0;
                int errorCount = 0;

                using (OracleConnection conn = new OracleConnection(connectionString))
                {
                    Console.WriteLine("🔗 Conectando ao Oracle...");
                    conn.Open();
                    Console.WriteLine("✓ Conectado com sucesso!");
                    Console.WriteLine($"   Versão: {conn.ServerVersion}");
                    Console.WriteLine();

                    Console.WriteLine($"⏱️  Executando {commands.Count} bloco(s) de migration...");
                    Console.WriteLine();

                    for (int i = 0; i < commands.Count; i++)
                    {
                        string command = commands[i];

                        try
                        {
                            using (OracleCommand cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = command;
                                cmd.CommandTimeout = 300; // 5 minutos timeout
                                Console.Write($"   [{i + 1}/{commands.Count}] Executando bloco {i + 1}...");
                                cmd.ExecuteNonQuery();
                                Console.WriteLine(" ✓");
                                successCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            Console.WriteLine($" ❌");
                            Console.WriteLine($"       Erro: {ex.Message.Split('\n')[0]}");
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
                    Console.WriteLine($"  Blocos executados com sucesso: {successCount}");
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
                    WHERE index_name = 'IX_RPS_TOMADOR_ALFANUMERICO'";

                using (OracleCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sqlCheckIndex;
                    int indexExists = Convert.ToInt32(cmd.ExecuteScalar());

                    if (indexExists > 0)
                    {
                        Console.WriteLine("   ✓ Índice IX_RPS_TOMADOR_ALFANUMERICO criado");
                    }
                    else
                    {
                        Console.WriteLine("   ⚠ Índice IX_RPS_TOMADOR_ALFANUMERICO NÃO encontrado");
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
