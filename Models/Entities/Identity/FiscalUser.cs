namespace LayoutParserApi.Models.Entities.Identity
{
    /// <summary>
    /// Usuário interno da plataforma fiscal (Slice 1 — issue #225). O <see cref="UserId"/> é a
    /// identidade imutável: nome/e-mail são apenas atributos de exibição/auditoria e NUNCA servem de
    /// chave — trocar nome ou e-mail não pode criar um usuário novo nem duplicar workspaces.
    /// </summary>
    public class FiscalUser
    {
        public Guid UserId { get; set; }

        /// <summary>Nome de exibição (auditoria/UI apenas — não é chave de identidade).</summary>
        public string? DisplayName { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
