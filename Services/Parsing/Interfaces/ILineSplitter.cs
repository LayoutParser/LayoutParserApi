namespace LayoutParserApi.Services.Parsing.Interfaces
{
    public interface ILineSplitter
    {
        string[] SplitTextIntoLines(string text, string layoutType);
    }
}