using LayoutParserApi.Controllers;
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

        [Fact]
        public async Task Sysmiddle_no_body_e_recusado_mesmo_com_engine_valido_na_query()
        {
            // Achado da revisão da Quinn (Slice 3): antes do fix, engine=xslt na query fazia
            // return imediato e o filtro nunca chegava a inspecionar o body — bypass real se um
            // Slice futuro reusar o filtro sem repetir a allowlist do controller.
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(
                query: new Dictionary<string, StringValues> { ["engine"] = "xslt" },
                bodyJson: "{\"engine\":\"sysmiddle\"}");
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
        public async Task Engine_como_array_contendo_sysmiddle_e_recusado()
        {
            // Slice 6 (issue #232) — o filtro original só checava "engine" como string simples;
            // {"engine":["sysmiddle"]} passava despercebido. Cobre também o caso de array misto.
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(
                query: new Dictionary<string, StringValues>(),
                bodyJson: "{\"engine\":[\"xslt\",\"sysmiddle\"]}");
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
        public async Task Engine_como_array_sem_sysmiddle_deixa_passar()
        {
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(
                query: new Dictionary<string, StringValues>(),
                bodyJson: "{\"engine\":[\"xslt\",\"tcl\"]}");
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
        public async Task Engine_como_objeto_JSON_e_recusado_fail_closed()
        {
            // Tipo inesperado (objeto, número, bool...) não é um formato reconhecido de "engine" —
            // não reconhecer não pode significar "aceitar por omissão".
            var filtro = new MappingEngineGuardFilter();
            var context = CriarExecutingContext(
                query: new Dictionary<string, StringValues>(),
                bodyJson: "{\"engine\":{\"name\":\"xslt\"}}");
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
        public void MappingCompilationController_aplica_o_filtro_no_nivel_da_classe()
        {
            // Slice 6 (issue #232) — o design não confirmava se compile/test-runs (Slice 5) estava
            // sob o gate. Confere aqui, via reflection, que o atributo está no controller inteiro
            // (mesmo padrão do MappingDraftsController), não em cada action isoladamente.
            var atributo = typeof(MappingCompilationController)
                .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: false)
                .Cast<ServiceFilterAttribute>()
                .SingleOrDefault(a => a.ServiceType == typeof(MappingEngineGuardFilter));

            Assert.NotNull(atributo);
        }

        // --- helpers ---

        private static ActionExecutingContext CriarExecutingContext(Dictionary<string, StringValues> query, string? bodyJson = null)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.QueryString = QueryString.Create(query);

            if (bodyJson is not null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(bodyJson);
                httpContext.Request.Body = new MemoryStream(bytes);
                httpContext.Request.ContentType = "application/json";
                httpContext.Request.ContentLength = bytes.Length;
            }

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
