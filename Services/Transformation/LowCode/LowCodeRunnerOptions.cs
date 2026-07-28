namespace LayoutParserApi.Services.Transformation.LowCode
{
    public class LowCodeRunnerOptions
    {
        public string RunnerPath { get; set; } = "";
        public string SysmiddleDir { get; set; } = "";
        public string GlobalFolder { get; set; } = "";
        public string Package { get; set; } = "";
        public string? DefaultMapperName { get; set; }

        // Seleção de mappers no banco (tbMapper)
        public int ProjectId { get; set; } = 2;
        public List<string> AllowedPackageGuids { get; set; } = new();

        // ✅ Seleção multi-candidato (LowCode-auto): quando há N>1 mapeadores genuinamente plausíveis
        // (MapperGuid distintos) para o mesmo layoutGuid, capamos em top-N pelos mesmos critérios de
        // prioridade já existentes (input match > target match > mais recente) antes de rodar em paralelo.
        public int MultiCandidateTopN { get; set; } = 4;
    }
}


