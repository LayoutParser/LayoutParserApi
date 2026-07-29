namespace LayoutParserApi.Services.XmlAnalysis
{
    /// <summary>
    /// Configuração da integração com Ollama (LLM local). Mapeada da seção "Ollama" do appsettings.
    /// </summary>
    public class OllamaOptions
    {
        public string Url { get; set; } = "http://localhost:11434";
        public string Model { get; set; } = "deepseek-coder:6.7b";

        // ✅ Timeout específico do diagnóstico de erro de validação — análogo ao
        // LowCode:RunnerTimeoutSeconds (Services/Transformation/LowCode/LowCodeRunnerOptions).
        // Diagnóstico é uma chamada única e mais curta que geração de dados sintéticos;
        // default generoso o bastante para modelo local rodando em CPU (sem GPU no host,
        // ver decisão de topologia em memória do @lp-devops) sem deixar o request pendurado
        // indefinidamente caso o Ollama trave.
        public int DiagnosisTimeoutSeconds { get; set; } = 60;
    }
}
