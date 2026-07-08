using System.Xml.Linq;
using XslSynth.Core;
using XslSynth.Synthesis;

// ─────────────────────────────────────────────────────────────────────────────
// XslSynth — MVP do loop de síntese de XSLT guiada por verificador (Fase 0-2).
// Roda o loop ponta-a-ponta num mapeador sintético embutido.
//
//   dotnet run                 → usa o MockXslSynthesizer (100% OFFLINE, sem Ollama)
//   dotnet run -- --ollama     → usa o OllamaXslSynthesizer (LLM local; ver README)
//
// Arquitetura: docs/architecture/ia-xslt-synthesis.md
// ─────────────────────────────────────────────────────────────────────────────

var useOllama = args.Contains("--ollama")
    || string.Equals(Environment.GetEnvironmentVariable("XSLSYNTH_SYNTH"), "ollama",
        StringComparison.OrdinalIgnoreCase);

var sampleDir = Path.Combine(AppContext.BaseDirectory, "sample");
var mapperPath = Path.Combine(sampleDir, "mapper.xml");
var inputPath = Path.Combine(sampleDir, "input.xml");
var expectedPath = Path.Combine(sampleDir, "expected.xml");
var xsdPath = Path.Combine(sampleDir, "schema.xsd");

void Log(string line) => Console.WriteLine(line);

Log("╔══════════════════════════════════════════════════════════════════╗");
Log("║  XslSynth — síntese de XSLT guiada por verificador (MVP Fase 0-2)  ║");
Log("╚══════════════════════════════════════════════════════════════════╝");
Log("");

// Passo 1: extrair o MapperVO.
var extractor = new MapperExtractor();
var mapper = extractor.ExtractFromFile(mapperPath);
Log($"Mapeador: {mapper.Name} ({mapper.MapperGuid})");
Log($"   LinkMappings (diretos): {mapper.LinkMappings.Count} | Rules (C#): {mapper.Rules.Count}");
Log("");

var input = XDocument.Load(inputPath);
var expected = File.ReadAllText(expectedPath);

IXslSynthesizer synthesizer = useOllama
    ? new OllamaXslSynthesizer(Log)
    : new MockXslSynthesizer();
Log($"Sintetizador: {synthesizer.Name}");
Log("");

// O loop fechado.
var orchestrator = new RepairOrchestrator();
var report = await orchestrator.RunAsync(mapper, input, expected, xsdPath, synthesizer, Log);

// Relatório final + métricas (honestas).
Log("");
Log("── Métricas ──────────────────────────────────────────────────────");
Log($"   Cobertura determinística : {report.MappedFields}/{report.TotalFields} campos");
Log($"   Iterações do loop        : {report.Iterations}");
Log($"   Diffs residuais          : {report.FinalDiffs.Count}");
Log($"   XSD válido               : {(report.FinalXsd.IsValid ? "sim" : "não")}");
Log("");

if (report.Converged)
{
    Log("✅ CONVERGIU (diff == 0 e XSD válido).");
    Log("");
    Log("── XSLT final aprovado ───────────────────────────────────────────");
    Log(report.FinalXslt);
    return 0;
}

Log("❌ NÃO convergiu dentro do limite de iterações.");
Log("");
Log("── Última saída produzida ────────────────────────────────────────");
Log(report.FinalOutput);
return 1;
