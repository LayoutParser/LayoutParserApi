using LayoutParserApi.Services.Interfaces;

namespace LayoutParserApi.Services.Logging
{

    public class RequestContextService : IRequestContextService
    {
        private readonly AsyncLocal<string> _currentRequestId = new AsyncLocal<string>();

        public string GetCurrentRequestId()
        {
            return _currentRequestId.Value ?? Guid.NewGuid().ToString();
        }
    }
}