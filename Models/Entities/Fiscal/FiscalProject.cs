namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>
    /// Projeto fiscal mínimo (Slice 2 — issue #229), só o necessário para o
    /// <see cref="FiscalMappingPackage"/> pendurar em algo. CRUD completo de projeto fica fora de
    /// escopo — é decisão de produto separada (ver design-slice2-fiscalmappingpackage-2026-08-31.md §2).
    /// </summary>
    public class FiscalProject
    {
        public Guid ProjectId { get; set; }

        public Guid WorkspaceId { get; set; }

        public string Name { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
