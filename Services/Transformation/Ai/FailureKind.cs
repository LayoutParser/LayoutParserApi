namespace LayoutParserApi.Services.Transformation.Ai
{
    /// <summary>
    /// Classificação interna (NUNCA exposta na resposta pública da API) do motivo pelo qual um
    /// pathway síncrono (sysmiddle ou tcl-xsl) não produziu candidato. Ver
    /// docs/architecture/design-fallback-ia-automatico-2026-08-16.md §2 — a distinção decide se o
    /// fallback automático de IA deve disparar (<see cref="NotApplicable"/>) ou não
    /// (<see cref="ExecutionInfraError"/>, onde o mapper existe mas a infra falhou).
    /// </summary>
    public enum FailureKind
    {
        /// <summary>Pathway não gerou falha (produziu candidato ou nem foi tentado).</summary>
        None,

        /// <summary>
        /// Estado A — não existe mapper/heurística cadastrada para este layout. Gap real de
        /// cobertura: dispara o fallback automático de IA.
        /// </summary>
        NotApplicable,

        /// <summary>
        /// Estado B — o mapper existe e é a fonte de verdade, mas a execução falhou por motivo de
        /// infraestrutura (runner ausente, timeout, processo .exe não encontrado, config inválida).
        /// NUNCA dispara o fallback de IA — recriar algo que já existe e está correto é desperdício
        /// e risco de divergência silenciosa (§2 do desenho).
        /// </summary>
        ExecutionInfraError
    }
}
