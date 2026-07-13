using System;
using System.IO;
using System.Text;
using appConnector.Client.Core.Util;   // MappersHelper (init completa do host: ConnectorApplicationManager + licença)

namespace LayoutParserLowCodeRunner
{
    /// <summary>
    /// CLI: usa o MappersHelper do appConnector (que faz o bootstrap completo do SDK) para
    /// executar um mapeador e gravar o XML final (gabarito), ou LISTar os mapeadores do package.
    /// Uso: LayoutParserLowCodeRunner &lt;globalFolder&gt; &lt;package&gt; &lt;mapperGuid|LIST&gt; &lt;input&gt; &lt;output&gt;
    /// (rode DE DENTRO da Bin da instância instalada e licenciada).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 5)
            {
                Console.Error.WriteLine("Uso: LayoutParserLowCodeRunner <globalFolder> <package> <mapperGuid|LIST> <input> <output>");
                return 2;
            }

            string globalFolder = args[0], package = args[1], mapperGuid = args[2], inputPath = args[3], outputPath = args[4];

            try
            {
                var helper = MappersHelper.Instance;

                // Descoberta: LIST imprime os mapeadores do package (package vazio = server package da instância).
                if (string.Equals(mapperGuid, "LIST", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("[BOOT] LIST globalFolder={0} package='{1}'", globalFolder, package);
                    var mappers = helper.GetMappers(package, globalFolder);
                    Console.Error.WriteLine("Mapeadores encontrados: {0}", mappers != null ? mappers.Count : 0);
                    if (mappers != null)
                        foreach (var kv in mappers)
                            Console.Out.WriteLine("{0}\t{1}", kv.Value.IdentifierGuid, kv.Value.Name);
                    return 0;
                }

                if (!File.Exists(inputPath)) { Console.Error.WriteLine("Input nao encontrado: " + inputPath); return 4; }
                string document = File.ReadAllText(inputPath);

                Console.Error.WriteLine("[BOOT] globalFolder={0} mapper={1}", globalFolder, mapperGuid);
                string result = helper.ExecuteMappingDocumentById(mapperGuid, document, globalFolder, Path.GetFileName(inputPath));

                File.WriteAllText(outputPath, result ?? string.Empty, new UTF8Encoding(false));
                Console.Error.WriteLine("[OK] {0} chars -> {1}", (result ?? string.Empty).Length, outputPath);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FATAL] " + ex);
                return 1;
            }
        }
    }
}
