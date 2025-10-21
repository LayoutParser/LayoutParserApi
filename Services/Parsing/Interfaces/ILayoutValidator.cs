using LayoutParserApi.Models.Entities;

namespace LayoutParserApi.Services.Parsing.Interfaces
{
    public interface ILayoutValidator
    {
        void ValidateCompleteLayout(Layout layout);
    }
}


