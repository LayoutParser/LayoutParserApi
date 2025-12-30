public class LineContext
{
    public string Sequence { get; set; } = "";
    public int ActualLength { get; set; }
    public int ExpectedLength { get; set; }
    public int StartPosition { get; set; }
    public int ExcessChars { get; set; }
    public string LineType { get; set; } = "";
    public string SurroundingContent { get; set; } = "";
}