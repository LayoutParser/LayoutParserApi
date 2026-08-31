using LayoutParserApi.Services.Filters;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace LayoutParserApi.Tests.Filters
{
    /// <summary>
    /// Slice 3 (issue #230) — <see cref="MappingEngineGuardFilter"/> recusa <c>engine=sysmiddle</c>
    /// centralizadamente (spec §4/§8: "Sysmiddle só executa/explica, nunca autoria").
    /// </summary>
    public class MappingEngineGuardFilterTests
    {
        [Fact]
        public async Task EngineSysmiddle_na_query_e_recusado_com_422()
        {
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(query: new Dictionary<string, StringValues> { ["engine"] = "sysmiddle" });
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            Assert.False(nextChamado);
            var result = Assert.IsType<UnprocessableEntityObjectResult>(context.Result);
            Assert.Equal(422, result.StatusCode);
        }

        [Fact]
        public async Task EngineSysmiddle_case_insensitive_tambem_e_recusado()
        {
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(query: new Dictionary<string, StringValues> { ["engine"] = "SysMiddle" });

            await filtro.OnActionExecutionAsync(context, () => Task.FromResult(CriarExecutedContext()));

            Assert.IsType<UnprocessableEntityObjectResult>(context.Result);
        }

        [Theory]
        [InlineData("tcl")]
        [InlineData("xslt")]
        public async Task Engine_valido_deixa_passar(string engine)
        {
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(query: new Dictionary<string, StringValues> { ["engine"] = engine });
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            Assert.True(nextChamado);
            Assert.Null(context.Result);
        }

        [Fact]
        public async Task Ausencia_de_engine_nao_e_bloqueada_pelo_filtro()
        {
            // Ausência de "engine" não é responsabilidade deste filtro — o controller decide se o
            // payload exige o campo (design §4).
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(query: new Dictionary<string, StringValues>());
            var nextChamado = false;

            await filtro.OnActionExecutionAsync(context, () =>
            {
                nextChamado = true;
                return Task.FromResult(CriarExecutedContext());
            });

            Assert.True(nextChamado);
        }

        // --- helpers ---

        private static ActionExecutingContext CriarExecutingContext(Dictionary<string, StringValues> query)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.QueryString = QueryString.Create(query);
            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

            return new ActionExecutingContext(
                actionContext,
                new List<IFilterMetadata>(),
                new Dictionary<string, object?>(),
                controller: new object());
        }

        private static ActionExecutedContext CriarExecutedContext()
        {
            var httpContext = new DefaultHttpContext();
            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
            return new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object());
        }
    }
}
