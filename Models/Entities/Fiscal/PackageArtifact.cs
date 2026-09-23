namespace LayoutParserApi.Models.Entities.Fiscal
{
    /// <summary>
    /// Tipo de artefato dentro de uma <see cref="FiscalMappingPackageRevision"/>
    /// (Slice 2 — issue #229, spec §7). <c>static class</c> em vez de <c>enum</c>, mesmo padrão de
    /// <c>WorkspaceKind</c>/<c>WorkspaceRole</c> do Slice 1.
    /// </summary>
    public static class ArtifactKind
    {
        /// <summary>Amostra de origem (TXT posicional).</summary>
        public const string Sample = "sample";

        /// <summary>Layout/estrutura (XML low-code Sysmiddle).</summary>
        public const string Layout = "layout";

        /// <summary>Planilha spec-fonte (XLSX).</summary>
        public const string Spec = "spec";

        /// <summary>XSD do destino.</summary>
        public const string Xsd = "xsd";

        /// <summary>XML gabarito de saída esperado (opcional).</summary>
        public const string ExpectedXml = "expectedXml";

        /// <summary>Contexto fiscal (JSON de metadados fiscais — CFOP, natureza da operação, etc.).</summary>
        public const string FiscalContext = "fiscalContext";

        public static readonly IReadOnlyCollection<string> All = new[]
        {
            Sample, Layout, Spec, Xsd, ExpectedXml, FiscalContext
        };

        public static bool IsValid(string? kind) => kind != null && All.Contains(kind);
    }

    /// <summary>
    /// Status da inspeção de antivírus assíncrona (Slice 2 — spec §13). <c>Pending</c> no upload,
    /// atualizado para <c>Clean</c>/<c>Rejected</c> pelo job de scan em background — nunca bloqueia
    /// a resposta HTTP do upload.
    /// </summary>
    public static class InspectionStatus
    {
        public const string Pending = "pending";
        public const string Clean = "clean";
        public const string Rejected = "rejected";
    }

    /// <summary>
    /// Proveniência do CONTEÚDO de um <see cref="PackageArtifact"/> (issue #341 — Fase F2 do ADR
    /// docs/architecture/adr-llm-provider-plugavel-2026-09-08.md). Explícito e preenchido pelo
    /// analista ao anexar o arquivo — NUNCA inferido pelo tipo (<see cref="ArtifactKind"/>) ou
    /// pelo nome do arquivo. Regra central (ADR §2.3, issue #341 critério de aceite): a AUSÊNCIA
    /// deste valor (<c>null</c>/vazio) resolve para <c>RealCustomerSample</c> — nunca para
    /// <see cref="Synthetic"/> por omissão/default. Ver <see cref="ResolveSensitivity"/>.
    /// </summary>
    public static class ArtifactProvenance
    {
        /// <summary>Gerado por regra/fixture — elegível a <c>DataSensitivity.SyntheticOrAnonymized</c> (fases futuras de F3).</summary>
        public const string Synthetic = "synthetic";

        /// <summary>Amostra real de cliente anexada pelo analista — sempre <c>DataSensitivity.RealFiscalDocument</c>.</summary>
        public const string RealCustomerSample = "real_customer_sample";

        public static readonly IReadOnlyCollection<string> All = new[] { Synthetic, RealCustomerSample };

        public static bool IsValid(string? value) => value != null && All.Contains(value);

        /// <summary>
        /// Resolve a <see cref="Services.Llm.DataSensitivity"/> a partir da proveniência declarada.
        /// Fail-closed: qualquer valor ausente, vazio ou não reconhecido resolve para
        /// <see cref="Services.Llm.DataSensitivity.RealFiscalDocument"/> — só o valor EXPLÍCITO
        /// <see cref="Synthetic"/> resolve para <see cref="Services.Llm.DataSensitivity.SyntheticOrAnonymized"/>.
        /// </summary>
        public static Services.Llm.DataSensitivity ResolveSensitivity(string? provenance)
            => provenance == Synthetic
                ? Services.Llm.DataSensitivity.SyntheticOrAnonymized
                : Services.Llm.DataSensitivity.RealFiscalDocument;
    }

    /// <summary>
    /// Um artefato binário dentro de uma revisão imutável de <see cref="FiscalMappingPackage"/>.
    /// Metadado em SQL; conteúdo bruto em filesystem (<c>MLData/FiscalMappingPackages/...</c>) —
    /// nunca no log/erro (ver <c>.claude/rules/security.md</c>).
    /// </summary>
    public class PackageArtifact
    {
        public Guid ArtifactId { get; set; }

        public Guid RevisionId { get; set; }

        /// <summary>Ver <see cref="ArtifactKind"/>.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>SHA256 hex lowercase do conteúdo (mesma convenção de <c>LowCodeTransformationStore.ComputeSha256</c>).</summary>
        public string Sha256 { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        /// <summary>Nome original sanitizado (sem separador de caminho/traversal).</summary>
        public string OriginalFileName { get; set; } = string.Empty;

        /// <summary>MIME declarado pelo cliente (<c>IFormFile.ContentType</c>) — nunca confiável sozinho.</summary>
        public string MimeDeclared { get; set; } = string.Empty;

        /// <summary>MIME real, detectado por assinatura binária (magic bytes).</summary>
        public string MimeSniffed { get; set; } = string.Empty;

        public Guid UploadedByUserId { get; set; }

        public DateTimeOffset UploadedAt { get; set; }

        /// <summary>Classificação de sensibilidade do dado (livre — ex.: "fiscal-sensivel").</summary>
        public string? Classification { get; set; }

        /// <summary>Política de retenção (livre — ex.: "90d").</summary>
        public string? RetentionPolicy { get; set; }

        /// <summary>Ver <see cref="Fiscal.InspectionStatus"/>.</summary>
        public string InspectionStatus { get; set; } = Fiscal.InspectionStatus.Pending;

        /// <summary>Caminho relativo dentro do store de filesystem (não é o caminho absoluto do host).</summary>
        public string StoragePath { get; set; } = string.Empty;

        /// <summary>Ver <see cref="ArtifactProvenance"/>. <c>null</c> resolve como <see cref="ArtifactProvenance.RealCustomerSample"/> (fail-closed).</summary>
        public string? Provenance { get; set; }
    }
}
