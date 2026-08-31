using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LayoutParserApi.Services.Filters
{
    /// <summary>
    /// Recusa centralizada de <c>engine=sysmiddle</c> (Slice 3 — issue #230, spec §4: "Sysmiddle só
    /// executa/explica, nunca autoria"). Aplicável por atributo (<c>[ServiceFilter(typeof(...))]</c>) no
    /// nível do CONTROLLER inteiro, mesmo padrão de <see cref="AuditActionFilter"/> — reutilizável
    /// pelos Slices 4/5 futuros sem repetir o <c>if</c> em cada action.
    /// </summary>
    public class MappingEngineGuardFilter : IAsyncActionFilter
    {
        private const string SysmiddleEngine = "sysmiddle";

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var engine = await ResolveEngineAsync(context);

            // "sysmiddle" explícito em body/query é recusado. Ausência de engine não é
            // responsabilidade deste filtro — o controller decide se o payload exige o campo.
            if (!string.IsNullOrWhiteSpace(engine) && string.Equals(engine, SysmiddleEngine, StringComparison.OrdinalIgnoreCase))
            {
                context.Result = new UnprocessableEntityObjectResult(new
                {
                    error = "engine=sysmiddle é somente leitura/explicação — autoria de mapeamento é via tcl/xslt."
                });
                return;
            }

            await next();
        }

        /// <summary>Lê <c>engine</c> da query string, ou do body (bufferizado sem consumir o stream original) quando presente como JSON.</summary>
        private static async Task<string?> ResolveEngineAsync(ActionExecutingContext context)
        {
            var httpContext = context.HttpContext;

            if (httpContext.Request.Query.TryGetValue("engine", out var queryValue))
                return queryValue.ToString();

            var request = httpContext.Request;
            if (!request.HasJsonContentType() || request.ContentLength is null or 0)
                return null;

            request.EnableBuffering();
            request.Body.Position = 0;

            try
            {
                using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: httpContext.RequestAborted);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("engine", out var engineProp) &&
                    engineProp.ValueKind == JsonValueKind.String)
                {
                    return engineProp.GetString();
                }
            }
            catch (JsonException)
            {
                // Body malformado: não é responsabilidade deste filtro — o model binding padrão do
                // controller vai reportar o erro de validação.
            }
            finally
            {
                request.Body.Position = 0;
            }

            return null;
        }
    }
}
