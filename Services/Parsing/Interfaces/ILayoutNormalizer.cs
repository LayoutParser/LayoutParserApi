using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Services.Parsing.Interfaces
{
    public interface ILayoutNormalizer
    {
        Layout ReestruturarLayout(Layout layoutOriginal);
        Layout ReordenarSequences(Layout layout);
    }
}


