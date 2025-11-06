namespace LayoutParserApi.Services.Generation.Interfaces
{
    /// <summary>
    /// Interface para detectar o tipo de layout
    /// </summary>
    public interface ILayoutTypeDetector
    {
        /// <summary>
        /// Detecta o tipo de layout baseado no XML do layout
        /// </summary>
        string DetectLayoutType(string layoutXml);

        /// <summary>
        /// Detecta o tipo de layout baseado no nome do layout
        /// </summary>
        string DetectLayoutTypeByName(string layoutName);
    }
}

