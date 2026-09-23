namespace LayoutParserApi.Services.Llm
{
    /// <summary>
    /// Ponto único de decisão de qual <see cref="ILlmProvider"/> usar para uma chamada, com o guard
    /// de segurança central (ADR docs/architecture/adr-llm-provider-plugavel-2026-09-08.md §2.2):
    /// dado classificado <see cref="DataSensitivity.RealFiscalDocument"/> NUNCA pode ser roteado a um
    /// provider <see cref="ProviderLocality.Cloud"/> — recusa em runtime, sem downgrade silencioso
    /// para não mascarar erro de configuração. Nenhum provider de nuvem existe nesta fase (F1), mas o
    /// guard já precisa estar ativo desde o primeiro commit — é o mecanismo que F3 vai depender.
    /// </summary>
    public sealed class LlmProviderResolver
    {
        private readonly IReadOnlyList<ILlmProvider> _providers;
        private readonly ILlmProvider _defaultProvider;

        public LlmProviderResolver(IEnumerable<ILlmProvider> providers)
        {
            _providers = providers.ToList();
            if (_providers.Count == 0)
                throw new InvalidOperationException("Nenhum ILlmProvider registrado no DI — pelo menos o provider local (Ollama) é obrigatório.");

            // ✅ Default é o primeiro provider Local registrado — nesta fase (F1) só existe Ollama,
            // mas a busca explícita por Locality.Local evita que um provider de nuvem registrado no
            // futuro (F3+) vire default sem decisão explícita de quem chama.
            _defaultProvider = _providers.FirstOrDefault(p => p.Locality == ProviderLocality.Local)
                ?? _providers[0];
        }

        /// <summary>
        /// Resolve o provider a usar para a sensibilidade declarada. Lança <see cref="InvalidOperationException"/>
        /// (nunca degrada silenciosamente) se o provider resolvido for de nuvem e o dado for fiscal real.
        /// </summary>
        public ILlmProvider Resolve(DataSensitivity sensitivity, string? requestedProviderName = null)
        {
            var provider = string.IsNullOrWhiteSpace(requestedProviderName)
                ? _defaultProvider
                : _providers.FirstOrDefault(p => string.Equals(p.Name, requestedProviderName, StringComparison.OrdinalIgnoreCase))
                    ?? _defaultProvider;

            if (sensitivity == DataSensitivity.RealFiscalDocument && provider.Locality == ProviderLocality.Cloud)
            {
                throw new InvalidOperationException(
                    $"Recusado: provider de nuvem '{provider.Name}' não pode processar dado classificado como " +
                    $"{sensitivity}. Configure um provider local para este fluxo.");
            }

            return provider;
        }
    }
}
