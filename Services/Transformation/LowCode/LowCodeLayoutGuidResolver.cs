namespace LayoutParserApi.Services.Transformation.LowCode
{
    /// <summary>
    /// Resolve o identificador usado pelo pathway Sysmiddle. O valor recebido junto ao resultado
    /// do parse é a fonte preferencial porque o catálogo legado pode cair em <see cref="Guid.Empty"/>.
    /// </summary>
    public static class LowCodeLayoutGuidResolver
    {
        public static string? Resolve(string? requestLayoutGuid, Guid catalogLayoutGuid)
        {
            var requestValue = requestLayoutGuid?.Trim();
            if (IsValid(requestValue))
                return requestValue;

            return catalogLayoutGuid == Guid.Empty ? null : catalogLayoutGuid.ToString();
        }

        private static bool IsValid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var valueWithoutPrefix = value.StartsWith("LAY_", StringComparison.OrdinalIgnoreCase)
                ? value[4..]
                : value;

            return Guid.TryParse(valueWithoutPrefix, out var parsed) && parsed != Guid.Empty;
        }
    }
}
