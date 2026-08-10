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
    ///
    /// Uso (nomeado, 2026-08-10): é a forma que a API fala (LowCodeTransformationService) —
    ///   --globalFolder &lt;dir&gt; --package &lt;pkg&gt; --inputFile &lt;arq&gt; --outputFile &lt;arq&gt;
    ///   (--mapperId &lt;guid&gt; | --mapperName &lt;nome&gt;) [--fileName &lt;nome&gt;] [--correlationId &lt;id&gt;]
    ///   [--runnerLogFile &lt;arq&gt;] [--sysmiddleDir &lt;dir&gt;]
    /// Até então o runner só entendia a forma posicional, então a chamada da API caía nela e
    /// deslocava tudo: "--sysmiddleDir" virava globalFolder e o VALOR de --globalFolder virava o
    /// input (exit=4, "Input nao encontrado: ...\globalfolder"). Ver RunnerArgsParser.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var parse = RunnerArgsParser.Parse(args);
            if (!parse.Success)
            {
                // Antes do Configure: sem correlationId ainda, mas o formato de linha já é o mesmo.
                foreach (var mensagem in parse.Messages)
                    RunnerLog.Error("{0}", mensagem);

                Console.Error.Flush();
                return parse.ExitCode;
            }

            var cli = parse.Args;

            // Liga o log ao contexto ANTES do bootstrap: falha de licença/config também precisa sair
            // correlacionada, senão o log da API não fecha com o do runner.
            RunnerLog.Configure(cli.CorrelationId, cli.RunnerLogFile);

            int exitCode;
            try
            {
                // ── P2: Bootstrap ── replica Service1.OnStart. Os transportes (MQ/DB) tentam subir e FALHAM
                // sem VPN — é esperado; capturamos e seguimos, pois só precisamos do SetConfiguration.
                if (!Bootstrap(TimeSpan.FromSeconds(90)))
                {
                    RunnerLog.Fatal("Bootstrap nao populou ConnectorApplicationManager._configuration.");
                    exitCode = RunnerExitCodes.BootstrapFailed;
                }
                else if (cli.Mode == RunnerMode.Sweep)
                {
                    exitCode = Sweep(cli.GlobalFolder, cli.Package, cli.MapperGuid,
                        examplesFolder: cli.ExamplesFolder, outputFolder: cli.OutputFolder);
                }
                else
                {
                    exitCode = Run(cli);
                }
            }
            catch (Exception ex)
            {
                RunnerLog.Fatal("{0}", ex);
                exitCode = RunnerExitCodes.Fatal;
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
            RunnerLog.Info("[BOOT] Iniciando bootstrap (EDocsClientConnectorManager.Start)...");
            var manager = new EDocsClientConnectorManager();

            var bootThread = new Thread(() =>
            {
                try
                {
                    manager.Start();
                    RunnerLog.Info("[BOOT] manager.Start() retornou.");
                }
                catch (Exception ex)
                {
                    // Transportes (MQ/DB) inacessiveis sem VPN caem aqui — esperado.
                    RunnerLog.Warn("[BOOT-WARN] Start() lancou (esperado sem VPN): {0}", ex.Message);
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
                    RunnerLog.Info("[BOOT] Config carregada em {0:n1}s. ServerPackage='{1}'.",
                        sw.Elapsed.TotalSeconds, SafeServerPackage());
                    return true;
                }
                Thread.Sleep(200);
            }

            RunnerLog.Warn("[BOOT] Timeout ({0:n0}s) aguardando a config.", timeout.TotalSeconds);
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

        private static int Run(RunnerArgs cli)
        {
            var helper = GateLicenseAndGetHelper(cli.GlobalFolder);

            // ── LIST: imprime os mapeadores do package (descoberta) ──
            if (cli.Mode == RunnerMode.List)
            {
                RunnerLog.Info("[LIST] globalFolder={0} package='{1}'", cli.GlobalFolder, cli.Package);
                var mappers = helper.GetMappers(cli.Package, cli.GlobalFolder);
                RunnerLog.Info("Mapeadores encontrados: {0}", mappers != null ? mappers.Count : 0);
                if (mappers != null)
                    foreach (var kv in mappers)
                        Console.Out.WriteLine("{0}\t{1}", kv.Value.IdentifierGuid, kv.Value.Name);
                return RunnerExitCodes.Ok;
            }

            // ── Resolução de --mapperName ── a API aceita mapperId OU mapperName
            // (TransformationExecutionController), então o runner precisa fechar os dois lados do
            // contrato. Reaproveita o MESMO GetMappers do LIST — nada de caminho novo de descoberta.
            string mapperGuid = cli.MapperGuid;
            if (string.IsNullOrEmpty(mapperGuid))
            {
                mapperGuid = ResolverMapperPorNome(helper, cli.Package, cli.GlobalFolder, cli.MapperName);
                if (string.IsNullOrEmpty(mapperGuid))
                    return RunnerExitCodes.MapperNameUnresolved;
            }

            // ── EXEC: executa um mapeador sobre o input e grava o XML gabarito ──
            if (!File.Exists(cli.InputPath))
            {
                RunnerLog.Error("Input nao encontrado: {0}", cli.InputPath);
                return RunnerExitCodes.InputNotFound;
            }
            string document = File.ReadAllText(cli.InputPath);

            // ✅ --fileName é o nome LÓGICO do documento. A API grava a entrada num temporário
            // (in_<guid>.txt), então derivar de Path.GetFileName(inputPath) perdia o nome real do
            // arquivo do usuário — e esse nome pode influenciar a seleção de parser no Sysmiddle.
            // A regra (incluindo o fallback) vive em RunnerArgs porque lá ela é testável.
            string documentName = cli.ResolveDocumentName();

            RunnerLog.Info("[EXEC] globalFolder={0} mapper={1} input={2} fileName={3}",
                cli.GlobalFolder, mapperGuid, Path.GetFileName(cli.InputPath), documentName);
            string result = helper.ExecuteMappingDocumentById(mapperGuid, document, cli.GlobalFolder, documentName);

            File.WriteAllText(cli.OutputPath, result ?? string.Empty, new UTF8Encoding(false));
            int len = (result ?? string.Empty).Length;
            RunnerLog.Info("[OK] {0} chars -> {1}", len, cli.OutputPath);
            if (len == 0)
            {
                RunnerLog.Warn("[WARN] Mapeador retornou vazio (mapperGuid errado, licenca invalida ou parser sem match).");
                return RunnerExitCodes.EmptyResult;
            }
            return RunnerExitCodes.Ok;
        }

        /// <summary>
        /// Traduz --mapperName para o GUID do mapeador dentro do package. Devolve null (e loga o
        /// motivo) quando não há match — o chamador converte isso em
        /// <see cref="RunnerExitCodes.MapperNameUnresolved"/>, para o log da API distinguir
        /// "nome errado" de "mapeador rodou e voltou vazio".
        /// </summary>
        private static string ResolverMapperPorNome(MappersHelper helper, string package, string globalFolder, string mapperName)
        {
            var mappers = helper.GetMappers(package, globalFolder);
            if (mappers == null || mappers.Count == 0)
            {
                RunnerLog.Error("Nenhum mapeador no package '{0}' para resolver --mapperName '{1}'.", package, mapperName);
                return null;
            }

            foreach (var kv in mappers)
            {
                if (kv.Value != null && string.Equals(kv.Value.Name, mapperName, StringComparison.OrdinalIgnoreCase))
                {
                    RunnerLog.Info("[MAPPER] --mapperName '{0}' resolvido para {1}.", mapperName, kv.Value.IdentifierGuid);
                    return kv.Value.IdentifierGuid;
                }
            }

            RunnerLog.Error("--mapperName '{0}' nao encontrado entre os {1} mapeador(es) do package '{2}'.",
                mapperName, mappers.Count, package);
            return null;
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
                RunnerLog.Error("[SWEEP] Pasta de exemplos nao encontrada: {0}", examplesFolder);
                return RunnerExitCodes.InputNotFound;
            }

            Directory.CreateDirectory(outputFolder);
            var helper = GateLicenseAndGetHelper(globalFolder);

            // Ignora artefatos que não são documentos de entrada (ex.: layout_learned.json, ocultos, o próprio manifesto).
            var files = Directory.EnumerateFiles(examplesFolder, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            RunnerLog.Info("[SWEEP] globalFolder={0} mapper={1} examples={2} ({3} arquivo(s)) -> {4}",
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
                        RunnerLog.Warn("[SWEEP-WARN] {0}: mapeador retornou vazio.", relInput);
                        manifestLines.Add(string.Join("\t", seqName, relInput, "EMPTY", "0", seqName));
                    }
                    else
                    {
                        ok++;
                        RunnerLog.Info("[SWEEP-OK] {0} -> {1} ({2} chars)", relInput, seqName, len);
                        manifestLines.Add(string.Join("\t", seqName, relInput, "OK", len.ToString(CultureInfo.InvariantCulture), seqName));
                    }
                }
                catch (Exception ex)
                {
                    // Degrade gracioso: um arquivo ruim (encoding invalido, parser sem match, etc.) nao pode
                    // interromper a varredura dos demais 169 restantes.
                    fail++;
                    RunnerLog.Error("[SWEEP-FAIL] {0}: {1}", relInput, ex.Message);
                    manifestLines.Add(string.Join("\t", seqName, relInput, "FAIL", "0", string.Empty));
                }
            }

            File.WriteAllLines(manifestPath, manifestLines, new UTF8Encoding(false));
            RunnerLog.Info("[SWEEP] Concluido: {0} ok, {1} vazios, {2} falhas (de {3}). Manifesto: {4}",
                ok, empty, fail, files.Count, manifestPath);

            // Exit code != 0 somente se NADA saiu com sucesso (sinaliza problema sistemico, ex.: mapperGuid errado).
            return ok > 0 ? RunnerExitCodes.Ok : RunnerExitCodes.SweepAllFailed;
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
