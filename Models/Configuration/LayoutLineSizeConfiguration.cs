namespace LayoutParserApi.Models.Configuration
{
    /// <summary>
    /// Configuração de tamanho de linha por LayoutGuid
    /// </summary>
    public static class LayoutLineSizeConfiguration
    {
        /// <summary>
        /// Layouts com 600 caracteres por linha
        /// </summary>
        private static readonly HashSet<string> Layouts600Chars = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LAY_e339073e-32d1-492e-ae8a-dcf6337b21a1",
            "LAY_79adf76a-4b07-428c-90d7-3c39d1296a5d",
            "LAY_ad4fb6f4-9ff5-44fd-988b-3da5ed56b22c",
            "LAY_2c5d031d-0405-466e-9ce1-f37ff2b148d5",
            "LAY_8eaa49f1-fd95-4588-b9fe-198a089d8529"
        };

        /// <summary>
        /// Layouts com 2500 caracteres por linha
        /// </summary>
        private static readonly HashSet<string> Layouts2500Chars = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LAY_c583d990-855e-42a3-8b2a-41d8fbdd48a9",
            "LAY_103024d0-ecdb-4834-ae54-690b5cd042fa",
            "LAY_8bf59c94-bb40-4bb4-b8a3-67f5971956f0",
            "LAY_ca0760b7-5de8-4026-84a7-c95dbdbadb25"
        };

        /// <summary>
        /// Obtém o tamanho de linha esperado para um LayoutGuid
        /// Retorna null se o layout não estiver na lista de layouts com cálculo específico
        /// </summary>
        public static int? GetLineSizeForLayout(string layoutGuid)
        {
            if (string.IsNullOrWhiteSpace(layoutGuid))
                return null;

            // Remover prefixo "LAY_" se existir para comparação
            var guidToCheck = layoutGuid.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase) 
                ? layoutGuid 
                : $"LAY_{layoutGuid}";

            if (Layouts600Chars.Contains(guidToCheck))
                return 600;

            if (Layouts2500Chars.Contains(guidToCheck))
                return 2500;

            return null; // Layout não está na lista de layouts com cálculo específico
        }

        /// <summary>
        /// Verifica se o layout deve ter cálculo de validação
        /// </summary>
        public static bool ShouldCalculateValidation(string layoutGuid)
        {
            return GetLineSizeForLayout(layoutGuid).HasValue;
        }

        /// <summary>
        /// Obtém todos os LayoutGuids configurados
        /// </summary>
        public static IEnumerable<string> GetAllConfiguredLayouts()
        {
            return Layouts600Chars.Concat(Layouts2500Chars);
        }
    }
}

