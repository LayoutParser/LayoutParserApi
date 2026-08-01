using LayoutParserApi.Services.Filters;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LayoutParserApi.Tests.Filters
{
    /// <summary>
    /// Chave compartilhada dos endpoints de escrita de métricas de IA. O que mais importa aqui é o
    /// comportamento FAIL-CLOSED: chave não configurada recusa a escrita, não a libera.
    /// </summary>
    public class AiMetricsIngestKeyFilterTests
    {
        private const string ChaveValida = "chave-de-teste-nao-usar-em-producao";

        [Fact]
        public void Sem_chave_configurada_a_escrita_e_recusada()
        {
            var context = CriarContexto(headerEnviado: ChaveValida);

            CriarFiltro(chaveConfigurada: null).OnAuthorization(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        }

        [Fact]
        public void Sem_header_a_escrita_e_recusada()
        {
            var context = CriarContexto(headerEnviado: null);

            CriarFiltro(ChaveValida).OnAuthorization(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        }

        [Theory]
        [InlineData("chave-errada")]
        [InlineData("chave-de-teste-nao-usar-em-producao-com-sufixo")]
        [InlineData("CHAVE-DE-TESTE-NAO-USAR-EM-PRODUCAO")]   // comparação é sensível a caixa
        [InlineData("")]
        public void Header_divergente_e_recusado(string headerEnviado)
        {
            var context = CriarContexto(headerEnviado);

            CriarFiltro(ChaveValida).OnAuthorization(context);

            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        }

        [Fact]
        public void Header_correto_deixa_a_requisicao_seguir()
        {
            var context = CriarContexto(ChaveValida);

            CriarFiltro(ChaveValida).OnAuthorization(context);

            Assert.Null(context.Result);
        }

        private static AiMetricsIngestKeyFilter CriarFiltro(string? chaveConfigurada)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [AiMetricsIngestKeyFilter.ConfigKey] = chaveConfigurada })
                .Build();

            return new AiMetricsIngestKeyFilter(configuration, NullLogger<AiMetricsIngestKeyFilter>.Instance);
        }

        private static AuthorizationFilterContext CriarContexto(string? headerEnviado)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/api/ai-metrics/generations/ingest";

            if (headerEnviado != null)
                httpContext.Request.Headers[AiMetricsIngestKeyFilter.HeaderName] = headerEnviado;

            return new AuthorizationFilterContext(
                new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>());
        }
    }
}
