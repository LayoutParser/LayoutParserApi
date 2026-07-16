using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using appConnector.Client.Core;              // ConnectorApplicationManager (config do host: _configuration)
using appConnector.Client.Core.Controller;   // EDocsClientConnectorManager (o manager do Service1.OnStart)
using appConnector.Client.Core.Util;         // MappersHelper (executa o mapeador)

namespace LayoutParserLowCodeRunner
{
    /// <summary>
    /// CLI que replica o bootstrap do host FiatMQ (Service1.OnStart) para destravar o SDK Sysmiddle
    /// e então executa um mapeador (gera o XML gabarito) ou LISTa os mapeadores do package.
    ///
    /// Descoberta (descompilação da Bin da instância):
    ///  - Service1.OnStart faz apenas: new EDocsClientConnectorManager().Start();
    ///  - Start() -> LoadConfigurationXml() -> ConnectorApplicationManager.Instance.SetConfiguration(connector).
    ///    É ESSA chamada que popula ConnectorApplicationManager._configuration. Sem ela, GetServerPackage()
    ///    (usado por ExecuteMappingDocumentById) estoura NullReference e o MappersHelper entra em loop infinito.
    ///  - A licença NÃO é machine-bound: LicenseController lê o global.config (LicenseCode com checksum + data de
    ///    expiração embutida) e valida offline. Os projetos/mapeadores carregam de arquivo local (DbProviderType=File,
    ///    ConnectionString=exportContext.data) — não do SQL Server do cliente. Logo, roda sem VPN.
    ///
    /// Uso (single-shot): LayoutParserLowCodeRunner &lt;globalFolder&gt; &lt;package&gt; &lt;mapperGuid|LIST&gt; &lt;input&gt; &lt;output&gt;
    /// Uso (lote/A1):      LayoutParserLowCodeRunner SWEEP &lt;globalFolder&gt; &lt;package&gt; &lt;mapperGuid&gt; &lt;pastaExamples&gt; &lt;pastaSaida&gt;
    ///   - pastaExamples: pasta com os arquivos-exemplo (ex.: Examples/LAY_CNHI_..., recursivo, .json ignorado).
    ///   - pastaSaida: gravada dentro/relativa a .claude/tmp/gabaritos/ (gitignored) — cria se não existir.
    /// (rode DE DENTRO da Bin da instância; globalFolder = pasta com o global.config de paths locais).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            bool isSweep = args.Length > 0 && string.Equals(args[0], "SWEEP", StringComparison.OrdinalIgnoreCase);

            if (isSweep && args.Length < 6)
            {
                Console.Error.WriteLine("Uso: LayoutParserLowCodeRunner SWEEP <globalFolder> <package> <mapperGuid> <pastaExamples> <pastaSaida>");
                return 2;
            }
            if (!isSweep && args.Length < 5)
            {
                Console.Error.WriteLine("Uso: LayoutParserLowCodeRunner <globalFolder> <package> <mapperGuid|LIST> <input> <output>");
                Console.Error.WriteLine("Uso (lote): LayoutParserLowCodeRunner SWEEP <globalFolder> <package> <mapperGuid> <pastaExamples> <pastaSaida>");
                return 2;
            }

            string globalFolder, package, mapperGuid, arg4, arg5;
            if (isSweep)
            {
                globalFolder = args[1]; package = args[2]; mapperGuid = args[3]; arg4 = args[4]; arg5 = args[5];
            }
            else
            {
                globalFolder = args[0]; package = args[1]; mapperGuid = args[2]; arg4 = args[3]; arg5 = args[4];
            }

            int exitCode;
            try
            {
                // ── P2: Bootstrap ── replica Service1.OnStart. Os transportes (MQ/DB) tentam subir e FALHAM
                // sem VPN — é esperado; capturamos e seguimos, pois só precisamos do SetConfiguration.
                if (!Bootstrap(TimeSpan.FromSeconds(90)))
                {
                    Console.Error.WriteLine("[FATAL] Bootstrap nao populou ConnectorApplicationManager._configuration.");
                    exitCode = 3;
                }
                else if (isSweep)
                {
                    exitCode = Sweep(globalFolder, package, mapperGuid, examplesFolder: arg4, outputFolder: arg5);
                }
                else
                {
                    exitCode = Run(globalFolder, package, mapperGuid, inputPath: arg4, outputPath: arg5);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FATAL] " + ex);
                exitCode = 1;
            }

            // Força a saída: o bootstrap deixou threads de transporte/falha vivas em background (fire-and-forget).
            Console.Out.Flush();
            Console.Error.Flush();
            Environment.Exit(exitCode);
            return exitCode; // inalcançável
        }

        /// <summary>
        /// Replica o OnStart do host: instancia o EDocsClientConnectorManager e chama Start() numa thread de
        /// fundo (as threads de transporte podem bloquear/loopar sem VPN). Aguarda até que o SetConfiguration
        /// tenha rodado (config != null) ou o timeout. Degrade gracioso: qualquer erro do Start é logado, não fatal.
        /// </summary>
        private static bool Bootstrap(TimeSpan timeout)
        {
            Console.Error.WriteLine("[BOOT] Iniciando bootstrap (EDocsClientConnectorManager.Start)...");
            var manager = new EDocsClientConnectorManager();

            var bootThread = new Thread(() =>
            {
                try
                {
                    manager.Start();
                    Console.Error.WriteLine("[BOOT] manager.Start() retornou.");
                }
                catch (Exception ex)
                {
                    // Transportes (MQ/DB) inacessiveis sem VPN caem aqui — esperado.
                    Console.Error.WriteLine("[BOOT-WARN] Start() lancou (esperado sem VPN): {0}", ex.Message);
                }
            })
            {
                IsBackground = true,
                Name = "LowCodeRunner-Bootstrap"
            };
            bootThread.Start();

            // O SetConfiguration roda no INICIO do Start() (LoadConfigurationXml), antes dos ActionManagers.
            // Basta esperar _configuration ser populado — não precisamos que o Start() inteiro conclua.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (ConnectorApplicationManager.Instance.GetConfiguration() != null)
                {
                    Console.Error.WriteLine("[BOOT] Config carregada em {0:n1}s. ServerPackage='{1}'.",
                        sw.Elapsed.TotalSeconds, SafeServerPackage());
                    return true;
                }
                Thread.Sleep(200);
            }

            Console.Error.WriteLine("[BOOT] Timeout ({0:n0}s) aguardando a config.", timeout.TotalSeconds);
            return ConnectorApplicationManager.Instance.GetConfiguration() != null;
        }

        private static string SafeServerPackage()
        {
            try { return ConnectorApplicationManager.Instance.GetServerPackage(); }
            catch { return "(indisponivel)"; }
        }

        /// <summary>
        /// Registra o LicenseController e aponta o global.config — pré-requisito comum ao modo
        /// single-shot (EXEC/LIST) e ao SWEEP. Idempotente (CreateType pode ser chamado de novo sem efeito colateral).
        /// </summary>
        private static MappersHelper GateLicenseAndGetHelper(string globalFolder)
        {
            // ── Gate de licença do APIManager (SEPARADO do ConnectorApplicationManager) ──
            // Descoberta por decompilação (SysMiddle.Base.InstanceFactory + SysMiddle.API.APIManager):
            //  1) O ctor do APIManager pega o ILicenseController de InstanceFactory.GetInstance<ILicenseController>();
            //     se a InstanceFactory NÃO tem o mapeamento interface→concreto, GetInstance retorna null (IsInterface)
            //     e o ctor lança "Controle de licença ... não encontrado" → MappersHelper em retry infinito.
            //  2) Neste ambiente o Initialize() da InstanceFactory NÃO escaneou SysMiddle.ConnectUs.Core.dll,
            //     então ILicenseController ficou sem concreto. Registramos o mapeamento EXPLICITAMENTE.
            //  3) Depois, SETAR GlobalConfigurationFileName instancia o LicenseController(configLocation) com o
            //     global.config (que tem o LicenseCode) → licença validada offline.
            SysMiddle.Base.InstanceFactory.Instance.CreateType(
                typeof(SysMiddle.Base.Interface.ILicenseController),
                typeof(SysMiddle.ConnectUs.Core.Helper.General.LicenseController));
            SysMiddle.API.APIManager.GlobalConfigurationFileName = Path.Combine(globalFolder, "global.config");

            return MappersHelper.Instance;
        }

        private static int Run(string globalFolder, string package, string mapperGuid, string inputPath, string outputPath)
        {
            var helper = GateLicenseAndGetHelper(globalFolder);

            // ── LIST: imprime os mapeadores do package (descoberta) ──
            if (string.Equals(mapperGuid, "LIST", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("[LIST] globalFolder={0} package='{1}'", globalFolder, package);
                var mappers = helper.GetMappers(package, globalFolder);
                Console.Error.WriteLine("Mapeadores encontrados: {0}", mappers != null ? mappers.Count : 0);
                if (mappers != null)
                    foreach (var kv in mappers)
                        Console.Out.WriteLine("{0}\t{1}", kv.Value.IdentifierGuid, kv.Value.Name);
                return 0;
            }

            // ── EXEC: executa um mapeador sobre o input e grava o XML gabarito ──
            if (!File.Exists(inputPath)) { Console.Error.WriteLine("Input nao encontrado: " + inputPath); return 4; }
            string document = File.ReadAllText(inputPath);

            Console.Error.WriteLine("[EXEC] globalFolder={0} mapper={1} input={2}", globalFolder, mapperGuid, Path.GetFileName(inputPath));
            string result = helper.ExecuteMappingDocumentById(mapperGuid, document, globalFolder, Path.GetFileName(inputPath));

            File.WriteAllText(outputPath, result ?? string.Empty, new UTF8Encoding(false));
            int len = (result ?? string.Empty).Length;
            Console.Error.WriteLine("[OK] {0} chars -> {1}", len, outputPath);
            if (len == 0)
            {
                Console.Error.WriteLine("[WARN] Mapeador retornou vazio (mapperGuid errado, licenca invalida ou parser sem match).");
                return 5;
            }
            return 0;
        }

        /// <summary>
        /// Modo SWEEP (A1): varre recursivamente <paramref name="examplesFolder"/> (ex.: Examples/LAY_CNHI_...),
        /// executa o mapeador informado para CADA arquivo-exemplo encontrado e grava o par input→XML resultante
        /// em <paramref name="outputFolder"/> (numerado 0001.xml, 0002.xml, ... — convenção já usada em
        /// .claude/tmp/gabaritos/fiat-sweep). Falha em UM arquivo não aborta a varredura: logamos e seguimos
        /// (degradação graciosa — princípio central do projeto, ver .claude/rules/dotnet-standards.md).
        /// </summary>
        private static int Sweep(string globalFolder, string package, string mapperGuid, string examplesFolder, string outputFolder)
        {
            if (!Directory.Exists(examplesFolder))
            {
                Console.Error.WriteLine("[SWEEP] Pasta de exemplos nao encontrada: " + examplesFolder);
                return 4;
            }

            Directory.CreateDirectory(outputFolder);
            var helper = GateLicenseAndGetHelper(globalFolder);

            // Ignora artefatos que não são documentos de entrada (ex.: layout_learned.json, ocultos, o próprio manifesto).
            var files = Directory.EnumerateFiles(examplesFolder, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.Error.WriteLine("[SWEEP] globalFolder={0} mapper={1} examples={2} ({3} arquivo(s)) -> {4}",
                globalFolder, mapperGuid, examplesFolder, files.Count, outputFolder);

            var manifestPath = Path.Combine(outputFolder, "_manifest.tsv");
            var manifestLines = new List<string> { "seq\tinput\tstatus\tout_chars\toutput" };

            int ok = 0, empty = 0, fail = 0, seq = 0;
            foreach (var inputPath in files)
            {
                seq++;
                string seqName = seq.ToString("D4", CultureInfo.InvariantCulture) + ".xml";
                string outputPath = Path.Combine(outputFolder, seqName);
                string relInput = MakeRelativePath(examplesFolder, inputPath);

                try
                {
                    string document = File.ReadAllText(inputPath);
                    string result = helper.ExecuteMappingDocumentById(mapperGuid, document, globalFolder, Path.GetFileName(inputPath));
                    string safeResult = result ?? string.Empty;

                    File.WriteAllText(outputPath, safeResult, new UTF8Encoding(false));
                    int len = safeResult.Length;

                    if (len == 0)
                    {
                        empty++;
                        Console.Error.WriteLine("[SWEEP-WARN] {0}: mapeador retornou vazio.", relInput);
                        manifestLines.Add(string.Join("\t", seqName, relInput, "EMPTY", "0", seqName));
                    }
                    else
                    {
                        ok++;
                        Console.Error.WriteLine("[SWEEP-OK] {0} -> {1} ({2} chars)", relInput, seqName, len);
                        manifestLines.Add(string.Join("\t", seqName, relInput, "OK", len.ToString(CultureInfo.InvariantCulture), seqName));
                    }
                }
                catch (Exception ex)
                {
                    // Degrade gracioso: um arquivo ruim (encoding invalido, parser sem match, etc.) nao pode
                    // interromper a varredura dos demais 169 restantes.
                    fail++;
                    Console.Error.WriteLine("[SWEEP-FAIL] {0}: {1}", relInput, ex.Message);
                    manifestLines.Add(string.Join("\t", seqName, relInput, "FAIL", "0", string.Empty));
                }
            }

            File.WriteAllLines(manifestPath, manifestLines, new UTF8Encoding(false));
            Console.Error.WriteLine("[SWEEP] Concluido: {0} ok, {1} vazios, {2} falhas (de {3}). Manifesto: {4}",
                ok, empty, fail, files.Count, manifestPath);

            // Exit code != 0 somente se NADA saiu com sucesso (sinaliza problema sistemico, ex.: mapperGuid errado).
            return ok > 0 ? 0 : 6;
        }

        /// <summary>
        /// net481 nao tem Path.GetRelativePath (introduzido no .NET Core 2.0) — versao minima so para exibir
        /// o caminho do input no log/manifesto de forma legivel.
        /// </summary>
        private static string MakeRelativePath(string baseFolder, string fullPath)
        {
            var baseUri = new Uri(Path.GetFullPath(baseFolder) + Path.DirectorySeparatorChar);
            var fullUri = new Uri(Path.GetFullPath(fullPath));
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
