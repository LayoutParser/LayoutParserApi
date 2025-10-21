namespace LayoutParserApi.Services.Logging
{
    public interface IRequestContextService
    {
        string GetCurrentRequestId();
        void SetCurrentRequestId(string requestId);
    }

    public class RequestContextService : IRequestContextService
    {
        private readonly AsyncLocal<string> _currentRequestId = new AsyncLocal<string>();

        public string GetCurrentRequestId()
        {
            return _currentRequestId.Value ?? Guid.NewGuid().ToString();
        }

        public void SetCurrentRequestId(string requestId)
        {
            _currentRequestId.Value = requestId;
        }
    }
}