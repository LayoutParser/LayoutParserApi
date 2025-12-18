namespace LayoutParserApi.Models.Validation
{
    /// <summary>
    /// Request para validar layout por GUID
    /// </summary>
    public class LayoutValidationRequest
    {
        public List<string> LayoutGuids { get; set; } = new(); // Se vazio, valida todos
        public bool ForceRevalidation { get; set; } = false; // Forçar revalidação mesmo se já validado
    }
}
