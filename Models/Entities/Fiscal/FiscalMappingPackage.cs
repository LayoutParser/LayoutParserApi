namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>
    /// Pacote de mapeamento fiscal (Slice 2 — issue #229). Pendurado num <see cref="FiscalProject"/>
    /// (por sua vez num <c>FiscalWorkspace</c> do Slice 1) — todo acesso de leitura/escrita exige
    /// membership do workspace, fail-closed, mesmo padrão do <c>WorkspacesController</c>.
    /// </summary>
    public class FiscalMappingPackage
    {
        public Guid PackageId { get; set; }

        public Guid WorkspaceId { get; set; }

        public Guid ProjectId { get; set; }

        public string Name { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>
    /// Revisão imutável de um <see cref="FiscalMappingPackage"/>. Alterar qualquer artefato cria uma
    /// revisão nova — a anterior nunca é sobrescrita (contrato que o Slice 3/<c>MappingDraft</c> exige
    /// para referenciar uma revisão exata).
    /// </summary>
    public class FiscalMappingPackageRevision
    {
        public Guid RevisionId { get; set; }

        public Guid PackageId { get; set; }

        /// <summary>Número sequencial por pacote (1, 2, 3...).</summary>
        public int RevisionNumber { get; set; }

        public Guid CreatedByUserId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
