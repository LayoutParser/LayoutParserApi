namespace LayoutParserApi.Models.Database
{
    public class LayoutSearchRequest
    {
        public string SearchTerm { get; set; } = "all_layouts";
        public int MaxResults { get; set; } = 1000;

        /// <summary>
        /// Issue #433: por padrão, <see cref="LayoutDatabaseService"/> só inclui layouts
        /// <c>TextPositional</c> no resultado — filtro pensado para o warmup do cache Redis (só
        /// layouts de ENTRADA/posicionais são usados no parse). Buscas por GUID específico (ex.:
        /// <c>CachedLayoutService.GetLayoutByGuidAsync</c>, usado por <c>LayoutTreeService</c> tanto
        /// para o lado origem QUANTO destino) precisam encontrar QUALQUER tipo de layout — incluindo
        /// <c>XmlLayoutVO</c> (layout de saída/destino), que sem esta flag é descartado
        /// silenciosamente, deixando <c>target.roots</c> sempre vazio para mappers cujo destino é XML.
        /// </summary>
        public bool IncludeAllLayoutTypes { get; set; }
    }
}