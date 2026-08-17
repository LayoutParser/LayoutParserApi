using XslSynth.Core;
using XslSynth.Model;
using XslSynth.Prompting;

namespace LayoutParserApi.Services.Transformation
{
    /// <summary>
    /// Cano de ligação entre o núcleo determinístico extraído para <c>XslSynth.Contracts</c>
    /// (design docs/architecture/design-xslsynth-runtime-e-reversibilidade-2026-08-16.md §1) e o
    /// runtime da API. Expõe o parser DSL→JSON estruturado (Camada 0/1) e o catálogo de funções
    /// (Camada 2) como um serviço injetável, sem trazer Ollama/RAG/HTTP junto — essas classes
    /// vivem em <c>ai/XslSynth.Contracts</c>, sem I/O externo (reflection-only via
    /// MetadataLoadContext, não executa nenhum assembly).
    ///
    /// Ainda sem consumidor no pipeline HTTP (a conexão final ao <c>/fieldMappings</c> é escopo
    /// de #140/#141, Fases 2/3) — este serviço só deixa o cano ligado no DI, pronto para uso.
    /// </summary>
    public sealed class MappingStructureService
    {
        private readonly DslStructuredParser _parser = new();

        /// <summary>Traduz uma <c>MapperRule</c> real (regra DSL Sysmiddle) para a representação
        /// estruturada <see cref="StructuredRule"/> (Camada 0 → Camada 1), 100% determinística.</summary>
        public StructuredRule ParseRule(MapperRule rule) => _parser.Parse(rule);

        /// <summary>Extrai o catálogo de funções (Camada 2) a partir da DLL de funções Sysmiddle
        /// (reflection-only, não executa o assembly). Retorna <c>null</c> se a DLL não estiver
        /// disponível no host — degrada graciosamente (dotnet-standards.md, resiliência).</summary>
        public FunctionCatalog? TryExtractFunctionCatalog(string dllPath, Action<string>? log = null)
        {
            try
            {
                return FunctionCatalog.ExtractFromDll(dllPath, log);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
