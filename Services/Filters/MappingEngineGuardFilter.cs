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
    ///
    /// <para>
    /// <b>IMPORTANTE — defesa em profundidade, não allowlist:</b> este filtro só BLOQUEIA
    /// <c>engine=sysmiddle</c> (em query OU body — qualquer um dos dois basta pra recusar). Ele NÃO
    /// valida que o valor de <c>engine</c> é um dos motores aceitos (ex.: <c>tcl</c>/<c>xslt</c>) —
    /// qualquer coisa que não seja "sysmiddle" passa implicitamente, inclusive lixo/typo. Cada
    /// controller que reusa este filtro (Slice 3/4/5) AINDA precisa da própria allowlist explícita
    /// sobre o campo <c>engine</c> do body (ver <c>MappingDraftsController.CreateDraft</c>) — não
    /// confie apenas neste filtro para aceitar implicitamente qualquer engine não-sysmiddle.
    /// </para>
    /// </summary>
    public class MappingEngineGuardFilter : IAsyncActionFilter
    {
        private const string SysmiddleEngine = "sysmiddle";

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var (queryEngine, bodyEngineBlocked) = await ResolveEnginesAsync(context);

            // "sysmiddle" explícito em QUALQUER um dos dois (query ou body) é recusado — não é
            // "query tem prioridade sobre body", é "se aparecer em qualquer lugar plausível, bloqueia".
            // Isso evita bypass via engine=xslt na query + {"engine":"sysmiddle"} no body.
            if (IsSysmiddle(queryEngine) || bodyEngineBlocked)
            {
                context.Result = new UnprocessableEntityObjectResult(new
                {
                    error = "engine=sysmiddle é somente leitura/explicação — autoria de mapeamento é via tcl/xslt."
                });
                return;
            }

            await next();
        }

        private static bool IsSysmiddle(string? engine) =>
            !string.IsNullOrWhiteSpace(engine) && string.Equals(engine.Trim(), SysmiddleEngine, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Avalia o <c>JsonElement</c> de <c>engine</c> no body considerando os formatos plausíveis:
        /// string simples (<c>"sysmiddle"</c>), array de strings (qualquer elemento batendo recusa) —
        /// e, por padrão fail-closed, qualquer outro tipo (objeto, número, bool etc.) que não seja
        /// reconhecido como string/array é tratado como recusa. "Não reconhecer o formato" não pode
        /// significar "aceitar por omissão" — o objetivo do filtro é bloquear, não validar sintaxe.
        /// </summary>
        private static bool IsEngineBlocked(JsonElement engineProp)
        {
            switch (engineProp.ValueKind)
            {
                case JsonValueKind.String:
                    return IsSysmiddle(engineProp.GetString());

                case JsonValueKind.Array:
                    foreach (var item in engineProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && IsSysmiddle(item.GetString()))
                            return true;
                    }
                    return false;

                // Fail-closed: objeto, número, bool, null etc. não são um formato reconhecido de
                // "engine" — recusa em vez de deixar passar implicitamente.
                default:
                    return true;
            }
        }

        /// <summary>
        /// Lê <c>engine</c> tanto da query string (sempre string simples) quanto do body (bufferizado
        /// sem consumir o stream original, quando JSON) — os dois são checados, não só o primeiro
        /// encontrado. O body pode trazer <c>engine</c> como string, array (ex.: <c>["xslt","sysmiddle"]</c>)
        /// ou outro tipo — a avaliação de bloqueio do body já sai pronta (<see cref="IsEngineBlocked"/>),
        /// porque só ali dá pra tratar array/objeto sem perder a informação de shape.
        /// </summary>
        private static async Task<(string? queryEngine, bool bodyEngineBlocked)> ResolveEnginesAsync(ActionExecutingContext context)
        {
            var httpContext = context.HttpContext;

            string? queryEngine = httpContext.Request.Query.TryGetValue("engine", out var queryValue)
                ? queryValue.ToString()
                : null;

            var request = httpContext.Request;
            if (!request.HasJsonContentType() || request.ContentLength is null or 0)
                return (queryEngine, false);

            request.EnableBuffering();
            request.Body.Position = 0;

            bool bodyEngineBlocked = false;
            try
            {
                using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: httpContext.RequestAborted);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("engine", out var engineProp))
                {
                    bodyEngineBlocked = IsEngineBlocked(engineProp);
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

            return (queryEngine, bodyEngineBlocked);
        }
    }
}
