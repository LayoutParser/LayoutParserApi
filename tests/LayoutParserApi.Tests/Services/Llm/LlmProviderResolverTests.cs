using LayoutParserApi.Services.Llm;

using Xunit;

namespace LayoutParserApi.Tests.Services.Llm
{
    /// <summary>
    /// Cobre o guard de runtime do ADR docs/architecture/adr-llm-provider-plugavel-2026-09-08.md §2.2
    /// (issue #340, F1): dado classificado <see cref="DataSensitivity.RealFiscalDocument"/> NUNCA pode
    /// ser resolvido para um provider <see cref="ProviderLocality.Cloud"/> — mesmo sem nenhum provider
    /// de nuvem real existir ainda (fake aqui simula o cenário de F3+).
    /// </summary>
    public class LlmProviderResolverTests
    {
        private sealed class FakeLocalProvider : ILlmProvider
        {
            public string Name => "ollama-local";
            public ProviderLocality Locality => ProviderLocality.Local;
            public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new LlmResponse("ok", Success: true));
            public Task<bool> IsReachableAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(true);
        }

        private sealed class FakeCloudProvider : ILlmProvider
        {
            public string Name => "fake-cloud";
            public ProviderLocality Locality => ProviderLocality.Cloud;
            public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new LlmResponse("ok", Success: true));
            public Task<bool> IsReachableAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(true);
        }

        [Fact]
        public void Resolve_ComDadoFiscalRealEProviderDeNuvem_LancaInvalidOperationException()
        {
            var resolver = new LlmProviderResolver(new ILlmProvider[] { new FakeCloudProvider() });

            var ex = Assert.Throws<InvalidOperationException>(
                () => resolver.Resolve(DataSensitivity.RealFiscalDocument, "fake-cloud"));

            Assert.Contains("fake-cloud", ex.Message);
        }

        [Fact]
        public void Resolve_ComDadoSinteticoEProviderDeNuvem_NaoLanca()
        {
            var resolver = new LlmProviderResolver(new ILlmProvider[] { new FakeLocalProvider(), new FakeCloudProvider() });

            var provider = resolver.Resolve(DataSensitivity.SyntheticOrAnonymized, "fake-cloud");

            Assert.Equal("fake-cloud", provider.Name);
        }

        [Fact]
        public void Resolve_ComDadoFiscalRealSemProviderExplicito_UsaDefaultLocal()
        {
            var resolver = new LlmProviderResolver(new ILlmProvider[] { new FakeCloudProvider(), new FakeLocalProvider() });

            var provider = resolver.Resolve(DataSensitivity.RealFiscalDocument);

            Assert.Equal(ProviderLocality.Local, provider.Locality);
        }

        [Fact]
        public void Construtor_SemProviders_Lanca()
        {
            Assert.Throws<InvalidOperationException>(() => new LlmProviderResolver(Array.Empty<ILlmProvider>()));
        }
    }
}
